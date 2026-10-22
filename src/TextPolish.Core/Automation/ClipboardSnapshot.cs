using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using TextPolish.Core.Logging;
using static TextPolish.Core.Automation.ClipboardNativeMethods;

namespace TextPolish.Core.Automation;

public enum ClipboardWriteResult
{
    /// <summary>すべての形式を書き込めた。</summary>
    Written,

    /// <summary>一部の形式の書き込みに失敗した(残りは書き込めている)。</summary>
    PartiallyWritten,

    /// <summary>退避後にクリップボードが書き換えられていたため、上書きしなかった。</summary>
    SkippedBecauseChanged,

    /// <summary>クリップボードを開けなかった等の理由で書き込めなかった。</summary>
    Failed,
}

/// <summary>
/// クリップボードの内容を、載っている全形式ぶんバイト列として退避したもの。
///
/// 以前はプレーンテキストだけを退避していたため、画像・ファイル一覧・HTML・RTF等が
/// Ctrl+C/Ctrl+V の送出で失われていた。Win32のクリップボードAPIで形式ごとの
/// HGLOBALの中身をそのまま写し取ることで、アプリ独自の登録形式も含めて元に戻せる。
///
/// 復元できないもの:
/// - GDIハンドルで渡される形式(CF_PALETTE等)。
///   CF_BITMAPはCF_DIB/CF_DIBV5から、CF_METAFILEPICTはCF_ENHMETAFILEから
///   システムが合成するため、これらは実際には失われない。
/// - CF_PRIVATEFIRST〜LAST / CF_GDIOBJFIRST〜LAST の範囲の形式。
/// - OLEのIStream等でしか提供されない形式(Outlookの添付ファイルの仮想ファイル等)。
/// - 合計サイズが上限を超えた分(大きい形式から諦める)。
/// </summary>
public sealed class ClipboardSnapshot
{
    /// <summary>退避するデータ量の上限。4K画面のスクリーンショット(DIB+DIBV5+PNG)が収まる程度。</summary>
    internal const long DefaultMaxTotalBytes = 128L * 1024 * 1024;

    private readonly IReadOnlyList<FormatData> _formats;

    private ClipboardSnapshot(IReadOnlyList<FormatData> formats, bool isComplete)
    {
        _formats = formats;
        IsComplete = isComplete;
    }

    /// <summary>退避できなかった形式がない場合にtrue。</summary>
    public bool IsComplete { get; }

    public int FormatCount => _formats.Count;

    public long TotalBytes => _formats.Sum(f => (long)f.Data.Length);

    internal IEnumerable<uint> Formats => _formats.Select(f => f.Format);

    internal byte[]? GetData(uint format) => _formats.FirstOrDefault(f => f.Format == format)?.Data;

    /// <summary>
    /// クリップボードの更新回数。内容が書き換わるたびにシステムが増やす。
    /// 退避後にユーザーが別の内容をコピーしたかどうかの判定に使う。
    /// </summary>
    public static uint CurrentSequenceNumber => GetClipboardSequenceNumber();

    /// <summary>プレーンテキストだけを持つスナップショットを作る(書き込み用)。</summary>
    public static ClipboardSnapshot FromText(string text)
    {
        // CF_UNICODETEXTはNUL終端が必要。CF_TEXT等はシステムが合成する。
        var bytes = Encoding.Unicode.GetBytes(text + "\0");
        return new ClipboardSnapshot(new[] { new FormatData(CF_UNICODETEXT, bytes) }, isComplete: true);
    }

