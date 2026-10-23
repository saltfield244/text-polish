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

    public async Task<TextInsertResult> InsertAsync(
        string text, IntPtr expectedWindow = default, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return TextInsertResult.EmptyText;
        }

        ClipboardSnapshot? original = null;
        uint? writtenSequence = null;

        // 遅延復元を予約するのは、実際に Ctrl+V を送った場合だけ。
        // 送らずに中止した場合は、待たずにその場で元の内容へ戻す。
        var pasteSent = false;

        try
        {
            if (_restoreClipboard)
            {
                // 直前の書き戻しの復元待ちがあれば、今のクリップボードは一時的な整形結果にすぎない。
                // その場合は復元待ちの内容こそが本来の元内容なので、そちらを引き継ぐ。
                original = ClipboardRestoreScheduler.TakeOriginal(_logger) ?? ClipboardSnapshot.Capture(_logger);
            }

            var sequenceBefore = ClipboardSnapshot.CurrentSequenceNumber;
            var writeResult = ClipboardSnapshot.FromText(text).WriteToClipboard();

            if (writeResult != ClipboardWriteResult.Written)
            {
                _logger.Error("クリップボードへのテキスト設定に失敗しました。挿入を中止します。");

                // 途中まで書き換えてしまった(空にした)可能性があるため、変化していれば即座に戻す。
                if (original is not null && ClipboardSnapshot.CurrentSequenceNumber != sequenceBefore)
                {
                    original.WriteToClipboard();
                }

                return TextInsertResult.ClipboardFailed;
            }

            // この値から変わっていれば、復元までの間に誰か(主にユーザー)がコピーしたことになる。
            writtenSequence = ClipboardSnapshot.CurrentSequenceNumber;

            await Task.Delay(50, ct);

            var foregroundBefore = NativeMethods.GetForegroundWindowTitle();

            // Ctrl+V は「送出した瞬間に前面にあるウィンドウ」へ届く。整形に数秒かかるため、
            // その間に利用者が別のアプリへ移っていることも、フォーカス復帰が実際には
            // 効いていないこともある。送出の直前に照合し、違っていれば何も貼り付けない。
            var targetCheck = PasteTarget.Check(
                expectedWindow, NativeMethods.GetForegroundWindow(), NativeMethods.IsWindow(expectedWindow));

            if (targetCheck is PasteTargetCheck.Mismatch or PasteTargetCheck.WindowGone)
            {
                _logger.Error(
                    $"貼り付け先が取得元のウィンドウではないため、書き戻しを中止しました({targetCheck})。" +
                    $"取得元: 0x{expectedWindow:X} / 現在のフォアグラウンド: {foregroundBefore}");

                // ここでは整形結果を貼り付けていないので、クリップボードは元の内容に戻す。
                // 整形結果は呼び出し元が保持している(パレットは画面に残し、即実行は
                // クリップボードへ退避する)ため、失われない。
                return TextInsertResult.TargetWindowChanged;
            }

            if (targetCheck == PasteTargetCheck.NotVerified)
            {
                _logger.Warn("取得元のウィンドウが分からないため、貼り付け先の照合を省略しました。");
            }

            var sent = SendCtrlV();
            // GetLastWin32Errorは直後に取得すること(間に他のWin32呼び出しを挟むと上書きされる)。
            var err = Marshal.GetLastWin32Error();

            await Task.Delay(250, ct);

            if (sent == 0)
            {
                _logger.Error(
                    $"SendInputが1件も送出できませんでした(Win32Error={err})。" +
                    $"送出先フォアグラウンド: {foregroundBefore}");
                return TextInsertResult.SendFailed;
            }

            pasteSent = true;

            _logger.Info(
                $"テキストを挿入しました(クリップボード+Ctrl+V)。文字数: {text.Length}。" +
                $"送出先フォアグラウンド: {foregroundBefore}");
            return TextInsertResult.Inserted;
        }
        catch (Exception ex)
        {
            _logger.Error("テキスト挿入(クリップボード+Ctrl+V)に失敗しました。", ex);
            return TextInsertResult.ClipboardFailed;
        }
        finally
        {
            if (original is not null && writtenSequence is uint sequence)
            {
                if (pasteSent)
                {
                    RestoreClipboardLater(original, sequence);
                }
                else
                {
                    // 貼り付けていないので待つ必要がない。待たずに戻せば、
                    // 整形結果がクリップボードに残ったまま放置されることもない。
                    RestoreClipboardNow(original, sequence);
                }
            }
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
    /// 待っている間にユーザーが新たにコピーした内容は上書きしない
    /// (<see cref="ClipboardRestoreScheduler"/>参照)。
    /// </summary>
    private void RestoreClipboardLater(ClipboardSnapshot original, uint writtenSequence)
    {
        ClipboardRestoreScheduler.Schedule(original, writtenSequence, _restoreDelayMs, _logger);
    }

    /// <summary>
    /// 貼り付けずに中止した場合の復元。待つ理由がないため即座に戻す。
    /// 書き込んだあとに利用者がコピーしていれば、その内容は上書きしない。
    /// </summary>
    private void RestoreClipboardNow(ClipboardSnapshot original, uint writtenSequence)
    {
        var result = original.WriteToClipboard(onlyIfSequenceIs: writtenSequence);

        if (result == ClipboardWriteResult.SkippedBecauseChanged)
        {
            _logger.Info("中止後にクリップボードが更新されていたため、元の内容の復元は見送りました。");
        }
        else if (result != ClipboardWriteResult.Written)
        {
            _logger.Warn($"中止後のクリップボードの復元に失敗しました({result})。");
        }
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
