using System.IO;
using TextPolish.Core.Logging;

namespace TextPolish.Tests;

/// <summary>
/// ログファイルそのものの扱い(<see cref="FileLogger"/>)。
///
/// このアプリは常駐するため、PCを起動したまま日付をまたぐのが普通の使い方になる。
/// 起動時に書き込み先を決めてしまうと何日分も初日のファイルへ積み上がり、
/// 「いつ何が起きたか」をログから辿れなくなる。
/// ネットワークもデスクトップも使わないため <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class FileLoggerTests
{
    public static IEnumerable<(string, Action)> All()
    {
        yield return ("ログ出力: 日付が変わると別のファイルへ書く", DayChangeStartsNewFile);
        yield return ("ログ出力: 書き込み先は現在の日付を指す", LogFilePathFollowsClock);
        yield return ("ログ出力: 改行を含む文面も1行に収める", MultilineMessageStaysOnOneLine);
    }

    private static void DayChangeStartsNewFile()
    {
        WithTempDirectory(dir =>
        {
            var now = new DateTime(2026, 10, 25, 23, 59, 30);
            var logger = new FileLogger(dir, () => now);

            logger.Info("日付が変わる前の記録");

            now = new DateTime(2026, 10, 26, 0, 0, 5);
            logger.Info("日付が変わった後の記録");

            var before = Path.Combine(dir, "app-20261025.log");
            var after = Path.Combine(dir, "app-20261026.log");

            Assert.True(File.Exists(before), "前日のファイルがない");
            Assert.True(File.Exists(after), "日付が変わった後のファイルがない");

            var beforeText = File.ReadAllText(before);
            var afterText = File.ReadAllText(after);

            Assert.True(beforeText.Contains("日付が変わる前の記録"), "前日の記録が入っていない");
            Assert.True(
                !beforeText.Contains("日付が変わった後の記録"),
                $"日付が変わった後の記録が前日のファイルに入っている: {beforeText}");
            Assert.True(afterText.Contains("日付が変わった後の記録"), "当日の記録が入っていない");

            // 行頭の時刻も、その行が入ったファイルの日付と一致している必要がある。
            Assert.True(afterText.StartsWith("2026-10-26", StringComparison.Ordinal), $"行の時刻: {afterText}");
        });
    }

    private static void LogFilePathFollowsClock()
    {
        WithTempDirectory(dir =>
        {
            var now = new DateTime(2026, 1, 2, 10, 0, 0);
            var logger = new FileLogger(dir, () => now);

            Assert.Equal(Path.Combine(dir, "app-20260102.log"), logger.LogFilePath, "初日の書き込み先");

            now = now.AddDays(3);

            Assert.Equal(Path.Combine(dir, "app-20260105.log"), logger.LogFilePath, "3日後の書き込み先");
            Assert.Equal(dir, logger.LogDirectory, "出力先フォルダ");
        });
    }

    private static void MultilineMessageStaysOnOneLine()
    {
        WithTempDirectory(dir =>
        {
            var logger = new FileLogger(dir, () => new DateTime(2026, 3, 4, 5, 6, 7));

            // 例外の文面は改行を含む。1件が複数行になると、あとから1行ずつ読めなくなる。
            logger.Error("1行目" + Environment.NewLine + "2行目\n3行目");

            var lines = File.ReadAllLines(logger.LogFilePath);

            Assert.Equal(1, lines.Length, "出力された行数");
            Assert.True(lines[0].Contains("1行目 2行目 3行目"), $"改行が落ちていない: {lines[0]}");
        });
    }

    private static void WithTempDirectory(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TextPolishTests-" + Guid.NewGuid().ToString("N"));

        try
        {
            body(dir);
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 後片付けの失敗はテスト結果に影響させない。
            }
        }
    }
}
