using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 実機確認手順「H. 周辺文脈の送信ON/OFF」を流す。
///
/// 見るべきことは1つ。**選択していない文章が、設定に反して送られていないか。**
/// v1.0.2 で既定を「送らない」へ変えたので、既定のまま使う利用者の画面から
/// 文章が出ていかないことを押さえる必要がある。
///
/// 手順書はログの「周辺文脈: ...」の行で見る形だが、ログはアプリの自己申告である。
/// ここでは**スタブが受け取ったリクエストの本文**も見る。送信しない設定のときに
/// 選択範囲外の文章が本文に入っていれば、ログが何と言っていても漏えいである。
///
/// そのため取得元の窓は**一部だけを選択**する(<see cref="TextSourceWindow.Select"/>)。
/// 全選択では「選択範囲の外」が存在せず、送られているかを見分けられない。
///
/// H2・H4 は設定画面の「LLMへ送る範囲」を実際に切り替えて保存するため、
/// **<c>appsettings.json</c> を書き換える。** 流した側が戻すこと(README に書いた)。
/// H3 はアプリを再起動する。起こし直したプロセスは別のIDになるため、
/// H3 のあとは新しいIDを見に行く。
/// </summary>
internal static class ChecklistH
{
    /// <summary>選択する部分。この文だけが整形対象になる。</summary>
    private const string Selected = "この文だけをせんたくしてせいけいします。";

    /// <summary>選択しない部分。送信しない設定のとき、ここが1文字も出てはいけない。</summary>
    private const string Around = "H-AROUND-4E88 これは選択していない周辺の文章です。";

    private static readonly string Sample = $"{Around}\r\n{Selected}\r\n{Around}";

    private const string Result = "H-RESULT-4E88";

    private static TextSourceWindow? _source;
    private static Process _app = null!;
    private static string _exePath = string.Empty;

    public static int Run(string name, string[] args, Process app, List<int> pids)
    {
        if (!UiaDriver.EndpointIsLoopback(app, out var endpoint))
        {
            Console.WriteLine("Endpoint がループバックではありません。H は流しません。");
            Console.WriteLine($"  いまの Endpoint: {endpoint}");
            return 1;
        }

        _app = app;
        _exePath = app.MainModule?.FileName ?? string.Empty;

        if (_exePath.Length == 0)
        {
            Console.WriteLine("アプリの場所が読めませんでした。H3 で起こし直せないため流しません。");
            return 1;
        }

        var port = PortOf(endpoint);

        if (port is null)
        {
            Console.WriteLine($"Endpoint からポート番号を読めませんでした: {endpoint}");
            return 1;
        }

        LlmStubServer stub;

        try
        {
            stub = new LlmStubServer(new StubScenario { Text = Result }, port.Value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ポート {port} でスタブを立てられませんでした: {ex.Message}");
            Console.WriteLine("  H はスタブを自分で立てます。--stub は止めてから流してください。");
            return 1;
        }

        Console.WriteLine($"スタブを自分で立てました(ポート {stub.Port})。受け取った本文も検証します。");
        Console.WriteLine($"設定ファイル: {SettingsPath()}");
        Console.WriteLine("**H2・H4 は設定画面から保存するため、この設定ファイルを書き換えます。**");

        var items = new (string Key, Func<List<int>, LlmStubServer, int> Body)[]
        {
            ("h1", H1),
            ("h2", H2),
            ("h3", H3),
            ("h4", H4),
            ("h5", H5),
        };

        using (stub)
        {
            if (name == "h")
            {
                var total = 0;

                foreach (var (key, body) in items)
                {
                    total += RunOne(key, body, pids, stub);

                    // H3 はアプリを起こし直す。以後は新しいプロセスIDを見る。
                    if (key == "h3")
                    {
                        pids = CurrentPids(pids);
                    }
                }

                Console.WriteLine();
                Console.WriteLine(total == 0 ? "H: 流した項目は全部期待どおり。" : $"H: 期待どおりでなかった確認: {total} 件");
                return total == 0 ? 0 : 1;
            }

            var one = items.FirstOrDefault(i => i.Key == name);

            if (one.Body is null)
            {
                Console.WriteLine($"{name} はここでは扱っていません(h1〜h5、または h)。");
                return 1;
            }

            return RunOne(name, one.Body, pids, stub) == 0 ? 0 : 1;
        }
    }

    private static int RunOne(
        string key, Func<List<int>, LlmStubServer, int> body, List<int> pids, LlmStubServer stub)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {key.ToUpperInvariant()} =====");

        CloseAllPalettes(pids);
        stub.Scenario = new StubScenario { Text = Result };

        try
        {
            var failures = body(pids, stub);
            Console.WriteLine(failures == 0 ? "  → 期待どおり" : $"  → 期待どおりでない: {failures} 件");
            return failures;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  → 確認の途中で例外: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            _source?.Dispose();
            _source = null;
            CloseAllPalettes(pids);
        }
    }

