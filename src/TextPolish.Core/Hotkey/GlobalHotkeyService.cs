using System.Runtime.InteropServices;
using System.Windows.Forms;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Hotkey;

/// <summary>
/// WH_KEYBOARD_LL のグローバルフックでPush-to-talkのキー押下/離上を検出する。
/// RegisterHotKeyではキーアップイベントを扱いにくいため、低レベルフックを採用している。
/// フックのコールバックが呼ばれるにはメッセージポンプ(WPFのDispatcher等)が動作しているスレッドで
/// Start()を呼ぶ必要がある。
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }

    private readonly IAppLogger _logger;
    private readonly LowLevelKeyboardProc _proc;
    private readonly bool _suppressKey;
    private IntPtr _hookId = IntPtr.Zero;
    private HotkeyDefinition _hotkey = HotkeyDefinition.None;
    private bool _isKeyDown;
    private SynchronizationContext? _syncContext;

    public event EventHandler? HotkeyDown;
    public event EventHandler? HotkeyUp;

    /// <param name="suppressKey">
    /// ホットキーを他のアプリへ渡さない。既定で有効。
    /// 通さないと、例えばF10のようにWindowsが特別扱いするキー(メニューバーの起動)では
    /// 対象アプリのフォーカスが奪われ、直後の操作(選択テキストのコピー等)が失敗する。
    /// </param>
    public GlobalHotkeyService(IAppLogger logger, string initialKeyName, bool suppressKey = true)
    {
        _logger = logger;
        _proc = HookCallback;
        _suppressKey = suppressKey;
        UpdateKey(initialKeyName);
    }

    public void UpdateKey(string keyName)
    {
        _isKeyDown = false;

        if (HotkeyDefinition.TryParse(keyName, out var hotkey))
        {
            _hotkey = hotkey;
            _logger.Info($"ホットキーを '{_hotkey}' に設定しました。");
        }
        else
        {
            _logger.Warn($"ホットキー '{keyName}' を解釈できませんでした。既定のF9を使用します。");
            _hotkey = new HotkeyDefinition(HotkeyModifiers.None, Keys.F9);
        }
    }

    /// <summary>
    /// 現在押されている修飾キーを取得する。
    /// フックのコールバック時点では物理的なキー状態が既に反映されているため、
    /// 修飾キーの押下をフック側で別途追跡しなくても判定できる。
    /// </summary>
    private static HotkeyModifiers CurrentModifiers()
    {
        var modifiers = HotkeyModifiers.None;

        if (IsPressed(VK_CONTROL))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (IsPressed(VK_SHIFT))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (IsPressed(VK_MENU))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (IsPressed(VK_LWIN) || IsPressed(VK_RWIN))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsPressed(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    public void Start()
    {
        if (_hookId != IntPtr.Zero)
        {
            return;
        }

        // Start()を呼んだスレッド(WPFのDispatcherスレッド)のSynchronizationContextを保持しておき、
        // フックコールバックからのイベント発火はここにPostする(下記HookCallbackのコメント参照)。
        _syncContext = SynchronizationContext.Current;

        using var curProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule;
        var moduleHandle = curModule is null ? IntPtr.Zero : GetModuleHandle(curModule.ModuleName);
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, moduleHandle, 0);

        if (_hookId == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.Error($"グローバルキーボードフックの設定に失敗しました。Win32Error={err}");
        }
        else
        {
            _logger.Info("グローバルホットキー監視を開始しました。");
        }
    }

    public void Stop()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var msg = wParam.ToInt32();

            if (data.vkCode == (int)_hotkey.Key)
            {
                var isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                var isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;
                var consumed = false;

                // 修飾キーは完全一致を要求する。Ctrl+W に対して Ctrl+Shift+W を
                // 拾ってしまうと、別の操作を横取りすることになるため。
                if (isDown && !_isKeyDown && CurrentModifiers() == _hotkey.Modifiers)
                {
                    _isKeyDown = true;
                    consumed = true;
                    RaiseHotkeyEvent(HotkeyDown);
                }
                else if (isUp && _isKeyDown)
                {
                    // 押下を消費した以上、離上も必ず消費する。
                    // ここで修飾キーの状態を見ないのは、主キーより先に修飾キーを
                    // 離された場合に、対にならないキーイベントが下流へ漏れるため。
                    _isKeyDown = false;
                    consumed = true;
                    RaiseHotkeyEvent(HotkeyUp);
                }

                // ホットキーとして消費したときだけ止める。
                // 主キーが一致しても修飾キーが違う場合は通常の入力なので下流へ流す
                // (「Ctrl+Shift+W」を割り当てても、単独の W はそのまま入力できる)。
                if (consumed && _suppressKey)
                {
                    return (IntPtr)1;
                }
            }
        }

        // WH_KEYBOARD_LLのコールバックはメッセージポンプの一部として同期的に呼ばれるため、
        // ここで直接async voidハンドラーを呼び出すと、ハンドラー内で待っているWinRTの非同期完了通知が
        // 同じスレッドのメッセージポンプ再開を必要とする場合にフックコールバックが返らず
        // デッドロック状態になり得る(実機検証でPush-to-talk解放後にStopAsyncが完了しない現象を確認)。
        // そのため、イベント発火はSynchronizationContext.Postで新しいメッセージとしてキューイングし、
        // フックコールバック自体は即座にCallNextHookExへ抜ける。
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void RaiseHotkeyEvent(EventHandler? handler)
    {
        if (handler is null)
        {
            return;
        }

        if (_syncContext is not null)
        {
            _syncContext.Post(_ => handler(this, EventArgs.Empty), null);
        }
        else
        {
            handler(this, EventArgs.Empty);
        }
    }

    public void Dispose() => Stop();
}
