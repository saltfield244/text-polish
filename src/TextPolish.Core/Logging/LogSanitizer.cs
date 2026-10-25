using System.Text;
using System.Text.RegularExpressions;

namespace TextPolish.Core.Logging;

/// <summary>
/// ログへ書き出す文字列から、残すべきでない情報を落とすための共通処理。
///
/// ログは <c>%AppData%\TextPolish\logs\</c> に平文で残り、利用者が不具合報告に添付することもある。
/// 整形対象の文章(利用者が書いた文)とAPIキーは、障害調査には不要なのでログへ入れない。
/// 「入れない」のは呼び出し側の責任だが、取りこぼしても最後に落ちるよう
/// <see cref="FileLogger"/> の書き出し直前でも <see cref="Redact"/> を通している。
/// </summary>
public static class LogSanitizer
{
    /// <summary>秘匿値を置き換える目印。ログを読む人が「落とした」と分かるようにする。</summary>
    public const string Redacted = "(秘匿)";

    /// <summary>識別子として想定外の値だった場合の目印。</summary>
    public const string Unexpected = "(想定外の形式)";

    // URLのクエリに載るAPIキー(Gemini の AI Studio 経路は ?key=... で渡す)。
    private static readonly Regex KeyQueryParam = new(
        @"(?<name>[?&](?:key|api[_-]?key|access_token|token)=)(?<value>[^&\s""']+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ヘッダー名を書いたうえで値を続けている形(例外メッセージや自前のログ文で起こりうる)。
    private static readonly Regex KeyHeaderValue = new(
        @"(?<name>\b(?:Bearer|api-key|Authorization)\s*[:=]?\s+)(?<value>[A-Za-z0-9._\-]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // 各プロバイダのAPIキーに特徴的な先頭文字列。単体で文中に現れても落とす。
    private static readonly Regex KnownKeyShapes = new(
        @"\b(?:sk-[A-Za-z0-9._\-]{8,}|AIza[A-Za-z0-9._\-]{10,}|ya29\.[A-Za-z0-9._\-]{10,})",
        RegexOptions.Compiled);

    /// <summary>
    /// ログ1行からAPIキーらしい値を落とす。
    /// 整形対象の文章そのものは形が決まっておらず機械的に判別できないため、ここでは落とせない。
    /// 文章をログへ渡さないのは呼び出し側で守る。
    /// </summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var result = KeyQueryParam.Replace(text, m => m.Groups["name"].Value + Redacted);
        result = KeyHeaderValue.Replace(result, m => m.Groups["name"].Value + Redacted);
        result = KnownKeyShapes.Replace(result, Redacted);
        return result;
    }

    /// <summary>
    /// 例外を「種別と短いメッセージ」だけに要約する。
    ///
    /// <see cref="Exception.ToString"/> はスタックトレースのほかに、サーバーが返した本文を
    /// そのままメッセージに含む例外(JSONの解析失敗など)も丸ごと書き出してしまう。
    /// 障害調査に効くのは例外の種別と連鎖なので、そこだけを残す。
    /// </summary>
    public static string DescribeException(Exception? ex)
    {
        if (ex is null)
        {
            return "(例外なし)";
        }

        var sb = new StringBuilder();

        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (sb.Length > 0)
            {
                sb.Append(" <- ");
            }

            sb.Append(current.GetType().FullName);

            var message = Truncate(Redact(current.Message).ReplaceLineEndings(" "), 200);
            if (!string.IsNullOrWhiteSpace(message))
            {
                sb.Append(": ").Append(message);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// APIが返した識別子(finish_reason、エラーコード等)を、そのまま残して安全な形か検査する。
    ///
    /// これらは本来 "stop" / "MAX_TOKENS" / "invalid_api_key" のような短い定数だが、
    /// 値を決めるのはサーバー側なので、長い文やJSONが入っていた場合はログへ流さない。
    /// </summary>
    public static string DescribeToken(string? value, int maxLength = 64)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "(なし)";
        }

        if (value.Length > maxLength)
        {
            return Unexpected;
        }

        foreach (var c in value)
        {
            var ok = char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':' or '/';
            if (!ok)
            {
                return Unexpected;
            }
        }

        return value;
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength] + "…";
}
