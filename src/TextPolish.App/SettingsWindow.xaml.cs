using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TextPolish.Core.Configuration;
using TextPolish.Core.Logging;

namespace TextPolish.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<AppSettings> _onSaved;
    private readonly IAppLogger _logger;
    private string _pendingPolishHotkey;
    private string _pendingQuickHotkey;

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
        LoadQuickPresets();
        PresetSummaryText.Text = "登録済み: " +
            string.Join(" / ", settings.Polish.Presets.Select(p => p.Name));

        LoadLlmSettings();
    }

    private void PolishHotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.LWin or Key.RWin)
        {
            return;
        }

        _pendingPolishHotkey = key.ToString();
        PolishHotkeyTextBox.Text = _pendingPolishHotkey;
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
        _settings.Polish.QuickHotkey = _pendingQuickHotkey;
        _settings.Polish.QuickPresetName =
            QuickPresetCombo.SelectedItem as string ?? _settings.Polish.QuickPresetName;

        _settings.LlmProvider =
            GeminiRadio.IsChecked == true ? "Gemini"
            : OpenAiCompatibleRadio.IsChecked == true ? "OpenAiCompatible"
            : "AzureOpenAI";

        _settings.AzureOpenAI.Endpoint = AzureEndpointBox.Text.Trim();
        _settings.AzureOpenAI.ApiKey = AzureApiKeyBox.Password;
        _settings.AzureOpenAI.Deployment = AzureDeploymentBox.Text.Trim();
        _settings.AzureOpenAI.ApiVersion = AzureApiVersionBox.Text.Trim();

        _settings.Gemini.Endpoint = GeminiEndpointBox.Text.Trim();
        _settings.Gemini.ApiKey = GeminiApiKeyBox.Password;
        _settings.Gemini.Model = GeminiModelBox.Text.Trim();
        _settings.Gemini.AuthMode =
            (GeminiAuthModeCombo.SelectedItem as ComboBoxItem)?.Content as string ?? "ApiKeyQueryParam";

        _settings.OpenAiCompatible.Endpoint = OpenAiEndpointBox.Text.Trim();
        _settings.OpenAiCompatible.ApiKey = OpenAiApiKeyBox.Password;
        _settings.OpenAiCompatible.Model = OpenAiModelBox.Text.Trim();

        _onSaved(_settings);
        Close();
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

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Tab or Key.LWin or Key.RWin)
        {
            return;
        }

        _pendingQuickHotkey = key.ToString();
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
