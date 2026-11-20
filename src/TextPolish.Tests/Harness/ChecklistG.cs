using System.Diagnostics;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 実機確認手順「G. 整形パレットの再整形」を流す。
///
/// この章で守られるべきことは1つ。**いま画面に出ている文字列が、今回の整形の結果で
/// ないなら書き戻せない。** 「整える」で成功したあとに「英訳」で失敗したとき、
/// 利用者は英訳を貼ったつもりで整えた結果を貼ってしまう(<c>PolishResultState</c>)。
///
/// D・E と違い、スタブを**この試作の中で立てる**。G は「1回目は成功、2回目は失敗」
/// のように応答を回ごとに変える必要があり、別プロセスのスタブ(<c>--stub</c>)では
/// 標準入力で手打ちしない限り切り替えられない。同じ組み立てのサーバーを
/// 同じ番号のポートで自分が持てば、各項目の途中で応答を差し替えられる。
///
/// そのため <c>--stub</c> は**立てないで**実行する。ポート番号はアプリの
/// <c>appsettings.json</c> に書かれた Endpoint から読む(向け先が食い違っていると、
/// 整形が永遠に終わらないか本物のプロバイダを呼んでしまう)。
/// </summary>
internal static class ChecklistG
{
    private const string Sample = "きょうのかいぎでけっていしたことを、かじょうがきでまとめてください。";

    /// <summary>1回目の整形でスタブが返す文字列。</summary>
    private const string First = "G-RESULT-ONE-5A91";

    /// <summary>2回目の整形でスタブが返す文字列。1回目と必ず違えておく。</summary>
    private const string Second = "G-RESULT-TWO-5A91";

    private const string StaleNote = "表示中の結果は前回のものです";

    private static TextSourceWindow? _source;

    public static int Run(string name, string[] args, Process app, List<int> pids)
    {
        if (!UiaDriver.EndpointIsLoopback(app, out var endpoint))
        {
            Console.WriteLine("Endpoint がループバックではありません。G は流しません。");
            Console.WriteLine($"  いまの Endpoint: {endpoint}");
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
            stub = new LlmStubServer(new StubScenario { Text = First }, port.Value);
        }
        catch (Exception ex)
        {
            // いちばん多いのは --stub を立てたまま流した場合。原因を書かないと分からない。
            Console.WriteLine($"ポート {port} でスタブを立てられませんでした: {ex.Message}");
            Console.WriteLine("  G はスタブを自分で立てます。--stub は止めてから流してください。");
            return 1;
        }

        Console.WriteLine($"スタブを自分で立てました(ポート {stub.Port})。応答は項目ごとに差し替えます。");

        var items = new Dictionary<string, Func<List<int>, LlmStubServer, int>>
        {
            ["g1"] = G1,
            ["g2"] = G2,
            ["g3"] = G3,
            ["g4"] = G4,
            ["g5"] = G5,
            ["g6"] = G6,
        };

        using (stub)
        {
            if (name == "g")
            {
                var total = 0;

                foreach (var (key, body) in items)
                {
                    total += RunOne(key, body, pids, stub);
                }

                Console.WriteLine();
                Console.WriteLine("G3(やり直して成功させる)は G2 の続きとして、同じ項目の中で流しています。");
                Console.WriteLine(total == 0 ? "G: 流した項目は全部期待どおり。" : $"G: 期待どおりでなかった確認: {total} 件");
                return total == 0 ? 0 : 1;
            }

            if (!items.TryGetValue(name, out var single))
            {
                Console.WriteLine($"{name} はここでは扱っていません(g1〜g6、または g)。");
                return 1;
            }

            return RunOne(name, single, pids, stub) == 0 ? 0 : 1;
        }
    }

    private static int RunOne(
        string key, Func<List<int>, LlmStubServer, int> body, List<int> pids, LlmStubServer stub)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {key.ToUpperInvariant()} =====");

