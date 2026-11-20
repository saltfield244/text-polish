using System.IO;
using System.Text.RegularExpressions;
using TextPolish.Core.Logging;

namespace TextPolish.Tests.Harness;

/// <summary>
/// ログファイルの中身を機械的に照合するための読み取り口。
///
/// 実機確認では毎回この照合を手でやってきた(F6 では原文の断片8種と
/// <c>Body=</c> / <c>sk-</c> / <c>Bearer</c> / <c>?key=</c> / <c>Authorization</c> を
/// 1件ずつ grep し、全53行が時刻で始まることを数えた)。同じ判断を繰り返すので、
/// ここに固めて、手順書の「ログを開いて確認する」をコードに置き換えられるようにする。
///
/// 読むのは <see cref="FileLogger"/> が書いた1行1件のログ。
/// 書き込み中のファイルを読むため、共有を許して開く。
/// </summary>
internal sealed class LogFileAssert
{
    /// <summary>ログ1行の先頭にある時刻。これで始まらない行は、改行で記録が割れた印。</summary>
    private static readonly Regex LineStart = new(
        @"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} \[(INFO|WARN|ERROR)\] ",
        RegexOptions.Compiled);

    /// <summary>
    /// APIキーが混入したときに現れる形。F6 で手で grep していたもの。
    ///
    /// 「名前だけ」では判定できない。<see cref="LogSanitizer.Redact"/> は値を
    /// <see cref="LogSanitizer.Redacted"/> に置き換えて名前は残すため、
    /// <c>?key=</c> や <c>Bearer </c> を含むこと自体は正常な記録でも起きる。
    /// そこで「名前のうしろに、秘匿の目印でない値が続いている」ものだけを漏えいとみなす。
    /// </summary>
    private static readonly Regex[] KeyShapes =
    {
        // URLのクエリやヘッダー名のうしろに生の値が続く形。
        new(@"[?&](?:key|api[_-]?key|access_token|token)=(?!\(秘匿\))[^&\s""']+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\b(?:Bearer|api-key|Authorization)\s*[:=]?\s+(?!\(秘匿\))[A-Za-z0-9._\-]{8,}",
            RegexOptions.IgnoreCase | RegexOptions.Compiled),

        // 各プロバイダのキーに特徴的な先頭文字列。単体で文中に現れても漏えい。
        new(@"\b(?:sk-[A-Za-z0-9._\-]{8,}|AIza[A-Za-z0-9._\-]{10,}|ya29\.[A-Za-z0-9._\-]{10,})",
            RegexOptions.Compiled),
    };

    private readonly string _path;

    public LogFileAssert(string path)
    {
        _path = path;
    }

    /// <summary>ログの行。読むたびにファイルを開き直す(常駐アプリが書き足すため)。</summary>
    public IReadOnlyList<string> Lines => ReadLines();

    /// <summary>そのファイルが今あるか。</summary>
    public bool Exists => File.Exists(_path);

    /// <summary>ログの置き場所。既定は %AppData%\TextPolish\logs。</summary>
    public static string DefaultLogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextPolish", "logs");

    /// <summary>その日のログファイル。<see cref="FileLogger"/> の命名に合わせる。</summary>
    public static LogFileAssert ForDate(DateTime at, string? directory = null) =>
        new(Path.Combine(directory ?? DefaultLogDirectory, $"app-{at:yyyyMMdd}.log"));

    /// <summary>今日のログファイル。</summary>
    public static LogFileAssert Today(string? directory = null) => ForDate(DateTime.Now, directory);

    /// <summary>その文字列を含む行。</summary>
    public IReadOnlyList<string> Containing(string text) =>
        ReadLines().Where(l => l.Contains(text, StringComparison.Ordinal)).ToList();

    /// <summary>その水準(INFO / WARN / ERROR)の行数。</summary>
    public int CountOfLevel(string level) =>
        ReadLines().Count(l => l.Contains($"[{level}] ", StringComparison.Ordinal));

