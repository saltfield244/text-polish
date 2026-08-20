using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Automation;

/// <summary>
/// UI Automation (System.Windows.Automation) でアクティブウィンドウのアプリ名と
/// カーソルがあるコントロールのテキストを取得する。取得できない場合はnullを返し、
/// 呼び出し元(パイプライン)はコンテキストなしでLLM整形を続行する。
/// </summary>
public sealed class ActiveWindowContextProvider : IActiveWindowContextProvider
{
    private const int MaxContextLength = 500;
    private readonly IAppLogger _logger;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public ActiveWindowContextProvider(IAppLogger logger)
    {
        _logger = logger;
    }

    public ActiveWindowContext GetContext()
    {
        string? appName = null;
        string? surroundingText = null;

        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return new ActiveWindowContext();
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                appName = process.ProcessName;
            }
            catch (Exception ex)
            {
                _logger.Warn($"アクティブウィンドウのプロセス名取得に失敗しました: {ex.Message}");
            }

            try
            {
                surroundingText = TryGetSurroundingText();
            }
            catch (Exception ex)
            {
                // UI Automationでの取得失敗はコンテキストなしとして扱い、パイプライン全体は継続する。
                _logger.Warn($"UI Automationによるカーソル周辺テキスト取得に失敗しました: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            _logger.Error("アクティブウィンドウのコンテキスト取得中に予期しないエラーが発生しました。", ex);
        }

        return new ActiveWindowContext { AppName = appName, SurroundingText = surroundingText };
    }

    private static string? TryGetSurroundingText()
    {
        var focused = AutomationElement.FocusedElement;
        if (focused is null)
        {
            return null;
        }

        // TODO: Windows 10 2004以降で提供されるTextPattern2.GetCaretRangeが利用できる環境では、
        //       キャレット位置を中心とした前後テキスト取得に置き換えるとより精度が上がる。
        //       現状は簡易実装として、取得できたテキストの末尾側 MaxContextLength 文字を使用する。
        if (focused.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj) &&
            textPatternObj is TextPattern textPattern)
        {
            var fullText = textPattern.DocumentRange.GetText(MaxContextLength * 4);
            return Truncate(fullText, MaxContextLength);
        }

        if (focused.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) &&
            valuePatternObj is ValuePattern valuePattern)
        {
            return Truncate(valuePattern.Current.Value, MaxContextLength);
        }

        return null;
    }

    private static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        return text.Length <= maxLength ? text : text[^maxLength..];
    }
}