        CloseAllPalettes(pids);
        stub.Scenario = new StubScenario { Text = First };

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

    /// <summary>G1: 「整える」で整形して「置き換える」。従来どおり置き換わる。</summary>
    private static int G1(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        var palette = Format(pids, source, "整える", stub);

        if (palette is null)
        {
            return 1;
        }

        var failures = Check(Squash(ValueOf(palette, "ResultTextBox")) == First, "結果欄にスタブの文字列が出る");
        Invoker(ById(palette, "ApplyButton")!)?.Invoke();
        failures += Check(
            WaitUntil(() => Squash(source.Content) == First, 15000, 250), "選択範囲が整形結果に置き換わる");
        return failures;
    }

    /// <summary>
    /// G2: 「整える」で成功させたあと「英訳」で実行し、途中で「中止」を押す。
    /// 表示は前回の結果のままで、「置き換える」は押せない。
    /// </summary>
    private static int G2(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        var palette = Format(pids, source, "整える", stub);

        if (palette is null)
        {
            return 1;
        }

        // 2回目は中止する間が必要なので待たせる。返す文字列も変えておき、
        // 中止した結果が紛れ込んでいないかを後で見分けられるようにする。
        stub.Scenario = stub.Scenario with { DelayMs = 8000, Text = Second };

        if (!StartRun(palette, "英訳"))
        {
            return 1;
        }

        Invoker(ById(palette, "RunButton")!)?.Invoke();

        var back = WaitUntil(
            () => Squash(Safe(() => ById(palette, "RunButton")?.Current.Name)) == "実行", 10000, 100);
        var failures = Check(back, "中止すると待機へ戻る");

        var status = TextOf(palette, "StatusText");
        failures += Check(status.Contains("整形を中止しました", StringComparison.Ordinal), "中止したことが表示される");
        failures += Check(status.Contains(StaleNote, StringComparison.Ordinal), "前回の結果であることが表示される");
        failures += Check(!IsEnabled(palette, "ApplyButton"), "「置き換える」が押せない");
        failures += Check(Squash(ValueOf(palette, "ResultTextBox")) == First, "結果欄は前回の結果のまま");
        Console.WriteLine($"  パレットの表示: {Trim(status, 70)}");
        return failures;
    }

    /// <summary>
    /// G3: G2 のあともう一度「英訳」で成功させる。書き戻るのは**2回目**の結果。
    /// G2 と同じ流れを作る必要があるため、G2 の確認もそのまま続けて行う。
    /// </summary>
    private static int G3(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        var palette = Format(pids, source, "整える", stub);

        if (palette is null)
        {
            return 1;
        }

        stub.Scenario = stub.Scenario with { DelayMs = 8000, Text = Second };

        if (!StartRun(palette, "英訳"))
        {
            return 1;
        }

        Invoker(ById(palette, "RunButton")!)?.Invoke();
        WaitUntil(() => Squash(Safe(() => ById(palette, "RunButton")?.Current.Name)) == "実行", 10000, 100);

        // やり直し。今度は待たせない。
        stub.Scenario = stub.Scenario with { DelayMs = 0 };
        Invoker(ById(palette, "RunButton")!)?.Invoke();

        var apply = ById(palette, "ApplyButton");
        var done = WaitUntil(() => apply is not null && Safe(() => apply.Current.IsEnabled), 40000);
        var failures = Check(done, "やり直すと「置き換える」が押せるようになる");

        if (!done)
        {
            return failures;
        }

        failures += Check(Squash(ValueOf(palette, "ResultTextBox")) == Second, "結果欄が2回目の結果になる");
        Invoker(apply!)?.Invoke();

        var pasted = WaitUntil(() => Squash(source.Content) == Second, 15000, 250);
        failures += Check(pasted, "書き戻るのは2回目の結果");
        failures += Check(Squash(source.Content) != First, "1回目の結果は書き戻らない");
        return failures;
    }

