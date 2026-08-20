using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Forms;
using TextPolish.Core.Automation;
using TextPolish.Core.Configuration;
using TextPolish.Core.History;
using TextPolish.Core.Hotkey;
using TextPolish.Core.Llm;
using TextPolish.Core.Logging;
using Application = System.Windows.Application;

namespace TextPolish.App;

public partial class App : Application
{
    private IAppLogger _logger = null!;
    private AppSettings _settings = null!;
    private string _settingsFilePath = null!;
    private TrayIconManager _trayIconManager = null!;
    private GlobalHotkeyService? _polishHotkeyService;
    private GlobalHotkeyService? _quickHotkeyService;
    private HttpClient? _httpClient;
    private PaletteWindow? _paletteWindow;
    private InstructionHistory _history = null!;

    /// <summary>即実行の多重起動を防ぐ。パレットと違い画面が出ないため状態が見えない。</summary>
    private bool _quickRunning;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _logger = new FileLogger();
        _logger.Info("アプリケーションを起動します。");

        _settingsFilePath = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        _settings = AppSettingsLoader.Load(_settingsFilePath);

        _trayIconManager = new TrayIconManager(
            onSettingsRequested: ShowSettingsWindow,
            onExitRequested: () => Shutdown());

        // HttpClient.Timeout は生成後に変更できないため、設定変更時は再起動が必要
        // (OnSettingsSavedのログでその旨を案内している)。
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, _settings.RequestTimeoutSeconds)),
        };

        _history = new InstructionHistory(_logger);
        _history.Load();

        InitializePolishHotkey();
        InitializeQuickHotkey();
        _trayIconManager.SetStatus("待機中");
    }

    private void InitializePolishHotkey()
    {
        if (!_settings.Polish.Enabled)
        {
            _logger.Info("整形パレットは無効化されています(Polish.Enabled=false)。");
            _trayIconManager.SetStatus("無効");
            return;
        }

        _polishHotkeyService = new GlobalHotkeyService(_logger, _settings.Polish.Hotkey);

        // パレットは押し下げではなく離した時に開く(単発トリガーのため)。
        _polishHotkeyService.HotkeyUp += OnPolishHotkey;
        _polishHotkeyService.Start();

        _logger.Info($"整形パレットのホットキーを '{_settings.Polish.Hotkey}' で有効化しました。");
    }

    private async void OnPolishHotkey(object? sender, EventArgs e)
    {
        // 二重起動を防ぐ。既に開いていれば前面に出すだけ。
        if (_paletteWindow is not null)
        {
            _paletteWindow.Activate();
            return;
        }

        try
        {
            _trayIconManager.SetStatus("選択テキストを取得中...");

            var capture = new SelectionCapture(_logger);
            var selection = await capture.CaptureAsync();

            if (selection is null)
            {
                _trayIconManager.ShowBalloon(
                    "テキスト整形ツール",
                    "テキストが選択されていません。整形したい範囲を選択してから実行してください。",
                    ToolTipIcon.Info);
                _trayIconManager.SetStatus("待機中");
                return;
            }

            var context = new ActiveWindowContextProvider(_logger).GetContext();
            var surroundingContext = BuildSurroundingContext(context.SurroundingText, selection.Text);

            // 周辺文脈はUI Automation非対応のアプリでは取得できない。取れているかどうかで
            // 整形精度が変わるため、切り分けられるようログに残す。
            _logger.Info(
                $"アクティブアプリ: {context.AppName ?? "(不明)"} / " +
                $"周辺文脈: {(surroundingContext is null ? "なし" : $"{surroundingContext.Length}文字")}");

            _paletteWindow = new PaletteWindow(
                selection,
                _settings.Polish,
                CreatePolishFormatter,
                CreateTextInserter(),
                context.AppName,
                surroundingContext,
                _history,
                _logger);

            _paletteWindow.Closed += (_, _) =>
            {
                _paletteWindow = null;
                _trayIconManager.SetStatus("待機中");
            };

            _paletteWindow.Show();
            _paletteWindow.Activate();
            _trayIconManager.SetStatus("整形パレット表示中");
        }
        catch (Exception ex)
        {
            _logger.Error("整形パレットの表示に失敗しました。", ex);
            _trayIconManager.SetStatus("エラー");
            _paletteWindow = null;
        }
    }

    private void InitializeQuickHotkey()
    {
        if (!_settings.Polish.Enabled || string.IsNullOrWhiteSpace(_settings.Polish.QuickHotkey))
        {
            return;
        }

        // 同じキーに両方を割り当てると、どちらが動くか不定になる。即実行のほうを諦める。
        if (string.Equals(_settings.Polish.QuickHotkey, _settings.Polish.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warn(
                $"QuickHotkey がパレットのホットキー '{_settings.Polish.Hotkey}' と同じため、即実行を無効にしました。");
            return;
        }

        _quickHotkeyService = new GlobalHotkeyService(_logger, _settings.Polish.QuickHotkey);
        _quickHotkeyService.HotkeyUp += OnQuickHotkey;
        _quickHotkeyService.Start();

        _logger.Info(
            $"即実行のホットキーを '{_settings.Polish.QuickHotkey}' " +
            $"(プリセット: {_settings.Polish.QuickPresetName}) で有効化しました。");
    }

    /// <summary>
    /// パレットを出さずに、既定プリセットで整形して即座に書き戻す。
    /// 確認の機会がないぶん、失敗や空振りはトレイ通知で必ず知らせる。
    /// </summary>
    private async void OnQuickHotkey(object? sender, EventArgs e)
    {
        if (_quickRunning || _paletteWindow is not null)
        {
            return;
        }

        _quickRunning = true;

        try
        {
            _trayIconManager.SetStatus("選択テキストを取得中...");

            var capture = new SelectionCapture(_logger);
            var selection = await capture.CaptureAsync();

            if (selection is null)
            {
                _trayIconManager.ShowBalloon(
                    "テキスト整形ツール",
                    "テキストが選択されていません。整形したい範囲を選択してから実行してください。",
                    ToolTipIcon.Info);
                return;
            }

            var preset = ResolveQuickPreset();
            if (preset is null)
            {
                _trayIconManager.ShowBalloon(
                    "テキスト整形ツール",
                    "即実行に使うプリセットが見つかりません。設定を確認してください。",
                    ToolTipIcon.Warning);
                return;
            }

            var context = new ActiveWindowContextProvider(_logger).GetContext();
            var surroundingContext = BuildSurroundingContext(context.SurroundingText, selection.Text);

            _trayIconManager.SetStatus($"「{preset.Name}」で整形中...");

            var formatter = CreatePolishFormatter();
            var result = await formatter.FormatAsync(new LlmFormatRequest
            {
                RawText = selection.Text,
                ActiveAppName = context.AppName,
                SurroundingContextText = surroundingContext,
                TaskInstructions = new[] { preset.Instruction },
            });

            // パレット経由と違い、こちらは自分でフォーカスを奪っていないが、
            // 整形中にユーザーが別のウィンドウへ移っている可能性があるため戻しておく。
            if (!WindowFocus.Restore(selection.SourceWindow))
            {
                _logger.Warn("元のウィンドウへフォーカスを戻せませんでした。書き戻しに失敗する可能性があります。");
            }

            await Task.Delay(150);

            var inserted = await CreateTextInserter().InsertAsync(result);
            if (inserted)
            {
                _logger.Info($"即実行で整形しました({preset.Name})。{selection.Text.Length}文字 → {result.Length}文字");
            }
            else
            {
                _logger.Error("即実行の書き戻しに失敗しました。");
                _trayIconManager.ShowBalloon(
                    "テキスト整形ツール", "整形結果の書き戻しに失敗しました。", ToolTipIcon.Error);
            }
        }
        catch (Exception ex)
        {
            _logger.Error("即実行に失敗しました。", ex);
            _trayIconManager.ShowBalloon(
                "テキスト整形ツール", $"整形に失敗しました: {ex.Message}", ToolTipIcon.Error);
        }
        finally
        {
            _quickRunning = false;
            _trayIconManager.SetStatus("待機中");
        }
    }

    private FormatPreset? ResolveQuickPreset()
    {
        var byName = _settings.Polish.Presets.FirstOrDefault(p =>
            string.Equals(p.Name, _settings.Polish.QuickPresetName, StringComparison.OrdinalIgnoreCase));

        // 名前が変更・削除されていても黙って止まらないよう、先頭のプリセットで代替する。
        return byName ?? _settings.Polish.Presets.FirstOrDefault();
    }

    /// <summary>
    /// UI Automationで取得した周辺テキストから、整形対象の選択範囲そのものを取り除く。
    ///
    /// 取得できるのはフォーカス中のコントロール全体のテキストで、選択範囲もその中に含まれる。
    /// そのまま渡すと同じ文が「参考情報」と「整形対象」の両方でLLMに渡り、区別が曖昧になるため。
    /// 選択が長く周辺テキスト側が途中で切れている場合は一致せず除去されないが、
    /// その場合も参考情報として害はないのでそのまま渡す。
    /// </summary>
    private static string? BuildSurroundingContext(string? surroundingText, string selectionText)
    {
        if (string.IsNullOrWhiteSpace(surroundingText))
        {
            return null;
        }

        var trimmed = surroundingText.Replace(selectionText, string.Empty).Trim();

        // 選択範囲を除いたら何も残らない(＝選択範囲がコントロールの全文だった)場合は
        // 参考情報がないのと同じなので送らない。
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private ILlmFormatter CreatePolishFormatter()
    {
        var promptPath = ResolvePath(_settings.Polish.SystemPromptFilePath);
        var glossaryPath = ResolvePath(_settings.Polish.GlossaryFilePath);

        // 用語辞書はシステムプロンプトの末尾に追記する形で渡す。
        // 実行のたびに読み直すため、辞書を編集しても再起動は不要。
        ISystemPromptProvider promptProvider = new GlossarySystemPromptProvider(
            new FileSystemPromptProvider(promptPath), glossaryPath, _logger);

        // _settingsを都度参照するため、設定画面でLLMプロバイダを切り替えた場合も
        // アプリ再起動なしで次回実行から反映される。
        return LlmFormatterFactory.Create(_settings, _httpClient!, promptProvider, _logger);
    }

    private ITextInserter CreateTextInserter() => new ClipboardTextInserter(
        _logger,
        _settings.TextInsertion.RestoreClipboard,
        _settings.TextInsertion.RestoreDelayMs);

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private void ShowSettingsWindow()
    {
        // 設定画面を開く直前にファイルから読み直す。
        // アプリ起動中にappsettings.jsonを直接編集された場合、メモリ上の古い内容で
        // 保存すると外部の編集内容(APIキー等)を消してしまうため。
        try
        {
            _settings = AppSettingsLoader.Load(_settingsFilePath);
        }
        catch (Exception ex)
        {
            _logger.Error("設定ファイルの再読み込みに失敗しました。メモリ上の設定を使用します。", ex);
        }

        // 設定画面を開いている間はフックを止める。
        // 有効なままだと、ホットキーとして割り当てようとしたキーをフックが横取りしてしまい、
        // 入力欄に届かない。さらにパレット側の処理が走り、選択テキスト取得のために
        // 送出される Ctrl+C が設定画面に入力されてしまう。
        SuspendHotkeys();

        try
        {
            var window = new SettingsWindow(_settings, OnSettingsSaved, _logger);
            window.ShowDialog();
        }
        finally
        {
            // 保存・キャンセルのどちらで閉じても、その時点の設定でフックを張り直す。
            ReconfigureHotkeys();
        }
    }

    private void OnSettingsSaved(AppSettings updated)
    {
        _settings = updated;
        AppSettingsLoader.Save(_settingsFilePath, _settings);

        // フックの張り直しは設定画面を閉じたあとに ShowSettingsWindow 側で行う。
        _logger.Info(
            "設定を保存しました(ホットキーは即時反映、LLMプロバイダは次回実行から反映、" +
            "RequestTimeoutSeconds は再起動後に反映)。");
    }

    /// <summary>ホットキーのフックを解除する。</summary>
    private void SuspendHotkeys()
    {
        if (_polishHotkeyService is not null)
        {
            _polishHotkeyService.HotkeyUp -= OnPolishHotkey;
            _polishHotkeyService.Dispose();
            _polishHotkeyService = null;
        }

        if (_quickHotkeyService is not null)
        {
            _quickHotkeyService.HotkeyUp -= OnQuickHotkey;
            _quickHotkeyService.Dispose();
            _quickHotkeyService = null;
        }
    }

    /// <summary>
    /// ホットキーのフックを作り直す。
    /// 設定画面から有効・無効を切り替えられるため、キー名の更新だけでは足りない
    /// (起動時に無効だったものを、あとから有効にできない)。
    /// </summary>
    private void ReconfigureHotkeys()
    {
        SuspendHotkeys();

        InitializePolishHotkey();
        InitializeQuickHotkey();

        if (_settings.Polish.Enabled)
        {
            _trayIconManager.SetStatus("待機中");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconManager?.Dispose();

        if (_polishHotkeyService is not null)
        {
            _polishHotkeyService.HotkeyUp -= OnPolishHotkey;
            _polishHotkeyService.Dispose();
        }

        if (_quickHotkeyService is not null)
        {
            _quickHotkeyService.HotkeyUp -= OnQuickHotkey;
            _quickHotkeyService.Dispose();
        }

        _httpClient?.Dispose();
        base.OnExit(e);
    }
}
