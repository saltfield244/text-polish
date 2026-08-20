using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using TextPolish.Core.Automation;
using TextPolish.Core.Configuration;
using TextPolish.Core.History;
using TextPolish.Core.Diff;
using TextPolish.Core.Llm;
using TextPolish.Core.Logging;
// WinForms(トレイアイコン用)にも同名の型があるため、WPF側を明示する。
using Brush = System.Windows.Media.Brush;
using CheckBox = System.Windows.Controls.CheckBox;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace TextPolish.App;

/// <summary>
/// 選択テキストの整形パレット。
/// プリセットまたは自由指示でLLMに整形させ、結果を確認してから元の位置へ書き戻す。
/// </summary>
public partial class PaletteWindow : Window
{
    /// <summary>
    /// この書き換え率を超えたら、整形直後は差分を表示しない。
    /// 原文がほとんど残らない場合、差分が「全部削除して全部追加」に近づき読む意味がないため。
    ///
    /// 実測値は 誤変換だけ直す 約39% / 要約 約63% / 英訳 約97%。
    /// 0.5 は要約と翻訳を落とし、文体系のプリセットだけ差分を出すための線引き。
    /// 書き換えの大きいビジネスメールも巻き込む可能性はあるが、
    /// その場合はパレットのチェックを入れれば見られる。
    /// </summary>
    private const double DiffAutoHideThreshold = 0.5;

    private static readonly Brush RemovedBackground = new SolidColorBrush(Color.FromRgb(0xFD, 0xE7, 0xE7));
    private static readonly Brush RemovedForeground = new SolidColorBrush(Color.FromRgb(0xA3, 0x2D, 0x2D));
    private static readonly Brush AddedBackground = new SolidColorBrush(Color.FromRgb(0xE4, 0xF3, 0xE7));
    private static readonly Brush AddedForeground = new SolidColorBrush(Color.FromRgb(0x1E, 0x6B, 0x33));

    private readonly CapturedSelection _selection;
    private readonly PolishSettings _polishSettings;
    private readonly Func<ILlmFormatter> _formatterFactory;
    private readonly ITextInserter _textInserter;
    private readonly string? _activeAppName;
    private readonly string? _surroundingContext;
    private readonly InstructionHistory _history;
    private readonly IAppLogger _logger;

    private readonly List<CheckBox> _presetToggles = new();

    private bool _isRunning;
    private CancellationTokenSource? _cts;

    public PaletteWindow(
        CapturedSelection selection,
        PolishSettings polishSettings,
        Func<ILlmFormatter> formatterFactory,
        ITextInserter textInserter,
        string? activeAppName,
        string? surroundingContext,
        InstructionHistory history,
        IAppLogger logger)
    {
        InitializeComponent();

        _selection = selection;
        _polishSettings = polishSettings;
        _formatterFactory = formatterFactory;
        _textInserter = textInserter;
        _activeAppName = activeAppName;
        _surroundingContext = surroundingContext;
        _history = history;
        _logger = logger;

        OriginalTextBox.Text = selection.Text;
        BuildPresetToggles();
        InstructionBox.ItemsSource = _history.Entries;
        PositionNearCursor();
    }

    /// <summary>
    /// プリセットをチェックボックスとして並べる。
    /// 選択したものを組み合わせて1回のリクエストで整形するため、押した時点では実行しない。
    /// </summary>
    private void BuildPresetToggles()
    {
        foreach (var preset in _polishSettings.Presets)
        {
            var checkBox = new CheckBox
            {
                Content = preset.Name,
                Margin = new Thickness(0, 0, 12, 8),
                Tag = preset,
            };

            checkBox.Checked += PresetCheckBox_Checked;
            _presetToggles.Add(checkBox);
            PresetPanel.Children.Add(checkBox);
        }
    }

