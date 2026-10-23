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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    internal static string GetForegroundWindowTitle()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return "(none)";
        }

        var sb = new System.Text.StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        return $"{sb} [hwnd=0x{hwnd:X}]";
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
