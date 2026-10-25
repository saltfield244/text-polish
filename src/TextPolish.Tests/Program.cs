using TextPolish.Core.Automation;
using TextPolish.Core.Logging;

namespace TextPolish.Tests;

/// <summary>
/// 依存パッケージなしの小さなテストランナー。失敗が1件でもあれば終了コード1を返す。
///
///   TextPolish.Tests                全テストを実行
///   TextPolish.Tests --no-clipboard クリップボードを使うテストを除く(CI・リモート実行向け)
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
        var includeClipboard = !args.Contains("--no-clipboard", StringComparer.OrdinalIgnoreCase);

        var tests = new List<(string Name, Action Body)>();
        tests.AddRange(SettingsTests.All());
        tests.AddRange(OperationGateTests.All());
        tests.AddRange(LlmResponseTests.All());
        tests.AddRange(LlmErrorLoggingTests.All());
        tests.AddRange(FileLoggerTests.All());
        tests.AddRange(PolishResultTests.All());
        tests.AddRange(PasteTargetTests.All());
        tests.AddRange(PromptTests.All());
        if (includeClipboard)
        {
            tests.AddRange(ClipboardTests.All());
        }

        var userClipboard = includeClipboard ? ClipboardSnapshot.Capture(new NullLogger()) : null;
        var failed = 0;

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
                    failed++;
                    Console.WriteLine($"  FAIL  {name}");
                    Console.WriteLine($"        {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        finally
        {
            userClipboard?.WriteToClipboard();
        }

        Console.WriteLine();
        Console.WriteLine($"{tests.Count - failed}/{tests.Count} 件成功");
        return failed == 0 ? 0 : 1;
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
