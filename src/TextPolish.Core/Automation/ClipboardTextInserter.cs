using System.Runtime.InteropServices;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Automation;

/// <summary>
/// Phase 4: クリップボード経由 + Ctrl+V送出によるテキスト挿入(確実性優先の既定実装)。
/// UI Automationでの直接挿入(ValuePattern/TextPattern)は挿入先コントロールの対応状況に
/// 大きく左右されるため、まずはこの方式を既定とする。
/// TODO: Phase5以降、UI AutomationのTextPattern/ValuePatternで挿入できる場合はそちらを優先し、
///       失敗時のみ本クラスにフォールバックする構成へ拡張する余地がある。
/// </summary>
public sealed class ClipboardTextInserter : ITextInserter
{
    private readonly IAppLogger _logger;
    private readonly bool _restoreClipboard;
    private readonly int _restoreDelayMs;

    public ClipboardTextInserter(IAppLogger logger, bool restoreClipboard = true, int restoreDelayMs = 800)
    {
        _logger = logger;
        _restoreClipboard = restoreClipboard;
        _restoreDelayMs = restoreDelayMs;
    }

    public async Task<bool> InsertAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        string? originalClipboardText = null;

        try
        {
            originalClipboardText = TryGetClipboardText();

            if (!TrySetClipboardText(text))
            {
                _logger.Error("クリップボードへのテキスト設定に失敗しました。挿入を中止します。");
                return false;
            }

            await Task.Delay(50, ct);

            var foregroundBefore = NativeMethods.GetForegroundWindowTitle();

            var sent = SendCtrlV();
            // GetLastWin32Errorは直後に取得すること(間に他のWin32呼び出しを挟むと上書きされる)。
            var err = Marshal.GetLastWin32Error();

            await Task.Delay(250, ct);

            if (sent == 0)
            {
                _logger.Error(
                    $"SendInputが1件も送出できませんでした(Win32Error={err})。" +
                    $"送出先フォアグラウンド: {foregroundBefore}");
                return false;
            }

            _logger.Info(
                $"テキストを挿入しました(クリップボード+Ctrl+V)。文字数: {text.Length}。" +
                $"送出先フォアグラウンド: {foregroundBefore}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("テキスト挿入(クリップボード+Ctrl+V)に失敗しました。", ex);
            return false;
        }
        finally
        {
            RestoreClipboardLater(originalClipboardText);
        }
    }

    /// <summary>
    /// 元のクリップボード内容を、貼り付けが確実に完了したあとに復元する。
    ///
    /// 貼り付け先アプリはCtrl+Vを受け取ってから非同期にクリップボードを読むため、
    /// 送出直後に復元すると「復元後の内容(＝直前にコピーしていた別のテキスト)」が
    /// 貼り付けられてしまう。実際にこの競合で、認識結果ではなく編集中にコピーしていた
    /// 文字列が挿入される不具合が発生したため、十分な待ち時間を空けてから復元する。
    ///
    /// 待っている間ユーザーの操作を止めないよう、復元はバックグラウンドで行う。
    /// </summary>
    private void RestoreClipboardLater(string? originalClipboardText)
    {
        if (!_restoreClipboard || originalClipboardText is null)
        {
            return;
        }

        // Clipboard APIはSTAスレッドからしか呼べないため、スレッドプール(MTA)ではなく
        // 専用のSTAスレッドで復元する。
        var thread = new Thread(() =>
        {
            try
            {
                Thread.Sleep(_restoreDelayMs);
                TrySetClipboardText(originalClipboardText);
            }
            catch (Exception ex)
            {
                // 元のクリップボード内容の復元失敗は致命的ではない。
                _logger.Warn($"クリップボードの復元に失敗しました: {ex.Message}");
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
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
                // 他プロセスがクリップボードを保持している場合があるため短時間リトライする。
                Thread.Sleep(20);
            }
        }

        return null;
    }

    private static bool TrySetClipboardText(string text)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Forms.Clipboard.SetText(text);
                return true;
            }
            catch (ExternalException)
            {
                Thread.Sleep(20);
            }
        }

        return false;
    }

    private static uint SendCtrlV()
    {
        var inputs = new[]
        {
            NativeMethods.KeyDown(NativeMethods.VK_CONTROL),
            NativeMethods.KeyDown(NativeMethods.VK_V),
            NativeMethods.KeyUp(NativeMethods.VK_V),
            NativeMethods.KeyUp(NativeMethods.VK_CONTROL),
        };

        return NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
    }
}
