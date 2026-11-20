using TextPolish.Core.Automation;
using TextPolish.Core.Logging;

namespace TextPolish.Tests;

/// <summary>
/// 依存パッケージなしの小さなテストランナー。失敗が1件でもあれば終了コード1を返す。
///
///   TextPolish.Tests                全テストを実行
///   TextPolish.Tests --no-clipboard クリップボードを使うテストを除く(CI・リモート実行向け)
///   TextPolish.Tests --stub         テストではなく、手動確認用のLLMスタブを立てる
///   TextPolish.Tests --inspect      テストではなく、動いているアプリをUI Automationで覗く
///   TextPolish.Tests --drive palette テストではなく、動いているアプリを実際に操作する
///
/// クリップボードのテストは実際のクリップボードを書き換える。実行前の内容は
/// テスト対象の ClipboardSnapshot 自身で退避し、終了時に戻す。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // --stub はテストを実行しない。実機確認の最中に、応答の遅さや途中終了を
        // 手元で作るための道具(Harness/StubServerConsole.cs 参照)。
        if (args.Contains("--stub", StringComparer.OrdinalIgnoreCase))
        {
            return Harness.StubServerConsole.Run(args);
        }

        // --inspect もテストを実行しない。動いているアプリを UI Automation で覗くだけで、
        // クリックもキー送出もしない(Harness/UiaInspector.cs 参照)。
        if (args.Contains("--inspect", StringComparer.OrdinalIgnoreCase))
        {
            return Harness.UiaInspector.Run(args);
        }

        // --drive はテストを実行せず、動いているアプリを実際に操作する。
        // 本物のクリックが飛ぶので、誰かが同じアプリで手作業の確認をしている間は
        // 流さないこと(Harness/UiaDriver.cs 参照)。
        if (args.Contains("--drive", StringComparer.OrdinalIgnoreCase))
        {
            return Harness.UiaDriver.Run(args);
        }

        var includeClipboard = !args.Contains("--no-clipboard", StringComparer.OrdinalIgnoreCase);

        var tests = new List<(string Name, Action Body)>();
        tests.AddRange(SettingsTests.All());
        tests.AddRange(OperationGateTests.All());
        tests.AddRange(LlmResponseTests.All());
        tests.AddRange(LlmErrorLoggingTests.All());
        tests.AddRange(LlmStubServerTests.All());
        tests.AddRange(FileLoggerTests.All());
        tests.AddRange(PolishResultTests.All());
        tests.AddRange(PasteTargetTests.All());
        tests.AddRange(PromptTests.All());
        tests.AddRange(InstructionHistoryTests.All());
        if (includeClipboard)
        {
            tests.AddRange(ClipboardTests.All());
        }

        var userClipboard = includeClipboard ? ClipboardSnapshot.Capture(new NullLogger()) : null;
        var failures = new List<string>();

        try
        {
            foreach (var (name, body) in tests)
            {
                try
                {
                    body();
                    Console.WriteLine($"  OK    {name}");
                }
                catch (Exception ex)
                {
                    var detail = $"{ex.GetType().Name}: {ex.Message}";
                    failures.Add($"{name} / {detail}");
                    Console.WriteLine($"  FAIL  {name}");
                    Console.WriteLine($"        {detail}");
                }
            }
        }
        finally
        {
            userClipboard?.WriteToClipboard();
        }

        Console.WriteLine();
        Console.WriteLine($"{tests.Count - failures.Count}/{tests.Count} 件成功");
        ReportToGitHubActions(failures);
        return failures.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// GitHub Actions で流れているときに、失敗したテスト名を注記(annotation)として出す。
    ///
    /// 実行ログは公開リポジトリでも**サインインしないと読めない**。一方で注記は
    /// コミットのチェック画面にそのまま出るため、「どのテストが落ちたか」を
    /// ログを開かずに追えるようにしておく。メールの通知からも辿れる。
    ///
    /// 外部パッケージは使わない。Actions が決めた出力形式に1行ずつ書くだけ。
    /// Actions の外(手元での実行)では何も出さない。
    /// </summary>
    private static void ReportToGitHubActions(List<string> failures)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" || failures.Count == 0)
        {
            return;
        }

        foreach (var failure in failures)
        {
            // 改行とコロンは書式を壊すので落とす(注記は1件1行)。
            var line = failure
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Replace("::", ": ");
            Console.WriteLine($"::error title=テスト失敗::{line}");
        }
    }
}

internal static class Assert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new Exception(message);
        }
    }

    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new Exception($"{what}: 期待値 <{expected}> / 実際 <{actual}>");
        }
    }
}

/// <summary>警告・エラーを記録するだけのロガー。</summary>
internal sealed class NullLogger : IAppLogger
{
    public List<string> Warnings { get; } = new();

    public void Info(string message)
    {
    }

    public void Warn(string message) => Warnings.Add(message);

    public void Error(string message, Exception? ex = null) => Warnings.Add(message);
}
