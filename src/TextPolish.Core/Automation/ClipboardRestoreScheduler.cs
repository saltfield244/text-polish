using TextPolish.Core.Logging;

namespace TextPolish.Core.Automation;

/// <summary>
/// 書き戻し後に行う、元のクリップボード内容の遅延復元を管理する。
///
/// 貼り付け先アプリはCtrl+Vを受け取ってから非同期にクリップボードを読むため、復元は
/// 待ち時間を空けてから行う(<see cref="ClipboardTextInserter"/>参照)。この待ちの間に
///
/// - ユーザーが別の内容をコピーした場合、その内容を古い内容で上書きしてはならない。
///   書き込んだ直後の更新回数を覚えておき、変わっていたら復元を見送る。
/// - 次の整形(選択テキストの取得や書き戻し)が始まった場合、その時点のクリップボードは
///   自分が書き込んだ一時的な整形結果にすぎない。それを「元の内容」として退避すると、
///   本来の元の内容が失われるため、復元待ちの内容を引き継ぐ。
///
/// 復元待ちは常に1件だけ持つ(新しく予約すると古い予約は無効になる)。
/// </summary>
public static class ClipboardRestoreScheduler
{
    private static readonly object Gate = new();
    private static Pending? _pending;

    /// <summary>
    /// 復元待ちの元内容を引き取る。
    /// クリップボードがまだ自分の書き込んだ内容のままならその元内容を返し、
    /// 予約は取り消す(以後の復元は呼び出し側の責任になる)。
    /// 復元待ちがない、またはユーザーが既に別の内容をコピーしている場合はnullを返す。
    /// </summary>
    public static ClipboardSnapshot? TakeOriginal(IAppLogger logger)
    {
        lock (Gate)
        {
            var pending = _pending;
            _pending = null;

            if (pending is null)
            {
                return null;
            }

            if (ClipboardSnapshot.CurrentSequenceNumber == pending.ExpectedSequence)
            {
                logger.Info("前回の書き戻しの復元待ちを引き継ぎます。");
                return pending.Original;
            }

            logger.Info("前回の書き戻しのあとにクリップボードが更新されていたため、その復元は取りやめます。");
            return null;
        }
    }

    /// <summary>
    /// <paramref name="delayMs"/> 後に元内容を復元する。
    /// その時点のクリップボードの更新回数が <paramref name="expectedSequence"/> と異なれば復元しない。
    /// </summary>
    public static void Schedule(ClipboardSnapshot original, uint expectedSequence, int delayMs, IAppLogger logger)
    {
        var pending = new Pending(original, expectedSequence, logger);

        lock (Gate)
        {
            _pending = pending;
        }

        // 待っている間ユーザーの操作を止めないよう、復元はバックグラウンドで行う。
        // Win32のクリップボードAPIを使うため、STAスレッドである必要はない。
        var thread = new Thread(() =>
        {
            Thread.Sleep(Math.Max(0, delayMs));
            RunIfStillPending(pending);
        })
        {
            IsBackground = true,
        };

        thread.Start();
    }

    /// <summary>
    /// 復元待ちがあれば、待ち時間を待たずに今すぐ復元する。
    /// アプリ終了時に呼ぶ(バックグラウンドスレッドは終了時に打ち切られ、元内容が失われるため)。
    /// </summary>
    public static void RestorePendingNow()
    {
        Pending? pending;
        lock (Gate)
        {
            pending = _pending;
        }

        if (pending is not null)
        {
            RunIfStillPending(pending);
        }
    }

    private static void RunIfStillPending(Pending pending)
    {
        lock (Gate)
        {
            // 引き継がれた、または新しい予約で置き換えられた場合は何もしない。
            if (!ReferenceEquals(_pending, pending))
            {
                return;
            }

            _pending = null;

            try
            {
                var result = pending.Original.WriteToClipboard(onlyIfSequenceIs: pending.ExpectedSequence);
                switch (result)
                {
                    case ClipboardWriteResult.Written:
                        break;
                    case ClipboardWriteResult.SkippedBecauseChanged:
                        pending.Logger.Info("書き戻し後にクリップボードが更新されていたため、元の内容の復元は見送りました。");
                        break;
                    case ClipboardWriteResult.PartiallyWritten:
                        pending.Logger.Warn("クリップボードの一部の形式を復元できませんでした。");
                        break;
                    default:
                        pending.Logger.Warn("クリップボードの復元に失敗しました。");
                        break;
                }
            }
            catch (Exception ex)
            {
                // 元のクリップボード内容の復元失敗は致命的ではない。
                pending.Logger.Warn($"クリップボードの復元に失敗しました: {ex.Message}");
            }
        }
    }

    private sealed record Pending(ClipboardSnapshot Original, uint ExpectedSequence, IAppLogger Logger);
}
