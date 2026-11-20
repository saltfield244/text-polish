using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using TextPolish.Core.Automation;

namespace TextPolish.Tests;

/// <summary>
/// クリップボードの退避・復元と、遅延復元中の競合対策。実際のクリップボードを使う。
/// テストデータの準備と確認にはWinFormsのClipboard(OLE経由)を使い、
/// テスト対象のWin32経由の実装と独立した手段で確かめる。
/// </summary>
internal static class ClipboardTests
{
    private const string DropEffectFormat = "Preferred DropEffect";
    private const uint CF_DIB = 8;

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("クリップボード: テキストを復元できる", TextRoundTrip);
        yield return ("クリップボード: テキスト+HTML+RTFを全形式復元できる", HtmlRtfTextRoundTrip);
        yield return ("クリップボード: 画像を復元できる", ImageRoundTrip);
        yield return ("クリップボード: ファイル一覧と切り取り/コピーの区別を復元できる", FileDropRoundTrip);
        yield return ("クリップボード: 空のクリップボードは空に戻る", EmptyRoundTrip);
        yield return ("クリップボード: 上限を超える場合は小さい形式を優先して残す", SizeLimitKeepsSmallFormats);
        yield return ("クリップボード: 退避後に書き換えられていたら上書きしない", ConditionalWriteSkipsWhenChanged);
        yield return ("遅延復元: 待ち時間の後に元の内容へ戻る", ScheduledRestoreRestores);
        yield return ("遅延復元: 待機中にユーザーがコピーした内容は上書きしない", ScheduledRestoreRespectsUserCopy);
        yield return ("遅延復元: 連続実行では最初の元内容を引き継ぐ", ConsecutiveRunsKeepFirstOriginal);
        yield return ("遅延復元: 引き継ぎ時にユーザーのコピーがあれば元内容を捨てる", TakeOriginalAfterUserCopy);
        yield return ("遅延復元: 終了時は待たずに復元する", RestorePendingNowRestores);
        yield return ("書き戻し: 貼り付け先が違えば送出せずクリップボードを戻す", InsertAbortsWhenTargetWindowIsGone);
        yield return ("書き戻し: 退避できなければ書き込まずに中止する", InsertAbortsWhenOriginalCannotBeSaved);
        yield return ("取得: 退避できなければCtrl+Cを送らずに中止する", CaptureAbortsWhenOriginalCannotBeSaved);
    }

    /// <summary>
    /// 元の内容を退避できないときは、整形結果をクリップボードへ**書かずに**中止する。
    ///
    /// 書いてから退避できていないことに気づいても、利用者がコピーしていた内容はもう戻せない。
    /// 実際に起こる条件は、ほかのプロセスがクリップボードを開いたまま離さないこと
    /// (クリップボード履歴・パスワード管理ソフト・RDPのクリップボード同期)。
    /// ここでは別スレッドでクリップボードを開いたままにして、その状態を作る。
    /// </summary>
    private static void InsertAbortsWhenOriginalCannotBeSaved()
    {
        const string original = "利用者がコピーしていた内容(消えてはいけない)";
        Set(() => Clipboard.SetText(original));

        TextInsertResult result;

        // 掴んだままにするだけでは足りない。書き込みも同じように失敗するため、
        // 保護を外しても結果が ClipboardFailed になり、確認が通ってしまう(実際に通った)。
        // 本当に危ないのは「退避は失敗したが、書き込みは成功する」瞬間なので、
        // 退避の失敗がログに出た時点で離し、書き込みだけ成功する状態を作る。
        using (var holder = new ClipboardHolder())
        {
            Assert.True(holder.Held, "クリップボードを掴めなかった(この確認はできない)");

            var logger = new CallbackLogger(message =>
            {
                if (message.Contains("クリップボードを開けなかった", StringComparison.Ordinal))
                {
                    holder.Release();
                }
            });

            result = new ClipboardTextInserter(logger, restoreClipboard: true, restoreDelayMs: 60_000)
                .InsertAsync("整形結果(書き込んではいけない)", expectedWindow: new IntPtr(0x1234))
                .GetAwaiter()
                .GetResult();
        }

        Assert.Equal(TextInsertResult.ClipboardFailed, result, "書き戻しの結果");
        Assert.Equal(original, GetText(), "中止後のクリップボード");
    }

    /// <summary>ログの文面を見て手を打つためのロガー。</summary>
    private sealed class CallbackLogger : TextPolish.Core.Logging.IAppLogger
    {
        private readonly Action<string> _onMessage;

        public CallbackLogger(Action<string> onMessage) => _onMessage = onMessage;

        public void Info(string message) => _onMessage(message);

        public void Warn(string message) => _onMessage(message);

        public void Error(string message, Exception? ex = null) => _onMessage(message);
    }

    /// <summary>
    /// 選択テキストの取得も同じ。退避できないまま Ctrl+C を送ると、
    /// 利用者がコピーしていた内容が黙って消える。送る前に中止する。
    ///
    /// この確認が落ちるときは Ctrl+C が実際に送出される。前面のウィンドウの選択が
    /// クリップボードへ入るだけで、文章が書き換わることはない。
    /// </summary>
    private static void CaptureAbortsWhenOriginalCannotBeSaved()
    {
        const string original = "取得前にコピーしていた内容(消えてはいけない)";
        Set(() => Clipboard.SetText(original));

        SelectionCaptureResult captured;

        using (var holder = new ClipboardHolder())
        {
            Assert.True(holder.Held, "クリップボードを掴めなかった(この確認はできない)");

            captured = new SelectionCapture(new NullLogger()).CaptureAsync().GetAwaiter().GetResult();
        }

        Assert.Equal(
            SelectionCaptureFailure.ClipboardUnavailable, captured.Failure, "取得できなかった理由");
        Assert.True(captured.Selection is null, "中止したのに選択テキストを返している");
        Assert.Equal(original, GetText(), "中止後のクリップボード");
    }

    /// <summary>
    /// クリップボードを別スレッドで開いたまま保持する。
    ///
    /// 同じスレッドから開くと入れ子で成功してしまうため、必ず別スレッドで開く。
    /// 離し忘れると以降のテストが全部おかしくなるので <see cref="IDisposable"/> にしてある。
    /// </summary>
    private sealed class ClipboardHolder : IDisposable
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseClipboard();

        private readonly ManualResetEventSlim _release = new(false);
        private readonly ManualResetEventSlim _opened = new(false);
        private readonly Thread _thread;

        public ClipboardHolder()
        {
            _thread = new Thread(() =>
            {
                // 掴めないことがある(直前の操作や、クリップボード履歴などの他プロセス)。
                // 掴めないと確認そのものが成立しないので、短時間リトライする。
                for (var i = 0; i < RetryAttempts && !Held; i++)
                {
                    Held = OpenClipboard(IntPtr.Zero);

                    if (!Held)
                    {
                        Thread.Sleep(RetryIntervalMs);
                    }
                }

                _opened.Set();
                _release.Wait();

                if (Held)
                {
                    CloseClipboard();
                }
            })
            {
                IsBackground = true,
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            _opened.Wait(5000 + (RetryAttempts * RetryIntervalMs));
        }

        /// <summary>掴めたか。掴めていなければ、この確認は成立しない。</summary>
        public bool Held { get; private set; }

        /// <summary>
        /// 途中で離す。離し終わるまで待つ(待たずに戻ると、呼び出し側の次の操作が
        /// まだ掴まれている状態に当たってしまう)。
        /// </summary>
        public void Release()
        {
            _release.Set();
            _thread.Join(5000);
        }

        public void Dispose()
        {
            Release();
            _release.Dispose();
            _opened.Dispose();
        }
    }

    private static void TextRoundTrip()
    {
        Set(() => Clipboard.SetText("元のテキスト\r\n2行目"));
        var snapshot = CaptureOrFail();

        Set(() => Clipboard.SetText("上書き"));
        Assert.Equal(ClipboardWriteResult.Written, snapshot.WriteToClipboard(), "書き込み結果");

        Assert.Equal("元のテキスト\r\n2行目", GetText(), "テキスト");
        Assert.True(snapshot.IsComplete, "テキストだけなのに欠けた形式がある");
    }

    private static void HtmlRtfTextRoundTrip()
    {
        const string html = "<b>太字</b>の文章";
        const string rtf = @"{\rtf1\ansi {\b bold} text}";
        var data = new DataObject();
        data.SetText("太字の文章", TextDataFormat.UnicodeText);
        data.SetText(html, TextDataFormat.Html);
        data.SetText(rtf, TextDataFormat.Rtf);
        Set(() => Clipboard.SetDataObject(data, copy: true));

        var expectedHtml = GetText(TextDataFormat.Html);
        var snapshot = CaptureOrFail();

        Set(() => Clipboard.SetText("上書き"));
        snapshot.WriteToClipboard();

        var restoredHtml = GetText(TextDataFormat.Html);
        Assert.Equal("太字の文章", GetText(TextDataFormat.UnicodeText), "プレーンテキスト");
        Assert.Equal(expectedHtml, restoredHtml, "HTML");
        Assert.True(restoredHtml.Contains(html), "HTMLの本文");
        Assert.Equal(rtf, GetText(TextDataFormat.Rtf).TrimEnd('\0'), "RTF");
    }

    private static void ImageRoundTrip()
    {
        using var bitmap = new Bitmap(3, 2);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Lime);
        bitmap.SetPixel(2, 1, Color.Blue);
        Set(() => Clipboard.SetImage(bitmap));

        var snapshot = CaptureOrFail();
        Assert.True(snapshot.GetData(CF_DIB) is not null, "画像(CF_DIB)を退避できていない");

        Set(() => Clipboard.SetText("上書き"));
        Assert.True(!Query(Clipboard.ContainsImage), "上書きできていない");
        snapshot.WriteToClipboard();

        // 復元後のクリップボードを退避し直し、形式の並びと中身が退避時と完全に一致するかを見る。
        // 画像のデコード結果はWinFormsの変換(透過の扱い等)に左右されるため、バイト列で比べる。
        var restored = CaptureOrFail();
        Assert.Equal(
            string.Join(",", snapshot.Formats),
            string.Join(",", restored.Formats),
            "形式の並び");
        foreach (var format in snapshot.Formats)
        {
            Assert.True(
                snapshot.GetData(format)!.AsSpan().SequenceEqual(restored.GetData(format)),
                $"形式 0x{format:X4} の中身が一致しない");
        }

        Assert.True(Query(Clipboard.ContainsImage), "画像として認識されない");
        using var image = Query(Clipboard.GetImage)!;
        Assert.Equal(new Size(3, 2), image?.Size ?? Size.Empty, "画像サイズ");
    }

    private static void FileDropRoundTrip()
    {
        var files = new StringCollection { @"C:\Windows\notepad.exe", @"C:\Windows\System32\drivers\etc\hosts" };
        var data = new DataObject();
        data.SetFileDropList(files);
        // エクスプローラーで「切り取り」した状態(DROPEFFECT_MOVE = 2)。
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(2)));
        Set(() => Clipboard.SetDataObject(data, copy: true));

        var snapshot = CaptureOrFail();

        Set(() => Clipboard.SetText("上書き"));
        snapshot.WriteToClipboard();

        var restored = Query(Clipboard.GetFileDropList);
        Assert.Equal(files.Count, restored.Count, "ファイル数");
        Assert.Equal(files[0], restored[0], "1件目");
        Assert.Equal(files[1], restored[1], "2件目");

        Assert.True(
            Clipboard.TryGetData(DropEffectFormat, out MemoryStream? effect) && effect is not null,
            "Preferred DropEffect が復元されていない");
        Assert.Equal(2, BitConverter.ToInt32(effect!.ToArray(), 0), "切り取り/コピーの区別");
    }

    private static void EmptyRoundTrip()
    {
        Set(Clipboard.Clear);
        var snapshot = CaptureOrFail();
        Assert.Equal(0, snapshot.FormatCount, "空のクリップボードの形式数");

        Set(() => Clipboard.SetText("上書き"));
        snapshot.WriteToClipboard();

        Assert.True(!Query(Clipboard.ContainsText), "空に戻っていない");
    }

    private static void SizeLimitKeepsSmallFormats()
    {
        var data = new DataObject();
        data.SetText("小さいテキスト");
        data.SetData("TextPolish.Tests.Large", new MemoryStream(new byte[256 * 1024]));
        Set(() => Clipboard.SetDataObject(data, copy: true));

        var logger = new NullLogger();
        var snapshot = ClipboardSnapshot.Capture(logger, maxTotalBytes: 64 * 1024)!;

        Assert.True(!snapshot.IsComplete, "上限超過なのに完全扱い");
        Assert.True(logger.Warnings.Count > 0, "欠けた形式の警告がない");

        Set(() => Clipboard.SetText("上書き"));
        snapshot.WriteToClipboard();
        Assert.Equal("小さいテキスト", GetText(), "小さい形式(テキスト)");
        Assert.True(!Query(() => Clipboard.ContainsData("TextPolish.Tests.Large")), "上限を超えた形式が書き込まれた");
    }

    private static void ConditionalWriteSkipsWhenChanged()
    {
        Set(() => Clipboard.SetText("元の内容"));
        var snapshot = CaptureOrFail();

        ClipboardSnapshot.FromText("整形結果").WriteToClipboard();
        var written = ClipboardSnapshot.CurrentSequenceNumber;

        Set(() => Clipboard.SetText("ユーザーがコピーした内容"));

        Assert.Equal(
            ClipboardWriteResult.SkippedBecauseChanged,
            snapshot.WriteToClipboard(onlyIfSequenceIs: written),
            "書き込み結果");
        Assert.Equal("ユーザーがコピーした内容", GetText(), "クリップボード");
    }

    private static void ScheduledRestoreRestores()
    {
        Set(() => Clipboard.SetText("元の内容"));
        var original = CaptureOrFail();
        var written = WriteTransient("整形結果");

        ClipboardRestoreScheduler.Schedule(original, written, delayMs: 1000, new NullLogger());
        Assert.Equal("整形結果", GetText(), "待ち時間中の内容");

        Thread.Sleep(1600);
        Assert.Equal("元の内容", GetText(), "復元後の内容");
    }

    private static void ScheduledRestoreRespectsUserCopy()
    {
        Set(() => Clipboard.SetText("元の内容"));
        var original = CaptureOrFail();
        var written = WriteTransient("整形結果");

        ClipboardRestoreScheduler.Schedule(original, written, delayMs: 1000, new NullLogger());
        Set(() => Clipboard.SetText("待ち時間中にユーザーがコピー"));

        Thread.Sleep(1600);
        Assert.Equal("待ち時間中にユーザーがコピー", GetText(), "ユーザーのコピーが上書きされた");
    }

    /// <summary>
    /// 1回目の書き戻しの復元待ち中に2回目を実行した場合。2回目が一時的な整形結果を
    /// 「元の内容」として退避すると、最後に整形結果が残ってしまう。
    /// </summary>
    private static void ConsecutiveRunsKeepFirstOriginal()
    {
        var logger = new NullLogger();
        Set(() => Clipboard.SetText("本来の元内容"));

        // 1回目
        var first = ClipboardRestoreScheduler.TakeOriginal(logger) ?? CaptureOrFail();
        ClipboardRestoreScheduler.Schedule(first, WriteTransient("整形結果1"), delayMs: 1000, logger);

        // 2回目(1回目の復元前)
        var second = ClipboardRestoreScheduler.TakeOriginal(logger) ?? CaptureOrFail();
        Assert.True(ReferenceEquals(first, second), "復元待ちの元内容を引き継いでいない");
        ClipboardRestoreScheduler.Schedule(second, WriteTransient("整形結果2"), delayMs: 1000, logger);

        Thread.Sleep(1600);
        Assert.Equal("本来の元内容", GetText(), "最終的なクリップボード");
    }

    private static void TakeOriginalAfterUserCopy()
    {
        var logger = new NullLogger();
        Set(() => Clipboard.SetText("元の内容"));
        var original = CaptureOrFail();
        ClipboardRestoreScheduler.Schedule(original, WriteTransient("整形結果"), delayMs: 5000, logger);

        Set(() => Clipboard.SetText("ユーザーがコピー"));

        Assert.True(ClipboardRestoreScheduler.TakeOriginal(logger) is null, "古い元内容を引き継いでしまう");
        ClipboardRestoreScheduler.RestorePendingNow();
        Assert.Equal("ユーザーがコピー", GetText(), "クリップボード");
    }

    private static void RestorePendingNowRestores()
    {
        Set(() => Clipboard.SetText("元の内容"));
        var original = CaptureOrFail();
        ClipboardRestoreScheduler.Schedule(original, WriteTransient("整形結果"), delayMs: 60_000, new NullLogger());

        ClipboardRestoreScheduler.RestorePendingNow();
        Assert.Equal("元の内容", GetText(), "即時復元");
    }

    /// <summary>
    /// 取得元のウィンドウが存在しない場合、Ctrl+V を送らずに中止し、
    /// クリップボードを元の内容へ戻すこと。
    ///
    /// 整形結果がクリップボードに残ったままにならないこと(＝利用者が次に貼り付けたときに
    /// 意図しない文章が入らないこと)と、中止しても元の内容を失わないことの両方を見る。
    /// 存在しないハンドルを渡すため、実際にキー送出は起こらない。
    /// </summary>
    private static void InsertAbortsWhenTargetWindowIsGone()
    {
        Set(() => Clipboard.SetText("利用者がコピーしていた内容"));

        var logger = new NullLogger();
        var inserter = new ClipboardTextInserter(logger, restoreClipboard: true, restoreDelayMs: 60_000);

        var result = inserter
            .InsertAsync("整形結果(貼り付けてはいけない)", expectedWindow: new IntPtr(0x1234))
            .GetAwaiter()
            .GetResult();

        Assert.Equal(TextInsertResult.TargetWindowChanged, result, "書き戻しの結果");
        Assert.Equal("利用者がコピーしていた内容", GetText(), "中止後のクリップボード");
    }

    // --- テスト側のクリップボード操作のリトライ -------------------------------------------
    //
    // クリップボードはOSの共有資源で、クリップボード履歴(Win+V)をはじめ他のプロセスも
    // 同時に開く。WinForms の Clipboard はこの競合に弱く、短い間隔で読み書きすると
    //   ・SetText / SetDataObject が ExternalException を投げる
    //   ・GetText が空、または見た目が空白の壊れた値を返す
    // ことがある。どちらも検証対象とは無関係にテストを失敗させるため、
    // テスト側の操作だけを短時間リトライする。
    //
    // 検証対象(Win32で読み書きする ClipboardSnapshot)側はリトライを足していない。
    // 原因調査のため同じ往復を150回試した計測では、WriteToClipboard は毎回 Written を返し、
    // 復元内容も正しかった。失敗していたのはテスト側の WinForms 呼び出しだけで、
    // 操作の間隔を空けるだけでも再現しなくなる。期待値そのものは緩めていない。

    private const int RetryAttempts = 40;
    private const int RetryIntervalMs = 25;

    /// <summary>クリップボードへの書き込み。競合による例外だけをリトライする。</summary>
    private static void Set(Action set)
    {
        for (var i = 0; i < RetryAttempts; i++)
        {
            try
            {
                set();
                return;
            }
            catch (ExternalException) when (i < RetryAttempts - 1)
            {
                Thread.Sleep(RetryIntervalMs);
            }
        }
    }

    /// <summary>
    /// 中身があるはずのテキストを読む。空や壊れた値が返るあいだはリトライする。
    /// 最後まで読めなければ読めた値をそのまま返し、表明側で失敗させる(無言で待ち続けない)。
    /// </summary>
    private static string GetText(TextDataFormat? format = null)
    {
        var last = string.Empty;

        for (var i = 0; i < RetryAttempts; i++)
        {
            try
            {
                last = format is TextDataFormat f ? Clipboard.GetText(f) : Clipboard.GetText();
                if (LooksUsable(last))
                {
                    return last;
                }
            }
            catch (ExternalException)
            {
            }

            Thread.Sleep(RetryIntervalMs);
        }

        return last;
    }

    /// <summary>
    /// 読めた値が使えるものか。空白だけ、または制御文字だけの文字列は競合による
    /// 壊れた読み取りとみなす(テストがクリップボードへ書くのは必ず通常の文字を含む文字列のため)。
    /// </summary>
    private static bool LooksUsable(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Any(c => !char.IsControl(c) && !char.IsWhiteSpace(c));

    /// <summary>空も正しい答えになり得る問い合わせ。競合による例外だけをリトライする。</summary>
    private static T Query<T>(Func<T> read)
    {
        for (var i = 0; i < RetryAttempts; i++)
        {
            try
            {
                return read();
            }
            catch (ExternalException) when (i < RetryAttempts - 1)
            {
                Thread.Sleep(RetryIntervalMs);
            }
        }

        return read();
    }

    private static ClipboardSnapshot CaptureOrFail() =>
        ClipboardSnapshot.Capture(new NullLogger()) ?? throw new Exception("クリップボードを退避できなかった");

    /// <summary>ClipboardTextInserter と同じ手順で一時的な内容を書き、その直後の更新回数を返す。</summary>
    private static uint WriteTransient(string text)
    {
        Assert.Equal(ClipboardWriteResult.Written, ClipboardSnapshot.FromText(text).WriteToClipboard(), "一時書き込み");
        return ClipboardSnapshot.CurrentSequenceNumber;
    }
}
