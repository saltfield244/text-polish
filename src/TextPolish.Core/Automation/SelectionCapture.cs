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

        // Ctrl+Cで上書きされる前に、元の内容を全形式ぶん退避しておく。
        // 直前の書き戻しの復元待ちがある場合、今のクリップボードは一時的な整形結果にすぎないため、
        // 復元待ちの内容(本来の元内容)を引き継ぐ。その場合はCtrl+Cの成否に関わらず必ず書き戻す。
        var pendingOriginal = ClipboardRestoreScheduler.TakeOriginal(_logger);
        var original = pendingOriginal ?? ClipboardSnapshot.Capture(_logger);

        // 以前はCtrl+Cの結果と元の内容を区別するためにクリップボードを一度空にしていたが、
        // それだと空にした時点で元の内容が失われる。更新回数の変化で判定すれば空にしなくてよい。
        var sequenceBefore = ClipboardSnapshot.CurrentSequenceNumber;

        SendCtrlC();

        // Ctrl+Cの結果がクリップボードへ反映されるまでの時間はアプリによって差があるため、
        // 固定待ちではなく、内容が入るまで短い間隔でポーリングする。
        string? text = null;
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(60, ct);
            if (ClipboardSnapshot.CurrentSequenceNumber == sequenceBefore)
            {
                continue;
            }

            text = TryGetClipboardText();
            if (!string.IsNullOrEmpty(text))
            {
                break;
            }
        }

        // 取得できてもできなくても、クリップボードが変わっていれば元の内容に戻す。
        // ここでは更新回数による上書き防止は行わない。遅延レンダリングのアプリでは
        // 内容を読み出しただけで更新回数が進むことがあり、復元漏れの原因になるため
        // (Ctrl+C送出から1秒弱の間にユーザーがコピーすることはまずない)。
        if (pendingOriginal is not null || ClipboardSnapshot.CurrentSequenceNumber != sequenceBefore)
        {
            RestoreOriginal(original);
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

    private void RestoreOriginal(ClipboardSnapshot? original)
    {
        if (original is null)
        {
            _logger.Warn("元のクリップボード内容を退避できていなかったため、復元できません。");
            return;
        }

        var result = original.WriteToClipboard();
        if (result != ClipboardWriteResult.Written)
        {
            _logger.Warn($"選択テキスト取得後のクリップボードの復元に失敗しました({result})。");
        }
    }
}
