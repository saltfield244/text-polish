using System.Runtime.InteropServices;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Automation;

public sealed record CapturedSelection(string Text, IntPtr SourceWindow);

/// <summary>
/// アクティブなウィンドウで選択されているテキストを取得する。
///
/// UI AutomationのTextPatternで取得できないアプリが多いため、
/// Ctrl+Cを送出してクリップボード経由で取得する方式を採る
/// (テキスト挿入と同じ理由で、確実性を優先している)。
/// </summary>
public sealed class SelectionCapture
{
    private readonly IAppLogger _logger;

    public SelectionCapture(IAppLogger logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 選択テキストを取得する。取得できなかった場合はnullを返す。
    /// あわせて、取得元のウィンドウハンドルも返す(整形後に書き戻す際、
    /// パレットへ移ったフォーカスを元のウィンドウへ戻すために必要)。
    /// </summary>
    public async Task<CapturedSelection?> CaptureAsync(CancellationToken ct = default)
    {
        var sourceWindow = NativeMethods.GetForegroundWindow();

        // Ctrl+Cの結果と、元々クリップボードにあった内容を区別するために一度クリアする。
        var previous = TryGetClipboardText();
        TryClearClipboard();

        SendCtrlC();

        // Ctrl+Cの結果がクリップボードへ反映されるまでの時間はアプリによって差があるため、
        // 固定待ちではなく、内容が入るまで短い間隔でポーリングする。
        string? text = null;
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(60, ct);
            text = TryGetClipboardText();
            if (!string.IsNullOrEmpty(text))
            {
                break;
            }
        }

        // 取得できてもできなくても、元のクリップボード内容は戻す。
        if (previous is not null)
        {
            TrySetClipboardText(previous);
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            _logger.Warn("選択テキストを取得できませんでした。テキストが選択されているか確認してください。");
            return null;
        }

        _logger.Info($"選択テキストを取得しました。文字数: {text.Length}");
        return new CapturedSelection(text, sourceWindow);
    }

    private static void SendCtrlC()
    {
        var inputs = new[]
        {
            NativeMethods.KeyDown(NativeMethods.VK_CONTROL),
            NativeMethods.KeyDown(NativeMethods.VK_C),
            NativeMethods.KeyUp(NativeMethods.VK_C),
            NativeMethods.KeyUp(NativeMethods.VK_CONTROL),
        };

        NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }

    private static string? TryGetClipboardText()
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                return System.Windows.Forms.Clipboard.ContainsText()
                    ? System.Windows.Forms.Clipboard.GetText()
                    : null;
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    private static void TryClearClipboard()
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Forms.Clipboard.Clear();
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static void TrySetClipboardText(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(text);
                return;
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }
    }
}
