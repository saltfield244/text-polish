namespace TextPolish.Core.Configuration;

public sealed class HotkeySettings
{
    // TODO: Push-to-talkの既定キーは未確定。他アプリ(IME/OS)と衝突しにくいキーを要検討のうえ暫定でF9とする。
    //       設定画面(SettingsWindow)またはappsettings.jsonから変更可能。
    public string Key { get; set; } = "F9";
}