    // ------------------------------------------------------------------

    /// <summary>H1: 既定(送信しない)のまま整形する。</summary>
    private static int H1(List<int> pids, LlmStubServer stub)
    {
        var failures = Check(!SettingIsOn(), "設定ファイルは「送信しない」(既定)");

        if (SettingIsOn())
        {
            Console.WriteLine("  いまオンになっています。H1 は既定の状態で見る項目なので、H4 のあとに流してください。");
            return failures;
        }

        return failures + FormatAndCheck(pids, stub, expectOn: false);
    }

    /// <summary>H2: 設定画面でオンにして保存し、整形する。周辺文脈が送られる。</summary>
    private static int H2(List<int> pids, LlmStubServer stub)
    {
        var failures = SetSetting(pids, on: true);

        if (failures > 0)
        {
            return failures;
        }

        failures += Check(SettingIsOn(), "appsettings.json の SendSurroundingContext が true になる");
        return failures + FormatAndCheck(pids, stub, expectOn: true);
    }

    /// <summary>H3: オンのままアプリを再起動する。設定は維持される。</summary>
    private static int H3(List<int> pids, LlmStubServer stub)
    {
        if (!SettingIsOn())
        {
            Console.WriteLine("  オンになっていません。H3 は H2 の続きです。");
            return 1;
        }

        Console.WriteLine("  アプリを終了して起こし直します。");
        _app.Kill();
        _app.WaitForExit(20000);

        _app = Process.Start(new ProcessStartInfo(_exePath) { UseShellExecute = true })
            ?? throw new InvalidOperationException("アプリを起こし直せませんでした。");

        // 起き直したかどうかは**プロセスで**見る。トレイのアイコンは隠れた領域へ
        // たたまれることがあり、出ていないことがアプリの不合格を意味しない
        // (実際に、アイコンが見つからないのに整形はできる回があった)。
        var back = WaitUntil(() => CurrentPids(new List<int>()).Count > 0, 30000, 500);
        var failures = Check(back, "起こし直せる(プロセスがいる)");

        if (!back)
        {
            return failures;
        }

        if (FindTrayItem("テキスト整形ツール") is null)
        {
            Console.WriteLine("  トレイのアイコンは見つかりませんでした(隠れた領域にある可能性があります)。");
        }

        // ホットキーの登録が終わるまで少し待つ。
        Thread.Sleep(2500);
        failures += Check(SettingIsOn(), "再起動してもオンのまま維持される");
        return failures + FormatAndCheck(CurrentPids(pids), stub, expectOn: true);
    }

    /// <summary>H4: 設定画面でオフに戻して整形する。周辺の文章が送られない。</summary>
    private static int H4(List<int> pids, LlmStubServer stub)
    {
        var current = CurrentPids(pids);
        var failures = SetSetting(current, on: false);

        if (failures > 0)
        {
            return failures;
        }

        failures += Check(!SettingIsOn(), "appsettings.json の SendSurroundingContext が false に戻る");
        return failures + FormatAndCheck(current, stub, expectOn: false);
    }

