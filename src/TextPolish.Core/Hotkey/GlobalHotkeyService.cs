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
    private int _targetVkCode;
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

        if (Enum.TryParse<Keys>(keyName, ignoreCase: true, out var key))
        {
            _targetVkCode = (int)key;
            _logger.Info($"ホットキーを '{keyName}' (VK=0x{_targetVkCode:X2}) に設定しました。");
        }
        else
        {
            _logger.Warn($"ホットキー名 '{keyName}' を解釈できませんでした。既定のF9を使用します。");
            _targetVkCode = (int)Keys.F9;
        }
    }

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

            if (data.vkCode == _targetVkCode)
            {
                if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) && !_isKeyDown)
                {
                    _isKeyDown = true;
                    RaiseHotkeyEvent(HotkeyDown);
                }
                else if ((msg == WM_KEYUP || msg == WM_SYSKEYUP) && _isKeyDown)
                {
                    _isKeyDown = false;
                    RaiseHotkeyEvent(HotkeyUp);
                }

                // 押下・離上の両方を止める。片方だけ通すと、対象アプリが
                // 対になっていないキーイベントを受け取って誤動作する。
                if (_suppressKey)
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
