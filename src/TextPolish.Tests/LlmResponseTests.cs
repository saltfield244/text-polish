using TextPolish.Core.Llm;

namespace TextPolish.Tests;

/// <summary>
/// LLMの応答から整形結果を取り出すときの採否(<see cref="LlmResponseReader"/>)。
///
/// HTTPが200でも生成が途中で終わっていることがある。即実行は確認なしで書き戻すため、
/// 打ち切られた文章を採用すると原文の一部が失われる。終了理由を見て弾けているかを確かめる。
/// ネットワークもデスクトップも使わないため <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class LlmResponseTests
{
    public static IEnumerable<(string, Action)> All()
    {
        yield return ("応答判定: stop は採用する", ChatCompletionStopIsAccepted);
        yield return ("応答判定: length は採用しない(出力上限)", ChatCompletionLengthIsRejected);
        yield return ("応答判定: content_filter は採用しない", ChatCompletionContentFilterIsRejected);
        yield return ("応答判定: 知らない終了理由は採用しない", ChatCompletionUnknownReasonIsRejected);
        yield return ("応答判定: finish_reason がなくても本文があれば採用する", ChatCompletionMissingReasonIsAccepted);
        yield return ("応答判定: finish_reason がなく本文も空なら採用しない", ChatCompletionEmptyWithoutReasonIsRejected);
        yield return ("応答判定: stop でも本文が空なら採用しない", ChatCompletionEmptyWithStopIsRejected);
        yield return ("応答判定: 本文が空でも length なら上限として知らせる", ChatCompletionEmptyWithLengthReportsLimit);
        yield return ("応答判定: choices が無い・壊れたJSONは採用しない", ChatCompletionMalformedIsRejected);
        yield return ("応答判定: Gemini の STOP は採用する", GeminiStopIsAccepted);
        yield return ("応答判定: Gemini の MAX_TOKENS は採用しない", GeminiMaxTokensIsRejected);
        yield return ("応答判定: Gemini の SAFETY は採用しない", GeminiSafetyIsRejected);
        yield return ("応答判定: Gemini は複数partsを連結する", GeminiConcatenatesParts);
        yield return ("応答判定: Gemini の思考パートは結果に混ぜない", GeminiSkipsThoughtParts);
        yield return ("応答判定: Gemini の入力ブロックは採用しない", GeminiPromptBlockIsRejected);
        yield return ("応答判定: Gemini の candidates 無し・壊れたJSONは採用しない", GeminiMalformedIsRejected);
    }

    private static void ChatCompletionStopIsAccepted()
    {
        var parsed = LlmResponseReader.ReadChatCompletion(ChatCompletion("整形後の文章です。", "stop"));

        Assert.Equal(LlmFinishStatus.Completed, parsed.Status, "終了理由の分類");
        Assert.Equal("整形後の文章です。", parsed.Text, "整形結果");
        Assert.Equal(null, parsed.RejectionReason, "採用できない理由");
    }

    private static void ChatCompletionLengthIsRejected()
    {
        var parsed = LlmResponseReader.ReadChatCompletion(ChatCompletion("途中まで整形された文章", "length"));

        Assert.Equal(LlmFinishStatus.Truncated, parsed.Status, "終了理由の分類");

        // 本文はあるが採用しない。原文を変更しないことと、次の手を文面に含める。
        Assert.True(parsed.HasText, "本文は取り出せている");
        var reason = parsed.RejectionReason;
        Assert.True(reason is not null, "採用できない理由が返る");
        Assert.True(reason!.Contains("原文は変更していません"), $"原文を変えない旨を伝える: {reason}");
        Assert.True(reason.Contains("MaxCompletionTokens"), $"対処方法を伝える: {reason}");
    }

    private static void ChatCompletionContentFilterIsRejected()
    {
        var parsed = LlmResponseReader.ReadChatCompletion(ChatCompletion("", "content_filter"));

        Assert.Equal(LlmFinishStatus.Blocked, parsed.Status, "終了理由の分類");
        Assert.True(parsed.RejectionReason?.Contains("ブロック") == true, "ブロックされた旨を伝える");
    }

    private static void ChatCompletionUnknownReasonIsRejected()
    {
        var parsed = LlmResponseReader.ReadChatCompletion(ChatCompletion("何かの出力", "tool_calls"));

        Assert.Equal(LlmFinishStatus.Unexpected, parsed.Status, "終了理由の分類");
        Assert.True(parsed.RejectionReason?.Contains("tool_calls") == true, "終了理由を文面に含める");
    }

    private static void ChatCompletionMissingReasonIsAccepted()
    {
        // ローカルのOpenAI互換サーバーなど、finish_reason を返さない実装がある。
        const string json = """
            { "choices": [ { "message": { "role": "assistant", "content": "整形後の文章" } } ] }
            """;

        var parsed = LlmResponseReader.ReadChatCompletion(json);

        Assert.Equal(LlmFinishStatus.Unspecified, parsed.Status, "終了理由の分類");
        Assert.Equal("整形後の文章", parsed.Text, "整形結果");
        Assert.Equal(null, parsed.RejectionReason, "本文があれば採用する");
    }

    private static void ChatCompletionEmptyWithoutReasonIsRejected()
    {
        const string json = """
            { "choices": [ { "message": { "role": "assistant", "content": "" } } ] }
            """;

        var parsed = LlmResponseReader.ReadChatCompletion(json);

        Assert.True(parsed.RejectionReason?.Contains("空") == true, "空の結果は採用しない");
    }

    private static void ChatCompletionEmptyWithStopIsRejected()
    {
        // 推論モデルは出力上限に達すると、終了理由が stop でも content が空になることがある。
        var parsed = LlmResponseReader.ReadChatCompletion(ChatCompletion("   ", "stop"));

        Assert.Equal(LlmFinishStatus.Completed, parsed.Status, "終了理由の分類");
        Assert.True(parsed.RejectionReason is not null, "空白だけの結果も採用しない");
    }

    private static void ChatCompletionEmptyWithLengthReportsLimit()
    {
        var parsed = LlmResponseReader.ReadChatCompletion(ChatCompletion("", "length"));

        // 空かつ上限打ち切りの場合は、空であることより上限に達したことを伝えるほうが役に立つ。
        Assert.Equal(LlmFinishStatus.Truncated, parsed.Status, "終了理由の分類");
        Assert.True(parsed.RejectionReason?.Contains("打ち切られました") == true, "上限で打ち切られた旨を伝える");
    }

    private static void ChatCompletionMalformedIsRejected()
    {
        foreach (var json in new[] { "{}", """{ "choices": [] }""", """{ "error": { "message": "bad" } }""", "これはJSONではない" })
        {
            var parsed = LlmResponseReader.ReadChatCompletion(json);

            Assert.Equal(LlmFinishStatus.Unreadable, parsed.Status, $"分類({json})");
            Assert.True(parsed.RejectionReason?.Contains("形式") == true, $"形式が異常である旨を伝える({json})");
        }
    }

    private static void GeminiStopIsAccepted()
    {
        var parsed = LlmResponseReader.ReadGemini(Gemini("整形後の文章です。", "STOP"));

        Assert.Equal(LlmFinishStatus.Completed, parsed.Status, "終了理由の分類");
        Assert.Equal("整形後の文章です。", parsed.Text, "整形結果");
        Assert.Equal(null, parsed.RejectionReason, "採用できない理由");
    }

    private static void GeminiMaxTokensIsRejected()
    {
        var parsed = LlmResponseReader.ReadGemini(Gemini("途中まで整形され", "MAX_TOKENS"));

        Assert.Equal(LlmFinishStatus.Truncated, parsed.Status, "終了理由の分類");
        Assert.True(parsed.RejectionReason?.Contains("原文は変更していません") == true, "原文を変えない旨を伝える");
    }

    private static void GeminiSafetyIsRejected()
    {
        var parsed = LlmResponseReader.ReadGemini(Gemini("", "SAFETY"));

        Assert.Equal(LlmFinishStatus.Blocked, parsed.Status, "終了理由の分類");
        Assert.True(parsed.RejectionReason is not null, "採用しない");
    }

    private static void GeminiConcatenatesParts()
    {
        // partsが複数に分かれることがある。先頭だけを見ると後半を落とす。
        const string json = """
            {
              "candidates": [ {
                "content": { "parts": [ { "text": "前半です。" }, { "text": "後半です。" } ] },
                "finishReason": "STOP"
              } ]
            }
            """;

        var parsed = LlmResponseReader.ReadGemini(json);

        Assert.Equal("前半です。後半です。", parsed.Text, "連結された整形結果");
        Assert.Equal(null, parsed.RejectionReason, "採用できない理由");
    }

    private static void GeminiSkipsThoughtParts()
    {
        // 思考過程を返すモデルでは parts[0] が思考テキストになることがある。
        // それを整形結果として書き戻してはいけない。
        const string json = """
            {
              "candidates": [ {
                "content": { "parts": [
                  { "text": "まず文体を判断する…", "thought": true },
                  { "text": "整形後の文章です。" }
                ] },
                "finishReason": "STOP"
              } ]
            }
            """;

        var parsed = LlmResponseReader.ReadGemini(json);

        Assert.Equal("整形後の文章です。", parsed.Text, "思考パートを除いた整形結果");
    }

    private static void GeminiPromptBlockIsRejected()
    {
        const string json = """
            { "promptFeedback": { "blockReason": "SAFETY" } }
            """;

        var parsed = LlmResponseReader.ReadGemini(json);

        Assert.Equal(LlmFinishStatus.Blocked, parsed.Status, "終了理由の分類");
        Assert.True(parsed.RejectionReason?.Contains("SAFETY") == true, "ブロック理由を文面に含める");
    }

    private static void GeminiMalformedIsRejected()
    {
        foreach (var json in new[] { "{}", """{ "candidates": [] }""", """{ "candidates": [ { "finishReason": "STOP" } ] }""", "壊れている" })
        {
            var parsed = LlmResponseReader.ReadGemini(json);

            Assert.True(parsed.RejectionReason is not null, $"採用しない({json})");
        }
    }

    private static string ChatCompletion(string content, string finishReason) => $$"""
        {
          "choices": [ {
            "index": 0,
            "message": { "role": "assistant", "content": {{Json(content)}} },
            "finish_reason": {{Json(finishReason)}}
          } ]
        }
        """;

    private static string Gemini(string text, string finishReason) => $$"""
        {
          "candidates": [ {
            "content": { "parts": [ { "text": {{Json(text)}} } ], "role": "model" },
            "finishReason": {{Json(finishReason)}}
          } ]
        }
        """;

    private static string Json(string value) => System.Text.Json.JsonSerializer.Serialize(value);
}