    /// <summary>
    /// H5: オン・オフのどちらでも、アクティブアプリの名前がログに出てプロンプトに入る。
    /// アプリ名による文体の切り分けはこの情報に依存するため、周辺文脈とは別に確かめる。
    /// </summary>
    private static int H5(List<int> pids, LlmStubServer stub)
    {
        var current = CurrentPids(pids);
        var failures = 0;

        foreach (var on in new[] { false, true })
        {
            Console.WriteLine($"  --- 周辺文脈 {(on ? "オン" : "オフ")} ---");
            failures += SetSetting(current, on);

            var log = LogFileAssert.Today();
            var mark = log.LineCount;

            if (Format(current, stub) is null)
            {
                failures++;
                continue;
            }

            var line = log.WaitForLine("アクティブアプリ: ", 10000, mark);
            failures += Check(line is not null, $"{(on ? "オン" : "オフ")}でもログにアプリ名が出る");
            failures += Check(
                line is not null && !line.Contains("アクティブアプリ: (", StringComparison.Ordinal),
                "アプリ名が取得できている");

            var body = stub.LastRequest?.DecodedBody ?? string.Empty;
            failures += Check(
                body.Contains("TextPolish.Tests", StringComparison.OrdinalIgnoreCase),
                "プロンプトにアプリ名が入っている");

            if (line is not null)
            {
                Console.WriteLine($"  ログ: {Trim(line, 110)}");
            }

            _source?.Dispose();
            _source = null;
            CloseAllPalettes(current);
        }

        // 確認のあとは既定(オフ)へ戻しておく。意図せずオンのまま使い続けないため。
        failures += SetSetting(current, on: false);
        return failures;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 一部だけを選択して整形し、ログとスタブの受け取った本文の両方を見る。
    /// </summary>
    private static int FormatAndCheck(List<int> pids, LlmStubServer stub, bool expectOn)
    {
        var log = LogFileAssert.Today();
        var mark = log.LineCount;

        if (Format(pids, stub) is null)
        {
            return 1;
        }

        var line = log.WaitForLine("周辺文脈: ", 10000, mark);
        var failures = Check(line is not null, "ログに「周辺文脈: ...」が出る");

        if (line is not null)
        {
            Console.WriteLine($"  ログ: {Trim(line, 110)}");

            failures += expectOn
                ? Check(
                    line.Contains("文字", StringComparison.Ordinal)
                        && !line.Contains("送信しない設定", StringComparison.Ordinal),
                    "ログが「N文字」になる")
                : Check(
                    line.Contains("送信しない設定(SendSurroundingContext=false)", StringComparison.Ordinal),
                    "ログが「送信しない設定」になる");
        }

        // ログはアプリの自己申告なので、実際に送られた本文も見る。
        var body = stub.LastRequest?.DecodedBody;
        failures += Check(body is not null, "スタブがリクエストを受け取っている");

        if (body is null)
        {
            return failures;
        }

        failures += Check(
            body.Contains(Selected, StringComparison.Ordinal), "選択した文章は送られている");
        failures += expectOn
            ? Check(body.Contains("H-AROUND-4E88", StringComparison.Ordinal), "選択範囲外の文章も送られている")
            : Check(!body.Contains("H-AROUND-4E88", StringComparison.Ordinal), "選択範囲外の文章は送られていない");
        return failures;
    }

    /// <summary>取得元を出し、一部だけを選択してパレットで整形する。</summary>
    private static AutomationElement? Format(List<int> pids, LlmStubServer stub)
    {
        _source = TextSourceWindow.Open(Sample, "試作H(取得元)");

        if (_source is null)
        {
            Console.WriteLine("  取得元の窓を出せませんでした。");
            return null;
        }

        if (!_source.IsForeground && !_source.Activate())
        {
            Console.WriteLine("  取得元を前面にできませんでした。");
            return null;
        }

        // 前面化のあとに選び直す(Activate は全選択に戻すため)。
        _source.Select(Sample.IndexOf(Selected, StringComparison.Ordinal), Selected.Length);

        if (_source.SelectedLength != Selected.Length)
        {
            Console.WriteLine($"  一部だけを選択できませんでした(選択={_source.SelectedLength}文字)。");
            return null;
        }

        stub.Scenario = stub.Scenario with { Outcome = StubOutcome.Success, DelayMs = 0, Text = Result };

        SyntheticInput.Tap(SyntheticInput.VkF10);

        AutomationElement? palette = null;

        if (!WaitUntil(() => (palette = UiaDriver.FindPalette(pids)) is not null, 10000, 200)
            || palette is null)
        {
            Console.WriteLine("  パレットが開きませんでした。");
            return null;
        }

        Thread.Sleep(400);

        if (!SetPreset(palette, "整える"))
        {
            Console.WriteLine("  プリセット「整える」を選べませんでした。");
            return null;
        }

        var run = ById(palette, "RunButton");

        if (run is null)
        {
            Console.WriteLine("  「実行」が見つかりません。");
            return null;
        }

        Invoker(run)?.Invoke();

        var apply = ById(palette, "ApplyButton");

        if (!WaitUntil(() => apply is not null && Safe(() => apply.Current.IsEnabled), 40000))
        {
            Console.WriteLine("  整形が終わりませんでした。");
            return null;
        }

        // 「置き換える」は押さない。取得元は自前の窓だが、押す必要が無い。
        return palette;
    }

    /// <summary>
    /// トレイから設定画面を開き、「LLMへ送る範囲」を指定の状態にして保存する。
    ///
    /// メニューの項目は UIA の Invoke では押さない(モーダルを出す「設定」では
    /// ダイアログを閉じるまで返らず、UIA が時間切れになる。D11 で実際に落ちた)。
    /// </summary>
    private static int SetSetting(List<int> pids, bool on)
    {
        if (SettingIsOn() == on)
        {
            Console.WriteLine($"  周辺文脈はすでに{(on ? "オン" : "オフ")}です。設定画面は開きません。");
            return 0;
        }

        var menu = UiaDriver.OpenTrayMenu();

        if (menu is null)
        {
            Console.WriteLine("  トレイのメニューが出ませんでした。");
            return 1;
        }

        if (!UiaDriver.ClickMenuItem(menu, "設定"))
        {
            UiaDriver.CloseMenu();
            return 1;
        }

        AutomationElement? settings = null;

        if (!WaitUntil(
            () => (settings = TopLevelWindows(pids).FirstOrDefault(
                w => ById(w, "SaveButton") is not null)) is not null,
            10000,
            200) || settings is null)
        {
            Console.WriteLine("  設定画面が開きませんでした。");
            UiaDriver.CloseMenu();
            return 1;
        }

        Thread.Sleep(600);

        var box = ById(settings, "SendSurroundingContextCheckBox");
        var toggle = box is null ? null : Toggler(box);

        if (toggle is null)
        {
            Console.WriteLine("  「LLMへ送る範囲」のチェックが見つかりません。");
            Invoker(ById(settings, "CancelButton")!)?.Invoke();
            return 1;
        }

        if (Safe(() => toggle.Current.ToggleState == ToggleState.On) != on)
        {
            toggle.Toggle();
            Thread.Sleep(200);
        }

        Invoker(ById(settings, "SaveButton")!)?.Invoke();

        if (!WaitUntil(
            () => TopLevelWindows(pids).All(w => ById(w, "SaveButton") is null), 10000, 200))
        {
            Console.WriteLine("  設定画面が閉じませんでした。");
            return 1;
        }

        // 保存のたびにホットキーを張り直している(App.ReconfigureHotkeys)。
        Thread.Sleep(800);
        Console.WriteLine($"  周辺文脈を{(on ? "オン" : "オフ")}にして保存しました。");
        return 0;
    }

    /// <summary>設定ファイルの <c>Polish.SendSurroundingContext</c> を読む。</summary>
    private static bool SettingIsOn()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath()));

            return document.RootElement.TryGetProperty("Polish", out var polish)
                && polish.TryGetProperty("SendSurroundingContext", out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  設定ファイルを読めませんでした: {ex.GetType().Name}");
            return false;
        }
    }

    private static string SettingsPath() =>
        Path.Combine(Path.GetDirectoryName(_exePath)!, "appsettings.json");

    /// <summary>
    /// いま動いているアプリのプロセスID。H3 で起こし直すと変わるため、
    /// 読み直せるようにしておく(古いIDのままでは窓が見つからない)。
    /// </summary>
    private static List<int> CurrentPids(List<int> fallback)
    {
        var found = Process.GetProcessesByName("TextPolish").Select(p => p.Id).ToList();
        return found.Count > 0 ? found : fallback;
    }

    private static bool SetPreset(AutomationElement palette, string name)
    {
        var boxes = Safe(() => palette.FindAll(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox)));

        if (boxes is null)
        {
            return false;
        }

        var found = false;

        foreach (AutomationElement box in boxes)
        {
            var toggle = Toggler(box);

            if (toggle is null || Safe(() => box.Current.AutomationId) == "ShowDiffCheckBox")
            {
                continue;
            }

            var wanted = Squash(Safe(() => box.Current.Name)) == name;
            found |= wanted;

            if (Safe(() => toggle.Current.ToggleState == ToggleState.On) != wanted)
            {
                toggle.Toggle();
            }
        }

        return found;
    }

    private static void CloseAllPalettes(List<int> pids)
    {
        foreach (var window in TopLevelWindows(pids))
        {
            var close = ById(window, "CloseButton");

            if (close is not null)
            {
                Invoker(close)?.Invoke();
            }
        }

        WaitUntil(() => TopLevelWindows(pids).All(w => ById(w, "RunButton") is null), 5000, 200);
    }

    private static int? PortOf(string endpoint)
    {
        var at = endpoint.IndexOf("http", StringComparison.OrdinalIgnoreCase);

        if (at < 0)
        {
            return null;
        }

        return Uri.TryCreate(endpoint[at..].Trim(), UriKind.Absolute, out var uri) ? uri.Port : null;
    }

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "NG  ")}{what}");
        return ok ? 0 : 1;
    }
}
