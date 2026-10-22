using System.Runtime.InteropServices;

namespace TextPolish.Core.Automation;

/// <summary>
/// クリップボードをWin32 APIで直接扱うための宣言。
/// WinForms/WPFのClipboardはOLE経由でSTAスレッドが必要なうえ、
/// 取得できる形式が「.NETが知っている型」に限られるため、退避・復元ではこちらを使う。
/// </summary>
internal static class ClipboardNativeMethods
{
    internal const uint CF_TEXT = 1;
    internal const uint CF_BITMAP = 2;
    internal const uint CF_METAFILEPICT = 3;
    internal const uint CF_OEMTEXT = 7;
    internal const uint CF_DIB = 8;
    internal const uint CF_PALETTE = 9;
    internal const uint CF_UNICODETEXT = 13;
    internal const uint CF_ENHMETAFILE = 14;
    internal const uint CF_HDROP = 15;
    internal const uint CF_LOCALE = 16;
    internal const uint CF_DIBV5 = 17;
    internal const uint CF_OWNERDISPLAY = 0x0080;
    internal const uint CF_DSPBITMAP = 0x0082;
    internal const uint CF_DSPMETAFILEPICT = 0x0083;
    internal const uint CF_DSPENHMETAFILE = 0x008E;
    internal const uint CF_PRIVATEFIRST = 0x0200;
    internal const uint CF_PRIVATELAST = 0x02FF;
    internal const uint CF_GDIOBJFIRST = 0x0300;
    internal const uint CF_GDIOBJLAST = 0x03FF;

    internal const uint GMEM_MOVEABLE = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClipboardFormatName(uint format, [Out] char[] lpszFormatName, int cchMaxCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr GlobalSize(IntPtr hMem);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern uint GetEnhMetaFileBits(IntPtr hemf, uint nSize, [Out] byte[]? lpData);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SetEnhMetaFileBits(uint nSize, byte[] pb);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteEnhMetaFile(IntPtr hemf);
}
