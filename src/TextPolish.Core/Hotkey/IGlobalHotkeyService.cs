namespace TextPolish.Core.Hotkey;

/// <summary>Push-to-talk用のグローバルホットキー監視。キー自体はハードコードせず動的に変更可能。</summary>
public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? HotkeyDown;
    event EventHandler? HotkeyUp;

    /// <summary>
    /// 監視対象のホットキーを変更する。アプリ再起動なしで即座に反映される。
    /// 形式は "F10" のような単独キーのほか、"Ctrl+Shift+W" のような修飾キー付きも指定できる。
    /// </summary>
    void UpdateKey(string keyName);

    void Start();
    void Stop();
}
