using System.Text;
using System.Text.Json;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Llm;

/// <summary>
/// LLMの応答本文から、ログへ残して差し支えない診断情報だけを取り出す。
///
/// 応答本文には利用者が整形・翻訳を依頼した文章が入りうる(成功応答はもちろん、
/// エラー応答でも入力の一部を反映して返すサービスがある)。本文の全文をログへ書くと
/// その文章が <c>%AppData%\TextPolish\logs\</c> に平文で残るため、本文は書かずに
/// 「どのくらいの長さだったか」「エラー種別は何か」だけを残す。
///
/// 残すのは <c>error.type</c> / <c>error.code</c> / <c>error.status</c> のような短い識別子に限り、
/// 人間向けの文である <c>error.message</c> は入力を引用することがあるので残さない。
/// </summary>
public static class LlmErrorSummary
{
    /// <summary>
    /// 本文の長さと、取り出せたエラー識別子を1行に要約する。本文そのものは含まない。
    /// </summary>
    public static string Describe(string? body)
    {
        var sb = new StringBuilder();
        sb.Append("BodyChars=").Append(body?.Length ?? 0);

        if (string.IsNullOrWhiteSpace(body))
        {
            return sb.ToString();
        }

        try
        {
            using var doc = JsonDocument.Parse(body);

            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                sb.Append(" BodyShape=JSONだが想定の形ではない");
                return sb.ToString();
            }

            // OpenAI互換・Azure・Gemini はいずれも error オブジェクトにエラー種別を入れる。
            if (doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.Object)
            {
                AppendToken(sb, "ErrorType", error, "type");
                AppendToken(sb, "ErrorCode", error, "code");
                AppendToken(sb, "ErrorStatus", error, "status");
            }
            else
            {
                sb.Append(" BodyShape=errorオブジェクトなし");
            }
        }
        catch (JsonException)
        {
            // 502のHTMLやプロキシの平文など、JSONでない本文が返ることもある。
            // 中身は分からないのでログへは出さず、JSONでなかったことだけを残す。
            sb.Append(" BodyShape=JSONではない");
        }

        return sb.ToString();
    }

    private static void AppendToken(StringBuilder sb, string label, JsonElement error, string propertyName)
    {
        if (!error.TryGetProperty(propertyName, out var value))
        {
            return;
        }

        // code は文字列(OpenAI互換)と数値(Gemini)の両方がある。
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };

        if (text is null)
        {
            return;
        }

        sb.Append(' ').Append(label).Append('=').Append(LogSanitizer.DescribeToken(text));
    }
}
