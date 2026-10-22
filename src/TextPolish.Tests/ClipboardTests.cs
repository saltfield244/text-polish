using System.Collections.Specialized;
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
    }

    private static void TextRoundTrip()
    {
        Clipboard.SetText("元のテキスト\r\n2行目");
        var snapshot = CaptureOrFail();

        Clipboard.SetText("上書き");
        Assert.Equal(ClipboardWriteResult.Written, snapshot.WriteToClipboard(), "書き込み結果");

        Assert.Equal("元のテキスト\r\n2行目", Clipboard.GetText(), "テキスト");
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
        Clipboard.SetDataObject(data, copy: true);

        var expectedHtml = Clipboard.GetText(TextDataFormat.Html);
        var snapshot = CaptureOrFail();

        Clipboard.SetText("上書き");
        snapshot.WriteToClipboard();

        Assert.Equal("太字の文章", Clipboard.GetText(TextDataFormat.UnicodeText), "プレーンテキスト");
        Assert.Equal(expectedHtml, Clipboard.GetText(TextDataFormat.Html), "HTML");
        Assert.True(Clipboard.GetText(TextDataFormat.Html).Contains(html), "HTMLの本文");
        Assert.Equal(rtf, Clipboard.GetText(TextDataFormat.Rtf).TrimEnd('\0'), "RTF");
    }

    private static void ImageRoundTrip()
    {
        using var bitmap = new Bitmap(3, 2);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Lime);
        bitmap.SetPixel(2, 1, Color.Blue);
        Clipboard.SetImage(bitmap);

        var snapshot = CaptureOrFail();
        Assert.True(snapshot.GetData(CF_DIB) is not null, "画像(CF_DIB)を退避できていない");

        Clipboard.SetText("上書き");
        Assert.True(!Clipboard.ContainsImage(), "上書きできていない");
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

        Assert.True(Clipboard.ContainsImage(), "画像として認識されない");
        using var image = Clipboard.GetImage();
        Assert.Equal(new Size(3, 2), image?.Size ?? Size.Empty, "画像サイズ");
    }

    private static void FileDropRoundTrip()
    {
        var files = new StringCollection { @"C:\Windows\notepad.exe", @"C:\Windows\System32\drivers\etc\hosts" };
        var data = new DataObject();
        data.SetFileDropList(files);
        // エクスプローラーで「切り取り」した状態(DROPEFFECT_MOVE = 2)。
        data.SetData(DropEffectFormat, new MemoryStream(BitConverter.GetBytes(2)));
        Clipboard.SetDataObject(data, copy: true);

        var snapshot = CaptureOrFail();

        Clipboard.SetText("上書き");
        snapshot.WriteToClipboard();

        var restored = Clipboard.GetFileDropList();
        Assert.Equal(files.Count, restored.Count, "ファイル数");
        Assert.Equal(files[0], restored[0], "1件目");
        Assert.Equal(files[1], restored[1], "2件目");

        var effect = Clipboard.GetData(DropEffectFormat) as MemoryStream;
        Assert.True(effect is not null, "Preferred DropEffect が復元されていない");
        Assert.Equal(2, BitConverter.ToInt32(effect!.ToArray(), 0), "切り取り/コピーの区別");
    }

    private static void EmptyRoundTrip()
    {
        Clipboard.Clear();
        var snapshot = CaptureOrFail();
        Assert.Equal(0, snapshot.FormatCount, "空のクリップボードの形式数");

        Clipboard.SetText("上書き");
        snapshot.WriteToClipboard();

        Assert.True(!Clipboard.ContainsText(), "空に戻っていない");
    }

    private static void SizeLimitKeepsSmallFormats()
    {
        var data = new DataObject();
        data.SetText("小さいテキスト");
        data.SetData("TextPolish.Tests.Large", new MemoryStream(new byte[256 * 1024]));
        Clipboard.SetDataObject(data, copy: true);

        var logger = new NullLogger();
        var snapshot = ClipboardSnapshot.Capture(logger, maxTotalBytes: 64 * 1024)!;

        Assert.True(!snapshot.IsComplete, "上限超過なのに完全扱い");
        Assert.True(logger.Warnings.Count > 0, "欠けた形式の警告がない");

        Clipboard.SetText("上書き");
        snapshot.WriteToClipboard();
        Assert.Equal("小さいテキスト", Clipboard.GetText(), "小さい形式(テキスト)");
        Assert.True(!Clipboard.ContainsData("TextPolish.Tests.Large"), "上限を超えた形式が書き込まれた");
    }

    private static void ConditionalWriteSkipsWhenChanged()
    {
        Clipboard.SetText("元の内容");
        var snapshot = CaptureOrFail();

        ClipboardSnapshot.FromText("整形結果").WriteToClipboard();
        var written = ClipboardSnapshot.CurrentSequenceNumber;

        Clipboard.SetText("ユーザーがコピーした内容");

        Assert.Equal(
            ClipboardWriteResult.SkippedBecauseChanged,
            snapshot.WriteToClipboard(onlyIfSequenceIs: written),
            "書き込み結果");
        Assert.Equal("ユーザーがコピーした内容", Clipboard.GetText(), "クリップボード");
    }

    private static void ScheduledRestoreRestores()
    {
        Clipboard.SetText("元の内容");
        var original = CaptureOrFail();
        var written = WriteTransient("整形結果");

        ClipboardRestoreScheduler.Schedule(original, written, delayMs: 1000, new NullLogger());
        Assert.Equal("整形結果", Clipboard.GetText(), "待ち時間中の内容");

        Thread.Sleep(1600);
        Assert.Equal("元の内容", Clipboard.GetText(), "復元後の内容");
    }

    private static void ScheduledRestoreRespectsUserCopy()
    {
        Clipboard.SetText("元の内容");
        var original = CaptureOrFail();
        var written = WriteTransient("整形結果");

        ClipboardRestoreScheduler.Schedule(original, written, delayMs: 1000, new NullLogger());
        Clipboard.SetText("待ち時間中にユーザーがコピー");

        Thread.Sleep(1600);
        Assert.Equal("待ち時間中にユーザーがコピー", Clipboard.GetText(), "ユーザーのコピーが上書きされた");
    }

    /// <summary>
    /// 1回目の書き戻しの復元待ち中に2回目を実行した場合。2回目が一時的な整形結果を
    /// 「元の内容」として退避すると、最後に整形結果が残ってしまう。
    /// </summary>
    private static void ConsecutiveRunsKeepFirstOriginal()
    {
        var logger = new NullLogger();
        Clipboard.SetText("本来の元内容");

        // 1回目
        var first = ClipboardRestoreScheduler.TakeOriginal(logger) ?? CaptureOrFail();
        ClipboardRestoreScheduler.Schedule(first, WriteTransient("整形結果1"), delayMs: 1000, logger);

        // 2回目(1回目の復元前)
        var second = ClipboardRestoreScheduler.TakeOriginal(logger) ?? CaptureOrFail();
        Assert.True(ReferenceEquals(first, second), "復元待ちの元内容を引き継いでいない");
        ClipboardRestoreScheduler.Schedule(second, WriteTransient("整形結果2"), delayMs: 1000, logger);

        Thread.Sleep(1600);
        Assert.Equal("本来の元内容", Clipboard.GetText(), "最終的なクリップボード");
    }

    private static void TakeOriginalAfterUserCopy()
    {
        var logger = new NullLogger();
        Clipboard.SetText("元の内容");
        var original = CaptureOrFail();
        ClipboardRestoreScheduler.Schedule(original, WriteTransient("整形結果"), delayMs: 5000, logger);

        Clipboard.SetText("ユーザーがコピー");

        Assert.True(ClipboardRestoreScheduler.TakeOriginal(logger) is null, "古い元内容を引き継いでしまう");
        ClipboardRestoreScheduler.RestorePendingNow();
        Assert.Equal("ユーザーがコピー", Clipboard.GetText(), "クリップボード");
    }

    private static void RestorePendingNowRestores()
    {
        Clipboard.SetText("元の内容");
        var original = CaptureOrFail();
        ClipboardRestoreScheduler.Schedule(original, WriteTransient("整形結果"), delayMs: 60_000, new NullLogger());

        ClipboardRestoreScheduler.RestorePendingNow();
        Assert.Equal("元の内容", Clipboard.GetText(), "即時復元");
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