    /// <summary>G4: 「整える」で成功したあと、401 を返す状態で再整形する。</summary>
    private static int G4(List<int> pids, LlmStubServer stub) =>
        Reformat(pids, stub, StubOutcome.HttpError, "整形に失敗しました", "401 を返す");

    /// <summary>G5: 「整える」で成功したあと、途中終了する応答で再整形する。</summary>
    private static int G5(List<int> pids, LlmStubServer stub) =>
        Reformat(pids, stub, StubOutcome.Truncated, "打ち切られ", "途中終了を返す");

    /// <summary>
    /// G4・G5 は「成功 → 採用できない応答で再整形 → 前回の結果を書き戻せない」で同じ形。
    /// 変わるのは応答の種類と、表示に出るべき言葉だけ。
    /// </summary>
    private static int Reformat(
        List<int> pids, LlmStubServer stub, StubOutcome outcome, string expectedWord, string what)
    {
        var source = OpenSource();
        var palette = Format(pids, source, "整える", stub);

        if (palette is null)
        {
            return 1;
        }

        stub.Scenario = stub.Scenario with { Outcome = outcome, Text = Second };
        Console.WriteLine($"  スタブを「{what}」に切り替えて再整形します。");

        if (!StartRun(palette, "英訳"))
        {
            return 1;
        }

        // 「実行」に戻っただけでは足りない。待ち時間0の応答では、押した直後にまだ
        // 「実行」のままの瞬間があるため、表示が「整形中」でないことも見る。
        var back = WaitUntil(
            () => Squash(Safe(() => ById(palette, "RunButton")?.Current.Name)) == "実行"
                && !TextOf(palette, "StatusText").Contains("整形中", StringComparison.Ordinal),
            40000,
            100);
        var failures = Check(back, "整形が終わる(落ちない)");

        var status = TextOf(palette, "StatusText");
        failures += Check(status.Contains(expectedWord, StringComparison.Ordinal), $"表示に「{expectedWord}」が出る");
        failures += Check(status.Contains(StaleNote, StringComparison.Ordinal), "前回の結果であることが表示される");
        failures += Check(!IsEnabled(palette, "ApplyButton"), "「置き換える」が押せない");
        failures += Check(Squash(ValueOf(palette, "ResultTextBox")) == First, "結果欄は前回の結果のまま");
        failures += Check(Squash(_source!.Content) == Sample, "取得元の文章は変わっていない");
        Console.WriteLine($"  パレットの表示: {Trim(status, 70)}");
        return failures;
    }

    /// <summary>G6: 結果欄を手で直して書き戻す。空にすると書き戻せない。</summary>
    private static int G6(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        var palette = Format(pids, source, "整える", stub);

        if (palette is null)
        {
            return 1;
        }

        var box = ById(palette, "ResultTextBox");
        var value = box is null ? null : Valuer(box);

        if (value is null)
        {
            Console.WriteLine("  結果欄を編集できません(ValuePattern がありません)。");
            return 1;
        }

        var edited = First + "-TENAOSHI";
        value.SetValue(edited);
        var failures = Check(
            WaitUntil(() => IsEnabled(palette, "ApplyButton"), 3000, 100), "手直ししても「置き換える」は押せる");

        // 全部消すと書き戻せなくなる。消したままでは確認を続けられないので、戻してから進める。
        value.SetValue(string.Empty);
        failures += Check(
            WaitUntil(() => !IsEnabled(palette, "ApplyButton"), 3000, 100), "結果欄を空にすると押せなくなる");

        value.SetValue(edited);

        if (!WaitUntil(() => IsEnabled(palette, "ApplyButton"), 3000, 100))
        {
            Console.WriteLine("  書き戻せる状態へ戻せませんでした。");
            return failures + 1;
        }

        Invoker(ById(palette, "ApplyButton")!)?.Invoke();
        failures += Check(
            WaitUntil(() => Squash(source.Content) == edited, 15000, 250), "手直しした内容が書き戻る");
        return failures;
    }