    /// <summary>
    /// 現在のクリップボードを退避する。クリップボードを開けなかった場合はnullを返す。
    /// 空のクリップボードは「形式が0個のスナップショット」として退避する(復元すると空に戻る)。
    /// </summary>
    public static ClipboardSnapshot? Capture(IAppLogger logger, long maxTotalBytes = DefaultMaxTotalBytes)
    {
        using var owner = new ClipboardOwnerWindow();

        if (!TryOpenClipboard(owner.Handle))
        {
            logger.Warn("クリップボードを開けなかったため、元の内容を退避できませんでした。");
            return null;
        }

        var candidates = new List<(int Order, uint Format, IntPtr Handle, long Size)>();
        var lost = new List<string>();

        try
        {
            uint format = 0;
            var order = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (IsSynthesizedFromOtherFormat(format))
                {
                    continue;
                }

                if (IsHandleFormat(format))
                {
                    lost.Add(DescribeFormat(format));
                    continue;
                }

                // 遅延レンダリングの形式は、ここで元のアプリに描画を要求することになる。
                var handle = GetClipboardData(format);
                if (handle == IntPtr.Zero)
                {
                    lost.Add(DescribeFormat(format));
                    continue;
                }

                long size = format == CF_ENHMETAFILE
                    ? GetEnhMetaFileBits(handle, 0, null)
                    : (long)GlobalSize(handle).ToUInt64();

                if (size <= 0)
                {
                    lost.Add(DescribeFormat(format));
                    continue;
                }

                candidates.Add((order++, format, handle, size));
            }

            // 上限を超える場合は小さい形式(テキスト・HTML・ファイル一覧等)を優先して残す。
            var copied = new List<(int Order, FormatData Data)>();
            long total = 0;
            foreach (var c in candidates.OrderBy(c => c.Size))
            {
                if (total + c.Size > maxTotalBytes)
                {
                    lost.Add($"{DescribeFormat(c.Format)}({c.Size / 1024}KB)");
                    continue;
                }

                var data = CopyFormat(c.Format, c.Handle, c.Size);
                if (data is null)
                {
                    lost.Add(DescribeFormat(c.Format));
                    continue;
                }

                copied.Add((c.Order, new FormatData(c.Format, data)));
                total += data.Length;
            }

            // 貼り付け先のアプリは列挙順を優先度として扱うため、元の順序で持っておく。
            var snapshot = new ClipboardSnapshot(
                copied.OrderBy(c => c.Order).Select(c => c.Data).ToList(),
                isComplete: lost.Count == 0);

            if (lost.Count > 0)
            {
                logger.Warn($"クリップボードの一部の形式は退避できませんでした: {string.Join(", ", lost)}");
            }

            return snapshot;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>
    /// 退避した内容をクリップボードへ書き込む。
    /// <paramref name="onlyIfSequenceIs"/> を指定した場合、クリップボードの更新回数がその値のまま
    /// (＝その後だれも書き換えていない)ときだけ書き込む。判定と書き込みはクリップボードを
    /// 開いたまま行うため、その間に他のアプリが割り込むことはない。
    /// </summary>
    public ClipboardWriteResult WriteToClipboard(uint? onlyIfSequenceIs = null)
    {
        using var owner = new ClipboardOwnerWindow();

        if (!TryOpenClipboard(owner.Handle))
        {
            return ClipboardWriteResult.Failed;
        }

        try
        {
            if (onlyIfSequenceIs is uint expected && GetClipboardSequenceNumber() != expected)
            {
                return ClipboardWriteResult.SkippedBecauseChanged;
            }

            if (!EmptyClipboard())
            {
                return ClipboardWriteResult.Failed;
            }

            var failures = 0;
            foreach (var f in _formats)
            {
                if (!SetFormat(f))
                {
                    failures++;
                }
            }

            return failures == 0 ? ClipboardWriteResult.Written
                : failures == _formats.Count ? ClipboardWriteResult.Failed
                : ClipboardWriteResult.PartiallyWritten;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpenClipboard(IntPtr owner)
    {
        // 他プロセスがクリップボードを開いている場合があるため短時間リトライする。
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(owner))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return false;
    }

    /// <summary>
    /// 他の形式からシステムが自動で合成する、GDIハンドルの形式。
    /// 元になる形式(CF_DIB等)を復元すれば再び合成されるため、退避しなくてよい。
    /// </summary>
    private static bool IsSynthesizedFromOtherFormat(uint format) =>
        format is CF_BITMAP or CF_METAFILEPICT;

    /// <summary>中身がHGLOBALではなくハンドルで、バイト列として写し取れない形式。</summary>
    private static bool IsHandleFormat(uint format) =>
        format is CF_PALETTE or CF_OWNERDISPLAY or CF_DSPBITMAP or CF_DSPMETAFILEPICT or CF_DSPENHMETAFILE
        || format is >= CF_PRIVATEFIRST and <= CF_PRIVATELAST
        || format is >= CF_GDIOBJFIRST and <= CF_GDIOBJLAST;

    private static byte[]? CopyFormat(uint format, IntPtr handle, long size)
    {
        if (format == CF_ENHMETAFILE)
        {
            var bits = new byte[size];
            return GetEnhMetaFileBits(handle, (uint)size, bits) == size ? bits : null;
        }

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var data = new byte[size];
            Marshal.Copy(ptr, data, 0, data.Length);
            return data;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static bool SetFormat(FormatData f)
    {
        if (f.Format == CF_ENHMETAFILE)
        {
            var hemf = SetEnhMetaFileBits((uint)f.Data.Length, f.Data);
            if (hemf == IntPtr.Zero)
            {
                return false;
            }

            if (SetClipboardData(f.Format, hemf) == IntPtr.Zero)
            {
                DeleteEnhMetaFile(hemf);
                return false;
            }

            return true;
        }

        var hMem = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(ulong)f.Data.Length);
        if (hMem == IntPtr.Zero)
        {
            return false;
        }

        var ptr = GlobalLock(hMem);
        if (ptr == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }

        Marshal.Copy(f.Data, 0, ptr, f.Data.Length);
        GlobalUnlock(hMem);

        // 成功するとメモリの所有権はシステムへ移る。失敗した場合だけ自分で解放する。
        if (SetClipboardData(f.Format, hMem) == IntPtr.Zero)
        {
            GlobalFree(hMem);
            return false;
        }

        return true;
    }

    private static string DescribeFormat(uint format)
    {
        var buffer = new char[128];
        var length = GetClipboardFormatName(format, buffer, buffer.Length);
        return length > 0 ? new string(buffer, 0, length) : $"0x{format:X4}";
    }

    private sealed record FormatData(uint Format, byte[] Data);

    /// <summary>
    /// クリップボードの所有者にするメッセージ専用ウィンドウ。
    /// OpenClipboard(NULL)のまま EmptyClipboard すると所有者がNULLになり、
    /// SetClipboardData が失敗すると文書化されているため、一時的なウィンドウを用意する。
    /// 遅延レンダリングは使わないので、ウィンドウを破棄しても書き込んだ内容は残る。
    /// </summary>
    private sealed class ClipboardOwnerWindow : NativeWindow, IDisposable
    {
        private static readonly IntPtr HwndMessage = new(-3);

        public ClipboardOwnerWindow()
        {
            CreateHandle(new CreateParams { Parent = HwndMessage });
        }

        public void Dispose() => DestroyHandle();
    }
}