    /// <summary>
    /// 同じ排他グループのプリセットが既に選ばれていれば外す。
    /// 「誤変換だけ直す」(文体を変えるな)と「ビジネスメール」(ですます調にしろ)のように、
    /// 同時に指定すると指示が矛盾する組み合わせを作れないようにするため。
    /// </summary>
    private void PresetCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox changed || changed.Tag is not FormatPreset preset)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(preset.Group))
        {
            return;
        }

        foreach (var other in _presetToggles)
        {
            if (ReferenceEquals(other, changed) || other.IsChecked != true)
            {
                continue;
            }

            if (other.Tag is FormatPreset otherPreset &&
                string.Equals(otherPreset.Group, preset.Group, StringComparison.OrdinalIgnoreCase))
            {
                // Uncheckedイベントは購読していないため、ここから再帰的に呼ばれることはない。
                other.IsChecked = false;
            }
        }
    }

    /// <summary>チェックされたプリセットを、パレットの並び順(＝適用順)で返す。</summary>
    private List<FormatPreset> GetSelectedPresets() =>
        _presetToggles
            .Where(toggle => toggle.IsChecked == true)
            .Select(toggle => (FormatPreset)toggle.Tag)
            .ToList();

    /// <summary>マウスカーソルの近くに開く。画面外にはみ出さないよう補正する。</summary>
    private void PositionNearCursor()
    {
        var cursor = System.Windows.Forms.Cursor.Position;
        var screen = System.Windows.Forms.Screen.FromPoint(cursor).WorkingArea;

        double left = cursor.X + 16;
        double top = cursor.Y + 16;

        if (left + Width > screen.Right)
        {
            left = screen.Right - Width - 8;
        }

        if (top + Height > screen.Bottom)
        {
            top = screen.Bottom - Height - 8;
        }

        Left = Math.Max(screen.Left, left);
        Top = Math.Max(screen.Top, top);
    }

    private async Task RunAsync(IReadOnlyList<string> instructions, string label)
    {
        if (_isRunning)
        {
            return;
        }

        _isRunning = true;

        // 中止判定用にローカルへ保持する。タイムアウトもユーザー中止も同じ例外型で飛んでくるため、
        // このトークンが実際にキャンセルされたかどうかで振り分ける。
        var cts = new CancellationTokenSource();
        _cts = cts;
        SetBusy(true, $"「{label}」で整形中...(「中止」で取り消せます)");

        try
        {
            var formatter = _formatterFactory();
            var result = await formatter.FormatAsync(
                new LlmFormatRequest
                {
                    RawText = _selection.Text,
                    ActiveAppName = _activeAppName,
                    SurroundingContextText = _surroundingContext,
                    TaskInstructions = instructions,
                },
                cts.Token);

            ResultTextBox.Text = result;

            var diffSuppressed = InitializeDiffView();
            SetBusy(false, diffSuppressed
                ? "整形しました。書き換えが大きいため差分は表示していません。"
                : "整形しました。内容を確認して「置き換える」を押してください。");
            _logger.Info($"整形しました({label})。{_selection.Text.Length}文字 → {result.Length}文字");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            SetBusy(false, "整形を中止しました。");
            _logger.Info($"整形を中止しました({label})。");
        }
        catch (TaskCanceledException)
        {
            // ユーザー中止は上のフィルターで処理済みなので、ここに来るのはHttpClientのタイムアウト。
            SetBusy(false, "タイムアウトしました。RequestTimeoutSeconds を延ばすか ReasoningEffort を下げてください。");
            _logger.Error($"LLM呼び出しがタイムアウトしました({label})。");
        }
        catch (Exception ex)
        {
            _logger.Error("整形に失敗しました。", ex);
            SetBusy(false, $"整形に失敗しました: {ex.Message}");
        }
        finally
        {
            _isRunning = false;
            _cts = null;
            cts.Dispose();
        }
    }

    private void SetBusy(bool busy, string status)
    {
        StatusText.Text = status;
        PresetPanel.IsEnabled = !busy;

        // 実行中は「実行」ボタンを中止ボタンとして使う(押せる状態のまま役割だけ入れ替える)。
        RunButton.Content = busy ? "中止" : "実行";

        // 実行中に古い結果を書き戻せてしまわないよう、整形中は「置き換える」を無効にする。
        ApplyButton.IsEnabled = !busy && !string.IsNullOrWhiteSpace(ResultTextBox.Text);
        Cursor = busy ? System.Windows.Input.Cursors.Wait : System.Windows.Input.Cursors.Arrow;
    }

    private async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _cts?.Cancel();
            return;
        }

        await RunSelectedAsync();
    }

    private async void InstructionBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSelectedAsync();
        }
    }

    /// <summary>
    /// 選択されたプリセットと自由指示をまとめて1回分の手順にして実行する。
    /// 自由指示はプリセットを上書きせず、最後の手順として追加する
    /// (「ビジネスメール」+「もっと短く」のような使い方ができるようにするため)。
    /// </summary>
    private async Task RunSelectedAsync()
    {
        var presets = GetSelectedPresets();
        var freeInstruction = InstructionBox.Text.Trim();

        var instructions = presets.Select(p => p.Instruction).ToList();
        var labels = presets.Select(p => p.Name).ToList();

        if (!string.IsNullOrWhiteSpace(freeInstruction))
        {
            instructions.Add(freeInstruction);
            labels.Add("自由指示");

            // 履歴はファイルに残すだけで、開いているドロップダウンは更新しない。
            // 編集可能なComboBoxはItemsSourceを差し替えると入力中のTextが消えるため。
            _history.Add(freeInstruction);
        }

        if (instructions.Count == 0)
        {
            StatusText.Text = "整形方法を選ぶか、自由指示を入力してください。";
            return;
        }

        await RunAsync(instructions, string.Join("＋", labels));
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var text = ResultTextBox.Text;
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        // パレットを閉じてから、取得元のウィンドウへフォーカスを戻す。
        // 選択状態が残っているため、貼り付けで置換になる。
        Hide();

        if (!WindowFocus.Restore(_selection.SourceWindow))
        {
            _logger.Warn("元のウィンドウへフォーカスを戻せませんでした。書き戻しに失敗する可能性があります。");
        }

        await Task.Delay(150);

        var inserted = await _textInserter.InsertAsync(text);
        if (!inserted)
        {
            _logger.Error("整形結果の書き戻しに失敗しました。");
        }

        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>パレットを閉じたら、実行中のLLM呼び出しも打ち切る。</summary>
    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }

    /// <summary>
    /// 整形直後の差分表示の初期状態を決める。
    /// 書き換え率が高いと差分は「全部削除して全部追加」に近づき読む意味がないため、
    /// その場合は既定ONでも初期状態では出さない(チェックを入れれば見られる)。
    /// </summary>
    /// <returns>書き換えが大きく、差分表示を自動的に見送った場合は true。</returns>
    private bool InitializeDiffView()
    {
        ShowDiffCheckBox.IsEnabled = true;

        var diff = TextDiff.Compute(_selection.Text, ResultTextBox.Text);
        var suppressed = diff.IsTooLong || diff.RewriteRatio > DiffAutoHideThreshold;

        _logger.Info($"書き換え率: {diff.RewriteRatio:P1}{(suppressed ? "(差分表示を見送り)" : string.Empty)}");

        ShowDiffCheckBox.IsChecked = _polishSettings.ShowDiffByDefault && !suppressed;
        UpdateResultView();

        return suppressed && _polishSettings.ShowDiffByDefault;
    }

    private void ShowDiffCheckBox_Click(object sender, RoutedEventArgs e) => UpdateResultView();

    /// <summary>
    /// 差分表示と編集可能なテキストボックスを切り替える。
    /// 書き戻しに使うのは常に ResultTextBox の内容なので、差分表示中も中身は保持している。
    /// </summary>
    private void UpdateResultView()
    {
        var showDiff = ShowDiffCheckBox.IsChecked == true;

        if (showDiff)
        {
            RenderDiff();
        }

        DiffScroll.Visibility = showDiff ? Visibility.Visible : Visibility.Collapsed;
        ResultTextBox.Visibility = showDiff ? Visibility.Collapsed : Visibility.Visible;
        ResultLabel.Text = showDiff
            ? "整形結果(差分表示。編集するにはチェックを外す)"
            : "整形結果(編集できます)";
    }

    private void RenderDiff()
    {
        DiffTextBlock.Inlines.Clear();

        var diff = TextDiff.Compute(_selection.Text, ResultTextBox.Text);

        if (diff.IsTooLong)
        {
            DiffTextBlock.Inlines.Add(new Run($"テキストが長いため差分を表示できません(上限 {TextDiff.MaxLength} 文字)。")
            {
                Foreground = System.Windows.Media.Brushes.Gray,
            });
            return;
        }

        foreach (var segment in diff.Segments)
        {
            var run = new Run(segment.Text);

            switch (segment.Kind)
            {
                case DiffKind.Removed:
                    run.Background = RemovedBackground;
                    run.Foreground = RemovedForeground;
                    run.TextDecorations = TextDecorations.Strikethrough;
                    break;

                case DiffKind.Added:
                    run.Background = AddedBackground;
                    run.Foreground = AddedForeground;
                    break;
            }

            DiffTextBlock.Inlines.Add(run);
        }
    }
}
