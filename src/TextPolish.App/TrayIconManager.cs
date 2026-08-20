using System.Windows.Forms;

namespace TextPolish.App;

/// <summary>Phase 3: システムトレイ常駐(NotifyIcon)。設定画面表示と終了を提供する。</summary>
public sealed class TrayIconManager : IDisposable
{
    private const string BaseText = "テキスト整形ツール";
    private readonly NotifyIcon _notifyIcon;

    public TrayIconManager(Action onSettingsRequested, Action onExitRequested)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("設定(&S)...", null, (_, _) => onSettingsRequested());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("終了(&X)", null, (_, _) => onExitRequested());

        _notifyIcon = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            Text = $"{BaseText} - 起動中",
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => onSettingsRequested();
    }

    public void SetStatus(string status)
    {
        _notifyIcon.Text = Truncate($"{BaseText} - {status}", 63);
    }

    public void ShowBalloon(string title, string message, ToolTipIcon icon)
    {
        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = icon;
        _notifyIcon.ShowBalloonTip(5000);
    }

    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : text[..maxLength];

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
