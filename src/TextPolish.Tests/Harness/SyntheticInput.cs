using System.Runtime.InteropServices;

namespace TextPolish.Tests.Harness;

/// <summary>
/// キーとマウスを自分で送る。
///
/// なぜ要るか(マウス): トレイアイコンの右クリックメニュー
/// (<c>TrayIconManager</c> の <c>ContextMenuStrip</c> = 実機確認の D11・D12)を出す
/// 手段が UIA には無い。アイコン自身は <c>Invoke</c> を持つが、それは左クリック相当で
/// メニューは出ない。<c>ExpandCollapse</c> も持っていない。そのため
/// <c>BoundingRectangle</c> の中心へ実際にマウスを送る。これが唯一、座標に頼る操作。
///
/// なぜ要るか(キー): 整形パレットはホットキーでしか開かない。
/// <c>SendKeys.SendWait</c> は journal フックを使い、メッセージループを回していない
/// コンソールから呼ぶと待ちに入って返ってこない(実際に止まった)。アプリ本体も
/// <c>NativeMethods</c> で <c>SendInput</c> を使っているので、同じ方法に揃える。
///
/// アプリ本体の <c>NativeMethods</c> には <c>KeyDown</c>/<c>KeyUp</c> しか無く、
/// <c>MOUSEINPUT</c> は共用体の大きさを合わせるために宣言されているだけで、
/// 送出のヘルパーは無い。本体に要らないものを足さないため、ここに閉じて置く。
/// </summary>
internal static class SyntheticInput
{
    private const int InputMouse = 0;
    private const int InputKeyboard = 1;
    private const uint MouseEventFRightDown = 0x0008;
    private const uint MouseEventFRightUp = 0x0010;
    private const uint KeyEventFKeyUp = 0x0002;

    public const ushort VkControl = 0x11;
    public const ushort VkEscape = 0x1B;
    public const ushort VkShift = 0x10;
    public const ushort VkA = 0x41;
    public const ushort VkW = 0x57;
    public const ushort VkF10 = 0x79;

    private static bool _dpiChecked;

    /// <summary>
    /// 指した座標で右クリックする。終わったら元のカーソル位置へ戻す。
    /// 利用者のデスクトップを借りているので、使ったら返す。
    /// </summary>
    public static bool RightClick(int x, int y)
    {
        EnsureDpiAware();

        var restore = GetCursorPos(out var before);

        if (!SetCursorPos(x, y))
        {
            return false;
        }

        // 置いた直後に押すと、相手がまだ座標を拾っていないことがある。
        Thread.Sleep(80);

        var ok = SendMouse(MouseEventFRightDown) && SendMouse(MouseEventFRightUp);

        if (restore)
        {
            SetCursorPos(before.X, before.Y);
        }

        return ok;
    }

    /// <summary>1つの鍵を押して離す。</summary>
    public static bool Tap(ushort key) => SendKey(key, false) && SendKey(key, true);

    /// <summary>Ctrl を押しながら1つの鍵を押す。全選択(Ctrl+A)に使う。</summary>
    public static bool TapWithControl(ushort key)
    {
        if (!SendKey(VkControl, false))
        {
            return false;
        }

        var ok = SendKey(key, false) && SendKey(key, true);

        // 離すのは必ず行う。押したままにすると利用者の操作に影響が残る。
        return SendKey(VkControl, true) && ok;
    }

    /// <summary>
    /// Ctrl と Shift を押しながら1つの鍵を押す。即実行(Ctrl+Shift+W)に使う。
    ///
    /// <paramref name="holdMs"/> は、鍵を離す前に修飾キーを押したままにする時間。
    /// 0 にしてはいけない。アプリはホットキーのフックの中から
    /// <c>Ctrl+C</c> を送出するため、こちらが即座に Ctrl を離すと、アプリの
    /// Ctrl 押下とこちらの Ctrl 解放が入れ替わり、**C が素の文字として相手のアプリへ
    /// 打ち込まれる**(実際に、選択していた34文字が「c」1文字に置き換わった)。
    /// 人が押す場合は指が乗っているので起きない。人の打鍵に合わせて待つ。
    /// </summary>
    public static bool TapWithControlShift(ushort key, int holdMs = 250)
    {
        if (!SendKey(VkControl, false) || !SendKey(VkShift, false))
        {
            return false;
        }

        var ok = SendKey(key, false) && SendKey(key, true);

        Thread.Sleep(Math.Max(1, holdMs));

        // 離すのは必ず行う。押したままにすると利用者の操作に影響が残る。
        var released = SendKey(VkShift, true) && SendKey(VkControl, true);
        return released && ok;
    }

    private static bool SendKey(ushort key, bool up)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInputData
                {
                    VirtualKey = key,
                    Flags = up ? KeyEventFKeyUp : 0,
                },
            },
        };

        return Send(input);
    }

    private static bool SendMouse(uint flags)
    {
        var input = new Input
        {
            Type = InputMouse,
            Data = new InputUnion
            {
                Mouse = new MouseInputData { Flags = flags },
            },
        };

        return Send(input);
    }

    private static bool Send(Input input)
    {
        // SendInput は cbSize が合わないと何も送らずに 0 を返す。
        // 戻り値を見ないと「送ったつもり」で先へ進んでしまう。
        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<Input>());
        return sent == 1;
    }

    /// <summary>
    /// 高DPI(拡大表示)のPCで座標がずれないようにする。
    ///
    /// DPI を知らないプロセスが受け取る座標は OS に拡大・縮小され、UIA が返す
    /// 実際の画素とは別物になる。このPCは 96dpi(100%)なので差は出ないが、
    /// 他のPCで静かに外れた場所を押すのを防ぐため、最初に申告しておく。
    /// 古い Windows では関数が無いので、失敗しても進む。
    /// </summary>
    private static void EnsureDpiAware()
    {
        if (_dpiChecked)
        {
            return;
        }

        _dpiChecked = true;

        try
        {
            // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            SetProcessDpiAwarenessContext(new IntPtr(-4));
        }
        catch (EntryPointNotFoundException)
        {
        }
        catch (DllNotFoundException)
        {
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public int Type;
        public InputUnion Data;
    }

    /// <summary>
    /// 共用体。大きい方(<see cref="MouseInputData"/>)に合わせて場所を取る。
    /// 大きさが合わないと <c>SendInput</c> は何も送らない。
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MouseInputData Mouse;

        [FieldOffset(0)]
        public KeyboardInputData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputData
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInputData
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);
}