    /// <summary>いま何行あるか。操作の前後で差分だけを見るために使う。</summary>
    public int LineCount => ReadLines().Count;

    /// <summary>
    /// <paramref name="from"/> 行目より後ろの行だけを返す。
    /// 操作の前に <see cref="LineCount"/> を取っておけば、その操作が書いた分だけを見られる。
    /// 常駐アプリのログは前日からの記録が積み上がっているため、全体を見ると判断を誤る。
    /// </summary>
    public IReadOnlyList<string> LinesAfter(int from)
    {
        var lines = ReadLines();
        return from >= lines.Count ? Array.Empty<string>() : lines.Skip(from).ToList();
    }

    /// <summary>
    /// その文字列を含む行が現れるまで待つ。現れた行を返し、時間内に出なければ null。
    ///
    /// GUIを動かすテストでは、操作してから実際にログへ書かれるまでに間がある。
    /// 固定待ちにすると遅いか不安定かのどちらかになるので、短い間隔で見に行く。
    /// </summary>
    public string? WaitForLine(string text, int timeoutMs = 10000, int from = 0, int pollMs = 100)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            var hit = LinesAfter(from)
                .FirstOrDefault(l => l.Contains(text, StringComparison.Ordinal));

            if (hit is not null)
            {
                return hit;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return null;
            }

            Thread.Sleep(pollMs);
        }
    }

    /// <summary>
    /// 整形を依頼した文章の断片が1つも残っていないことを確かめる。
    ///
    /// ログは平文で残り、不具合報告に添付されることもある。
    /// <see cref="LogSanitizer.Redact"/> は散文を機械的に落とせないため、
    /// 「呼び出し側が渡さない」ことをこちらで押さえる必要がある。
    /// </summary>
    public void AssertNoFragmentOf(IEnumerable<string> fragments, int from = 0)
    {
        var lines = LinesAfter(from);

        foreach (var fragment in fragments)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                continue;
            }

            var hit = lines.FirstOrDefault(l => l.Contains(fragment, StringComparison.Ordinal));
            if (hit is not null)
            {
                throw new Exception($"原文の断片「{fragment}」がログに残っています: {hit}");
            }
        }
    }

    /// <summary>
    /// APIキーらしい値が残っていないことを確かめる。
    /// 秘匿済み(<see cref="LogSanitizer.Redacted"/> に置き換わっている)行は通す。
    /// </summary>
    public void AssertNoApiKeyShape(int from = 0)
    {
        foreach (var line in LinesAfter(from))
        {
            foreach (var shape in KeyShapes)
            {
                var hit = shape.Match(line);
                if (hit.Success)
                {
                    throw new Exception($"APIキーらしい値「{hit.Value}」がログに残っています: {line}");
                }
            }
        }
    }

    /// <summary>
    /// すべての行が時刻で始まることを確かめる。
    /// 1件が複数行に割れていると、あとから1行ずつ読めなくなる。
    /// </summary>
    public void AssertEveryLineIsOneRecord(int from = 0)
    {
        foreach (var line in LinesAfter(from))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!LineStart.IsMatch(line))
            {
                throw new Exception($"時刻で始まらない行があります(記録が改行で割れている): {line}");
            }
        }
    }

    /// <summary>ERROR が1件も無いことを確かめる。中止やタイムアウトは異常ではない。</summary>
    public void AssertNoError(int from = 0)
    {
        var errors = LinesAfter(from)
            .Where(l => l.Contains("[ERROR] ", StringComparison.Ordinal))
            .ToList();

        if (errors.Count > 0)
        {
            throw new Exception($"ERROR が {errors.Count} 件あります: {errors[0]}");
        }
    }

    private List<string> ReadLines()
    {
        if (!File.Exists(_path))
        {
            return new List<string>();
        }

        // 常駐アプリが書いている最中のファイルを読むため、読み書きの共有を許す。
        using var stream = new FileStream(
            _path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);

        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines;
    }
}
