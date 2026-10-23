using System.Text;
using System.Text.Json;

namespace TextPolish.Core.Llm;

/// <summary>LLMが生成を終えた理由の分類。</summary>
public enum LlmFinishStatus
{
    /// <summary>正常に終了した(OpenAI互換: stop / Gemini: STOP)。</summary>
    Completed,

    /// <summary>出力上限で打ち切られた(OpenAI互換: length / Gemini: MAX_TOKENS)。</summary>
    Truncated,

    /// <summary>安全フィルタ等でブロックされた。</summary>
    Blocked,

    /// <summary>想定していない終了理由。</summary>
    Unexpected,

    /// <summary>
    /// 終了理由が応答に含まれていなかった。
    /// ローカルのOpenAI互換サーバーなど、finish_reason を返さない実装があるため、
    /// これ自体は異常として扱わない。
    /// </summary>
    Unspecified,

    /// <summary>応答の形が想定と違い、整形結果を取り出せなかった。</summary>
    Unreadable,
}

/// <summary>
/// LLMの応答から取り出した整形結果と、その採否。
///
/// 途中で打ち切られた文章をそのまま採用すると、即実行では確認の機会がないまま
/// 原文の一部を失う。<see cref="RejectionReason"/> が null でない場合は採用せず、
/// 呼び出し元は原文を変更しないこと。
/// </summary>
public sealed record LlmResponse(string? Text, string? FinishReason, LlmFinishStatus Status)
{
    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>
    /// 整形結果として採用できない理由。採用できる場合は null。
    /// 利用者にそのまま見せる文面なので、原文を変更していないことと次の手を書いている。
    /// </summary>
    public string? RejectionReason => Status switch
    {
        LlmFinishStatus.Unreadable =>
            "LLMの応答の形式が想定と異なり、整形結果を取り出せませんでした。原文は変更していません。",

        LlmFinishStatus.Truncated =>
            $"整形結果が出力途中で打ち切られました({DescribeReason()})。原文は変更していません。" +
            "MaxCompletionTokens を増やす、ReasoningEffort を下げる、選択範囲を短くする、のいずれかをお試しください。",

        LlmFinishStatus.Blocked =>
            $"LLM側で出力がブロックされました({DescribeReason()})。原文は変更していません。",

        LlmFinishStatus.Unexpected =>
            $"LLMが想定外の理由で生成を終了しました({DescribeReason()})。原文は変更していません。",

        // 終了理由が正常(または省略)でも、結果が空なら採用できない。
        // 推論モデルは出力上限に達すると推論トークンだけを消費して content が空になることがある。
        _ when !HasText =>
            "整形結果が空でした。出力上限に達した可能性があります。" +
            "MaxCompletionTokens を増やすか ReasoningEffort を下げてください。",

        _ => null,
    };

    private string DescribeReason() =>
        string.IsNullOrWhiteSpace(FinishReason) ? "終了理由の記載なし" : $"終了理由: {FinishReason}";
}

/// <summary>
/// 各プロバイダの応答JSONから整形結果と終了理由を取り出す。
///
/// HTTPが200でも生成が途中で終わっていることがあるため、本文の有無だけでなく
/// 終了理由(OpenAI互換・Azure: choices[0].finish_reason / Gemini: candidates[0].finishReason)も見る。
/// 応答の形が想定と違う場合は例外にせず <see cref="LlmFinishStatus.Unreadable"/> を返し、
/// 呼び出し元が同じ経路で利用者に知らせられるようにしている。
/// </summary>
public static class LlmResponseReader
{
    /// <summary>OpenAI互換・Azure OpenAI の Chat Completions 応答を読む。</summary>
    public static LlmResponse ReadChatCompletion(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!TryGetFirstArrayItem(doc.RootElement, "choices", out var choice))
            {
                return Unreadable();
            }

            string? text = null;
            if (choice.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object)
            {
                text = ReadString(message, "content");
            }

            var reason = ReadString(choice, "finish_reason");
            return new LlmResponse(text, reason, ClassifyChatCompletion(reason));
        }
        catch (JsonException)
        {
            return Unreadable();
        }
    }

    /// <summary>Gemini の generateContent 応答を読む。</summary>
    public static LlmResponse ReadGemini(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);

            if (!TryGetFirstArrayItem(doc.RootElement, "candidates", out var candidate))
            {
                // 入力自体が拒否された場合は candidates が空で、理由は promptFeedback に入る。
                var blockReason = doc.RootElement.TryGetProperty("promptFeedback", out var feedback) &&
                                  feedback.ValueKind == JsonValueKind.Object
                    ? ReadString(feedback, "blockReason")
                    : null;

                return string.IsNullOrWhiteSpace(blockReason)
                    ? Unreadable()
                    : new LlmResponse(null, blockReason, LlmFinishStatus.Blocked);
            }

            var reason = ReadString(candidate, "finishReason");
            return new LlmResponse(ReadGeminiText(candidate), reason, ClassifyGemini(reason));
        }
        catch (JsonException)
        {
            return Unreadable();
        }
    }

    /// <summary>
    /// candidates[0].content.parts の text を順に連結する。
    ///
    /// parts[0] だけを見ると、複数partsに分かれた応答で後半を落とす。また思考過程を
    /// 返すモデルでは parts[0] が "thought": true の思考テキストになることがあり、
    /// それを整形結果として書き戻してしまう。thought は除いて連結する。
    /// </summary>
    private static string? ReadGeminiText(JsonElement candidate)
    {
        if (!candidate.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Object ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var sb = new StringBuilder();

        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (part.TryGetProperty("thought", out var thought) &&
                thought.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            var text = ReadString(part, "text");
            if (!string.IsNullOrEmpty(text))
            {
                sb.Append(text);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private static LlmFinishStatus ClassifyChatCompletion(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return LlmFinishStatus.Unspecified;
        }

        return reason.ToLowerInvariant() switch
        {
            "stop" => LlmFinishStatus.Completed,
            "length" or "max_tokens" => LlmFinishStatus.Truncated,
            "content_filter" => LlmFinishStatus.Blocked,
            _ => LlmFinishStatus.Unexpected,
        };
    }

    private static LlmFinishStatus ClassifyGemini(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return LlmFinishStatus.Unspecified;
        }

        return reason.ToUpperInvariant() switch
        {
            "STOP" => LlmFinishStatus.Completed,
            "MAX_TOKENS" => LlmFinishStatus.Truncated,
            "SAFETY" or "RECITATION" or "BLOCKLIST" or "PROHIBITED_CONTENT"
                or "SPII" or "IMAGE_SAFETY" => LlmFinishStatus.Blocked,
            _ => LlmFinishStatus.Unexpected,
        };
    }

    private static bool TryGetFirstArrayItem(JsonElement root, string propertyName, out JsonElement item)
    {
        item = default;

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var array) ||
            array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() == 0)
        {
            return false;
        }

        item = array[0];
        return item.ValueKind == JsonValueKind.Object;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static LlmResponse Unreadable() => new(null, null, LlmFinishStatus.Unreadable);
}
