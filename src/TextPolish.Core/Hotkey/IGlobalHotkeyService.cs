namespace TextPolish.Core.Hotkey;

/// <summary>Push-to-talk用のグローバルホットキー監視。キー自体はハードコードせず動的に変更可能。</summary>
public interface IGlobalHotkeyService : IDisposable
{
    event EventHandler? HotkeyDown;
    event EventHandler? HotkeyUp;

    /// <summary>監視対象キーを変更する。アプリ再起動なしで即座に反映される。</summary>
    void UpdateKey(string keyName);

    void Start();
    void Stop();
}
