using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextPolish.Core.Configuration;
using TextPolish.Core.Hotkey;
using TextPolish.Core.Logging;

namespace TextPolish.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _onSaved;
    private readonly IAppLogger _logger;
    private string _pendingPolishHotkey;
    private string _pendingQuickHotkey;

    /// <summary>「削除」ボタンが押されたプロバイダのAPIキー入力欄。</summary>
    private readonly HashSet<PasswordBox> _apiKeyDeleteRequested = new();

    public SettingsWindow(AppSettings settings, Action<AppSettings> onSaved, IAppLogger logger)
    {
        InitializeComponent();
        _settings = settings;
        _onSaved = onSaved;
        _logger = logger;
        _pendingPolishHotkey = settings.Polish.Hotkey;
        _pendingQuickHotkey = settings.Polish.QuickHotkey;

        PolishHotkeyTextBox.Text = _pendingPolishHotkey;
        QuickHotkeyTextBox.Text = _pendingQuickHotkey;
        EnablePolishCheckBox.IsChecked = settings.Polish.Enabled;
        ShowDiffCheckBox.IsChecked = settings.Polish.ShowDiffByDefault;
        SendSurroundingContextCheckBox.IsChecked = settings.Polish.SendSurroundingContext;
        LoadQuickPresets();
        PresetSummaryText.Text = "登録済み: " +
            string.Join(" / ", settings.Polish.Presets.Select(p => p.Name));

        LoadLlmSettings();
    }

    private void PolishHotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        if (!TryCaptureHotkey(e, out var hotkey))
        {
            return;
        }

        _pendingPolishHotkey = hotkey;
        PolishHotkeyTextBox.Text = _pendingPolishHotkey;
        QuickHotkeyWarning.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 押されたキーの組み合わせを "Ctrl+Shift+W" 形式の文字列にする。
    /// 修飾キー単独の押下(Ctrlだけ等)は、まだ組み合わせが確定していないので無視する。
    /// </summary>
    private static bool TryCaptureHotkey(System.Windows.Input.KeyEventArgs e, out string hotkey)
    {
        hotkey = string.Empty;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt
                or Key.LWin or Key.RWin
                or Key.Tab)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        // Windowsキーは Keyboard.Modifiers に現れないことがあるため個別に見る。
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        var winFormsKey = (System.Windows.Forms.Keys)KeyInterop.VirtualKeyFromKey(key);
        hotkey = new HotkeyDefinition(modifiers, winFormsKey).ToString();

        return hotkey.Length > 0;
    }

    private void LoadLlmSettings()
    {
        AzureEndpointBox.Text = _settings.AzureOpenAI.Endpoint;
        AzureApiKeyBox.Password = _settings.AzureOpenAI.ApiKey;
        AzureDeploymentBox.Text = _settings.AzureOpenAI.Deployment;
        AzureApiVersionBox.Text = _settings.AzureOpenAI.ApiVersion;

        GeminiEndpointBox.Text = _settings.Gemini.Endpoint;
        GeminiApiKeyBox.Password = _settings.Gemini.ApiKey;
        GeminiModelBox.Text = _settings.Gemini.Model;
        SelectComboByText(GeminiAuthModeCombo, _settings.Gemini.AuthMode);

        OpenAiEndpointBox.Text = _settings.OpenAiCompatible.Endpoint;
        OpenAiApiKeyBox.Password = _settings.OpenAiCompatible.ApiKey;
        OpenAiModelBox.Text = _settings.OpenAiCompatible.Model;

        ShowApiKeyStatus(AzureApiKeyBox, AzureApiKeyStatus, _settings.AzureOpenAI);
        ShowApiKeyStatus(GeminiApiKeyBox, GeminiApiKeyStatus, _settings.Gemini);
        ShowApiKeyStatus(OpenAiApiKeyBox, OpenAiApiKeyStatus, _settings.OpenAiCompatible);

        if (string.Equals(_settings.LlmProvider, "Gemini", StringComparison.OrdinalIgnoreCase))
        {
            GeminiRadio.IsChecked = true;
        }
        else if (string.Equals(_settings.LlmProvider, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase))
        {
            OpenAiCompatibleRadio.IsChecked = true;
        }
        else
        {
            AzureOpenAiRadio.IsChecked = true;
        }

        UpdateLlmPanels();
    }

    private static void SelectComboByText(System.Windows.Controls.ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content as string, value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        combo.SelectedIndex = 0;
    }

    /// <summary>選択中のプロバイダの入力欄だけを表示する。</summary>
    private void UpdateLlmPanels()
    {
        AzurePanel.Visibility = AzureOpenAiRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        GeminiPanel.Visibility = GeminiRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OpenAiPanel.Visibility = OpenAiCompatibleRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LlmProviderRadio_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        UpdateLlmPanels();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 同じキーに両方を割り当てると、どちらが動くか不定になるため保存させない。
        if (!string.IsNullOrWhiteSpace(_pendingQuickHotkey) &&
            string.Equals(_pendingQuickHotkey, _pendingPolishHotkey, StringComparison.OrdinalIgnoreCase))
        {
            QuickHotkeyWarning.Text =
                "即実行のホットキーがパレットのホットキーと同じです。別のキーにするか「無効にする」を押してください。";
            QuickHotkeyWarning.Visibility = Visibility.Visible;
            return;
        }

        _settings.Polish.Enabled = EnablePolishCheckBox.IsChecked == true;
        _settings.Polish.Hotkey = _pendingPolishHotkey;
        _settings.Polish.ShowDiffByDefault = ShowDiffCheckBox.IsChecked == true;
        _settings.Polish.SendSurroundingContext = SendSurroundingContextCheckBox.IsChecked == true;
        _settings.Polish.QuickHotkey = _pendingQuickHotkey;
        _settings.Polish.QuickPresetName =
            QuickPresetCombo.SelectedItem as string ?? _settings.Polish.QuickPresetName;

        _settings.LlmProvider =
            GeminiRadio.IsChecked == true ? "Gemini"
            : OpenAiCompatibleRadio.IsChecked == true ? "OpenAiCompatible"
            : "AzureOpenAI";

        _settings.AzureOpenAI.Endpoint = AzureEndpointBox.Text.Trim();
        ApiKeyProtector.ApplyInput(
            _settings.AzureOpenAI, AzureApiKeyBox.Password, _apiKeyDeleteRequested.Contains(AzureApiKeyBox));
        _settings.AzureOpenAI.Deployment = AzureDeploymentBox.Text.Trim();
        _settings.AzureOpenAI.ApiVersion = AzureApiVersionBox.Text.Trim();

        _settings.Gemini.Endpoint = GeminiEndpointBox.Text.Trim();
        ApiKeyProtector.ApplyInput(
            _settings.Gemini, GeminiApiKeyBox.Password, _apiKeyDeleteRequested.Contains(GeminiApiKeyBox));
        _settings.Gemini.Model = GeminiModelBox.Text.Trim();
        _settings.Gemini.AuthMode =
            (GeminiAuthModeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "ApiKeyQueryParam";

        _settings.OpenAiCompatible.Endpoint = OpenAiEndpointBox.Text.Trim();
        ApiKeyProtector.ApplyInput(
            _settings.OpenAiCompatible, OpenAiApiKeyBox.Password, _apiKeyDeleteRequested.Contains(OpenAiApiKeyBox));
        _settings.OpenAiCompatible.Model = OpenAiModelBox.Text.Trim();

        _onSaved(_settings);
        Close();
    }

    /// <summary>
    /// 復号できなかったキーがある場合に知らせる。入力欄は空になるが、保存しても暗号文は消えない
    /// (別のWindowsユーザーで保存した設定をコピーしてきた場合など、元の環境なら読めるため)。
    /// </summary>
    private static void ShowApiKeyStatus(PasswordBox box, TextBlock status, IApiKeySettings keySettings)
    {
        if (!keySettings.ApiKeyUnreadable)
        {
            status.Visibility = Visibility.Collapsed;
            return;
        }

        status.Text = "保存済みのAPIキーを復号できませんでした(別のWindowsユーザー・PCで保存された可能性があります)。" +
                      "入力し直すか、「削除」を押してください。";
        status.Foreground = System.Windows.Media.Brushes.Firebrick;
        status.Visibility = Visibility.Visible;
    }

    private void ApiKeyDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var (box, status) = sender == AzureApiKeyDeleteButton ? (AzureApiKeyBox, AzureApiKeyStatus)
            : sender == GeminiApiKeyDeleteButton ? (GeminiApiKeyBox, GeminiApiKeyStatus)
            : (OpenAiApiKeyBox, OpenAiApiKeyStatus);

        box.Password = string.Empty;
        _apiKeyDeleteRequested.Add(box);

        status.Text = "保存するとAPIキーを削除します。";
        status.Foreground = System.Windows.Media.Brushes.Gray;
        status.Visibility = Visibility.Visible;
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        // 削除を押したあとに入力し直した場合は、その値で更新する。
        if (sender is PasswordBox box && box.Password.Length > 0 && _apiKeyDeleteRequested.Remove(box))
        {
            var status = box == AzureApiKeyBox ? AzureApiKeyStatus
                : box == GeminiApiKeyBox ? GeminiApiKeyStatus
                : OpenAiApiKeyStatus;
            status.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadQuickPresets()
    {
        QuickPresetCombo.ItemsSource = _settings.Polish.Presets.Select(p => p.Name).ToList();
        QuickPresetCombo.SelectedItem = _settings.Polish.Presets
            .Select(p => p.Name)
            .FirstOrDefault(name =>
                string.Equals(name, _settings.Polish.QuickPresetName, StringComparison.OrdinalIgnoreCase));

        if (QuickPresetCombo.SelectedItem is null && QuickPresetCombo.Items.Count > 0)
        {
            QuickPresetCombo.SelectedIndex = 0;
        }
    }

    private void QuickHotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        if (!TryCaptureHotkey(e, out var hotkey))
        {
            return;
        }

        _pendingQuickHotkey = hotkey;
        QuickHotkeyTextBox.Text = _pendingQuickHotkey;
        QuickHotkeyWarning.Visibility = Visibility.Collapsed;
    }

    /// <summary>即実行を無効にする。空文字が「使わない」を表すため、専用のボタンを置いている。</summary>
    private void ClearQuickHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _pendingQuickHotkey = string.Empty;
        QuickHotkeyTextBox.Text = string.Empty;
        QuickHotkeyWarning.Visibility = Visibility.Collapsed;
    }
}
