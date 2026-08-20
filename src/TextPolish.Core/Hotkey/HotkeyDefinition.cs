using System.Text;
using System.Windows.Forms;

namespace TextPolish.Core.Hotkey;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Windows = 1,
    Control = 2,
    Alt = 4,
    Shift = 8,
}

/// <summary>
/// ホットキーの定義。修飾キーの組み合わせと、主となるキー1つで表す。
///
/// 文字列表現は "Ctrl+Shift+W" や "Win+Alt+X" の形式。
/// 修飾キーなしの "F10" も有効で、これは設定ファイルの旧形式との互換にもなっている。
/// </summary>
public sealed record HotkeyDefinition(HotkeyModifiers Modifiers, Keys Key)
{
    public static readonly HotkeyDefinition None = new(HotkeyModifiers.None, Keys.None);

    public bool IsEmpty => Key == Keys.None;

    /// <summary>
    /// "Ctrl+Shift+W" 形式の文字列を解釈する。
    /// 区切りは '+'、大文字小文字は区別しない。主キーが無い(修飾キーだけ)場合は失敗する。
    /// </summary>
    public static bool TryParse(string? text, out HotkeyDefinition result)
    {
        result = None;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        var key = Keys.None;

        foreach (var rawToken in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = rawToken.Trim();
            if (token.Length == 0)
            {
                continue;
            }

            switch (token.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    continue;

                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    continue;

                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    continue;

                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Windows;
                    continue;
            }

            // 修飾キー以外は主キーとして解釈する。2つ以上あった場合は解釈できないものとして扱う。
            if (key != Keys.None || !Enum.TryParse(token, ignoreCase: true, out Keys parsed))
            {
                return false;
            }

            key = parsed;
        }

        if (key == Keys.None)
        {
            return false;
        }

        result = new HotkeyDefinition(modifiers, key);
        return true;
    }

    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();

        // 表記順は Win → Ctrl → Alt → Shift。一般的な表記(Ctrl+Shift+W / Win+Alt+X)に合う。
        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            sb.Append("Win+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            sb.Append("Ctrl+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            sb.Append("Alt+");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            sb.Append("Shift+");
        }

        sb.Append(Key);
        return sb.ToString();
    }
}