    // ------------------------------------------------------------------

    private static TextSourceWindow OpenSource()
    {
        _source = TextSourceWindow.Open(Sample, "試作G(取得元)")
            ?? throw new InvalidOperationException("取得元の窓を出せませんでした。");
        return _source;
    }

    /// <summary>取得元からパレットを開き、プリセットで整形して「置き換える」が押せる状態にする。</summary>
    private static AutomationElement? Format(
        List<int> pids, TextSourceWindow source, string preset, LlmStubServer stub)
    {
        if (!Ready(source))
        {
            return null;
        }

        stub.Scenario = stub.Scenario with { Outcome = StubOutcome.Success, DelayMs = 0, Text = First };

        SyntheticInput.Tap(SyntheticInput.VkF10);

        AutomationElement? palette = null;
        var opened = WaitUntil(
            () =>
            {
                palette = UiaDriver.FindPalette(pids);
                return palette is not null;
            },
            10000,
            200);

        if (!opened || palette is null)
        {
            Console.WriteLine("  パレットが開きませんでした。");
            return null;
        }

        Thread.Sleep(400);

        if (!StartRun(palette, preset))
        {
            return null;
        }

        var apply = ById(palette, "ApplyButton");

        if (!WaitUntil(() => apply is not null && Safe(() => apply.Current.IsEnabled), 40000))
        {
            Console.WriteLine("  整形が終わりませんでした。");
            return null;
        }

        return palette;
    }

    /// <summary>プリセットを選び直して「実行」を押し、整形中になったことを確かめる。</summary>
    private static bool StartRun(AutomationElement palette, string preset)
    {
        if (!SetPreset(palette, preset))
        {
            Console.WriteLine($"  プリセット「{preset}」を選べませんでした。");
            return false;
        }

        var run = ById(palette, "RunButton");

        if (run is null)
        {
            Console.WriteLine("  「実行」が見つかりません。");
            return false;
        }

        Invoker(run)?.Invoke();

        // 整形中は「実行」が「中止」に変わる(PaletteWindow.SetBusy)。無効にはならない。
        // 待ち時間0のスタブでは終わっている場合もあるため、戻っていても良しとする。
        WaitUntil(() => Squash(Safe(() => run.Current.Name)) is "中止" or "実行", 5000, 50);
        return true;
    }

    /// <summary>
    /// 指定のプリセットだけを選んだ状態にする。
    /// プリセットは複数選べるため、前の選択を外さないと「整える＋英訳」になってしまう。
    /// </summary>
    private static bool SetPreset(AutomationElement palette, string name)
    {
        // プリセットの入れ物(PresetPanel)から辿るのはやめた。WrapPanel は UIA の
        // コントロールビューに出ないことがあり、実際に見つからなかった。
        // パレット全体のチェックボックスを見て、「差分表示」だけ除く。
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

    private static bool Ready(TextSourceWindow window)
    {
        for (var i = 0; i < 2; i++)
        {
            if (window.IsForeground && window.SelectedLength > 0)
            {
                Thread.Sleep(300);
                return true;
            }

            window.Activate();
        }

        Console.WriteLine(
            $"  用意が崩れています(前面={window.IsForeground} / 選択={window.SelectedLength}文字)。中止します。");
        return false;
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

    private static bool IsEnabled(AutomationElement palette, string automationId)
    {
        var element = ById(palette, automationId);
        return element is not null && Safe(() => element.Current.IsEnabled);
    }

    private static string? ValueOf(AutomationElement palette, string automationId)
    {
        var element = ById(palette, automationId);
        return element is null ? null : Valuer(element)?.Current.Value;
    }

    private static string TextOf(AutomationElement palette, string automationId)
    {
        var element = ById(palette, automationId);
        return element is null ? string.Empty : Squash(Safe(() => element.Current.Name));
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
