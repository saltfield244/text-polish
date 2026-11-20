using System.Runtime.InteropServices;

namespace TextPolish.Core.Automation;

internal static class NativeMethods
{
    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_V = 0x56;
    internal const ushort VK_C = 0x43;

    private const int InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    /// <summary>
    /// Win32の INPUT.DUMMYUNIONNAME に対応する共用体。
    /// キーボード入力しか使わないが、MOUSEINPUT/HARDWAREINPUT も必ず宣言すること。
    /// 共用体のサイズは最大メンバ(MOUSEINPUT)で決まり、SendInput は cbSize が実際の
    /// sizeof(INPUT) と一致しない場合、何もせずに 0 を返して失敗するため
    /// (x64: 40バイト。KEYBDINPUTだけだと32バイトになり全ての送出が失敗する)。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;

        [FieldOffset(0)]
        public KEYBDINPUT ki;

        [FieldOffset(0)]
        public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>
    /// 前面のウィンドウを、ログへ残してよい形で説明する。
    ///
    /// **ウィンドウタイトルは使わない。** タイトルには編集中の文書名やファイル名が入り、
    /// 未保存のメモ帳ではタブ名が本文の1行目になるため、整形した文章そのものが
    /// ログへ残ってしまう(実機確認の A1 で、実際に3件とも残っていた)。
    /// 障害調査に必要なのは「どのアプリの、どのウィンドウだったか」なので、
    /// プロセス名とハンドルだけを残す。
    /// </summary>
    internal static string DescribeForegroundWindow()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return "(none)";
        }

        return $"{GetProcessName(hwnd)} [hwnd=0x{hwnd:X}]";
    }

    private static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);

            if (pid == 0)
            {
                return "(不明)";
            }

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // 既に終了しているウィンドウ。調査に使う情報が1つ減るだけなので、落とさない。
            return "(不明)";
        }
    }

    internal static INPUT KeyDown(ushort vk) => new()
    {
        type = InputKeyboard,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = 0 } },
    };

    internal static INPUT KeyUp(ushort vk) => new()
    {
        type = InputKeyboard,
        U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = KeyEventFKeyUp } },
    };
}
