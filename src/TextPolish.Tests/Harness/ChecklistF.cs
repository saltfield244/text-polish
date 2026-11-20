using System.Diagnostics;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 実機確認手順「F. LLM応答の途中終了」を流す。
///
/// この章で守られるべきことは2つ。**途中で切れた応答で原文を置き換えない**ことと、
/// **ログに利用者の文章を残さない**ことである。どちらも壊れても画面には出ないため、
/// 手順書では毎回ログを開いて目で確かめていた(F6 は原文の断片を1件ずつ grep していた)。
/// その判断は <see cref="LogFileAssert"/> に固めてあるので、ここから呼ぶ。
///
/// G と同じく、スタブは**この試作の中で立てる**。F は項目ごとに応答の種類を
/// 変える必要があり(正常・途中終了・終了理由なし)、別プロセスのスタブでは
/// 手打ちしない限り切り替えられない。そのため <c>--stub</c> は立てないで実行する。
///
/// 流せないもの:
/// - **F4(Gemini で F1・F2 を繰り返す)。** Gemini には出力トークン上限の設定が無く、
///   <c>GeminiFormatter</c> は <c>maxOutputTokens</c> を送らない。つまり
///   <c>finishReason=MAX_TOKENS</c> を狙って起こす手段が利用者側に無い。判定自体は
///   <c>LlmResponseTests</c> とスタブの回帰テストで固定してある
/// - **F7(誤ったAPIキー)。** 本物のプロバイダから認証失敗を受ける必要がある。
///   2026-11-20 に本物の鍵で実測した(README の F の節に結果がある)
/// - **F8(日付をまたぐ)。** アプリを日付が変わるまで起動したままにする必要がある。
///   行ごとにその行の時刻で書き込み先を決める実装なので、判定は
///   <c>FileLoggerTests</c> で固定してある
/// </summary>
internal static class ChecklistF
{
    /// <summary>
    /// 取得元に出す文章。ログに残っていないことを確かめるため、
    /// ありふれた言葉ではなく、探して一致したら漏えいと言い切れる形にする。
    /// </summary>
    private const string Sample =
        "きょうのかいぎでけっていしたことを、かじょうがきでまとめてください。" +
        "ぎだいはF6-LEAKCHECK-7C42で、たんとうはサンプル担当者です。";

    /// <summary>ログに1つも出てはいけない断片。原文を分けて持つ。</summary>
    private static readonly string[] Fragments =
    {
        "きょうのかいぎでけっていしたこと",
        "かじょうがきでまとめてください",
        "F6-LEAKCHECK-7C42",
        "サンプル担当者",
    };

    private const string Result = "F-RESULT-9D13";

    private static TextSourceWindow? _source;

    public static int Run(string name, string[] args, Process app, List<int> pids)
    {
        if (!UiaDriver.EndpointIsLoopback(app, out var endpoint))
        {
            Console.WriteLine("Endpoint がループバックではありません。F は流しません。");
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
            stub = new LlmStubServer(new StubScenario { Text = Result }, port.Value);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ポート {port} でスタブを立てられませんでした: {ex.Message}");
            Console.WriteLine("  F はスタブを自分で立てます。--stub は止めてから流してください。");
            return 1;
        }

        Console.WriteLine($"スタブを自分で立てました(ポート {stub.Port})。応答は項目ごとに差し替えます。");

        var items = new Dictionary<string, Func<List<int>, LlmStubServer, int>>
        {
            ["f1"] = F1,
            ["f2"] = F2,
            ["f3"] = F3,
            ["f5"] = F5,
            ["f6"] = F6,
            ["f9"] = F9,
        };

        using (stub)
        {
            if (name == "f")
            {
                var total = 0;

                foreach (var (key, body) in items)
                {
                    total += RunOne(key, body, pids, stub);
                }

                Console.WriteLine();
                Console.WriteLine("F4・F7・F8 はここでは流せません(理由は ChecklistF の説明と README を参照)。");
                Console.WriteLine(total == 0 ? "F: 流した項目は全部期待どおり。" : $"F: 期待どおりでなかった確認: {total} 件");
                return total == 0 ? 0 : 1;
            }

            if (!items.TryGetValue(name, out var single))
            {
                Console.WriteLine($"{name} はここでは扱っていません(f1・f2・f3・f5・f6・f9、または f)。");
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

    /// <summary>
    /// F1: 途中終了する応答でパレットから整形する。
    /// 結果は表示されず、「置き換える」も押せない。
    /// </summary>
    private static int F1(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        stub.Scenario = stub.Scenario with { Outcome = StubOutcome.Truncated };

        var palette = RunPalette(pids, source);

        if (palette is null)
        {
            return 1;
        }

        var status = TextOf(palette, "StatusText");
        var failures = Check(
            status.Contains("打ち切られました", StringComparison.Ordinal), "「打ち切られました」と表示される");
        failures += Check(
            status.Contains("length", StringComparison.Ordinal), "終了理由(length)が表示される");
        failures += Check(!IsEnabled(palette, "ApplyButton"), "「置き換える」が押せない");
        failures += Check(Squash(ValueOf(palette, "ResultTextBox")).Length == 0, "結果欄に整形結果が出ていない");
        failures += Check(Squash(source.Content) == Sample, "取得元の文章は変わっていない");
        Console.WriteLine($"  パレットの表示: {Trim(status, 80)}");
        return failures;
    }

    /// <summary>
    /// F2: 同じ応答で即実行する。**原文は変わらない。**
    /// 置き換えてしまうと利用者は気づけないので、この章でいちばん重い項目。
    /// </summary>
    private static int F2(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        stub.Scenario = stub.Scenario with { Outcome = StubOutcome.Truncated };

        var log = LogFileAssert.Today();
        var mark = log.LineCount;

        if (!Ready(source))
        {
            return 1;
        }

        SyntheticInput.TapWithControlShift(SyntheticInput.VkW);

        var logged = log.WaitForLine("打ち切られ", 40000, mark);
        var failures = Check(logged is not null, "採用しなかったことがログに残る");

        // 書き戻しは起きないはずなので、待っても変わらないことを見る。
        Thread.Sleep(1500);
        failures += Check(Squash(source.Content) == Sample, "原文は変わっていない");
        failures += Check(
            log.LinesAfter(mark).All(l => !l.Contains("テキストを挿入しました", StringComparison.Ordinal)),
            "書き戻しを行っていない");

        if (logged is not null)
        {
            Console.WriteLine($"  ログ: {Trim(logged, 110)}");
        }

        return failures;
    }

    /// <summary>
    /// F3: 正常な応答(終了理由つき)で整形する。
    /// 整形でき、「finish_reason がありませんでした」は出ない。
    ///
    /// 手順書は <c>MaxCompletionTokens</c> を既定へ戻してから整形する形だが、
    /// 設定値そのものではなく「終了理由が返るときに正常として扱えること」が要点なので、
    /// スタブの正常応答で同じ状態を作っている。
    /// </summary>
    private static int F3(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        var log = LogFileAssert.Today();
        var mark = log.LineCount;

        var palette = RunPalette(pids, source);

        if (palette is null)
        {
            return 1;
        }

        var failures = Check(IsEnabled(palette, "ApplyButton"), "従来どおり整形できる");
        failures += Check(Squash(ValueOf(palette, "ResultTextBox")) == Result, "結果欄に整形結果が出る");
        failures += Check(
            log.LinesAfter(mark).All(
                l => !l.Contains("finish_reason がありませんでした", StringComparison.Ordinal)),
            "「finish_reason がありませんでした」は出ない");
        return failures;
    }

    /// <summary>
    /// F5: 終了理由を返さないサーバーで整形する。
    /// 整形でき、正常終了として扱ったことがログに残る。
    ///
    /// 手順書は「そういう環境を探す」形だったが、スタブの <c>nofinish</c> で作れる。
    /// </summary>
    private static int F5(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        stub.Scenario = stub.Scenario with { Outcome = StubOutcome.NoFinishReason };

        var log = LogFileAssert.Today();
        var mark = log.LineCount;

        var palette = RunPalette(pids, source);

        if (palette is null)
        {
            return 1;
        }

        var failures = Check(IsEnabled(palette, "ApplyButton"), "終了理由が無くても整形できる");
        failures += Check(Squash(ValueOf(palette, "ResultTextBox")) == Result, "結果欄に整形結果が出る");

        var line = log.WaitForLine("finish_reason がありませんでした", 10000, mark);
        failures += Check(line is not null, "正常終了として扱ったことがログに残る");

        if (line is not null)
        {
            Console.WriteLine($"  ログ: {Trim(line, 110)}");
        }

        return failures;
    }

    /// <summary>
    /// F6: F1・F2 と同じ流れを作ってから、その間にログへ書かれた行を機械的に調べる。
    ///
    /// 見るのは4点。原文の断片が無いこと、応答本文(<c>Body=</c>)が無いこと、
    /// APIキーらしい値が無いこと、全部の行が時刻で始まること(1件が改行で割れていないこと)。
    /// </summary>
    private static int F6(List<int> pids, LlmStubServer stub)
    {
        var log = LogFileAssert.Today();
        var mark = log.LineCount;

        var failures = F1(pids, stub);
        _source?.Dispose();
        _source = null;
        CloseAllPalettes(pids);
        failures += F2(pids, stub);

        var written = log.LinesAfter(mark);
        Console.WriteLine($"  この確認の間にログへ書かれた行: {written.Count}");

        failures += Checked(() => log.AssertNoFragmentOf(Fragments, mark), "整形を依頼した文章が1文字も残っていない");
        failures += Check(
            written.All(l => !l.Contains("Body=", StringComparison.Ordinal)), "応答本文(Body=)が残っていない");
        failures += Checked(() => log.AssertNoApiKeyShape(mark), "APIキーらしい値が残っていない");
        failures += Checked(() => log.AssertEveryLineIsOneRecord(mark), "すべての行が時刻で始まる(1件1行)");

        // 残っていてよい診断情報は、形が決まっているものだけ。出ていることも見る。
        var diagnostics = written.FirstOrDefault(l => l.Contains("判定=", StringComparison.Ordinal))
            ?? written.FirstOrDefault(l => l.Contains("BodyChars=", StringComparison.Ordinal));
        failures += Check(diagnostics is not null, "診断情報(判定= / BodyChars=)は残っている");

        if (diagnostics is not null)
        {
            Console.WriteLine($"  ログ: {Trim(diagnostics, 110)}");
        }

        return failures;
    }

    /// <summary>
    /// F9: パレットで整形して「中止」を押す。
    /// 中止は INFO で1行残るだけで、ERROR は出ない(本物の障害が埋もれるため)。
    /// </summary>
    private static int F9(List<int> pids, LlmStubServer stub)
    {
        var source = OpenSource();
        var log = LogFileAssert.Today();
        var mark = log.LineCount;

        if (!Ready(source))
        {
            return 1;
        }

        // 中止を押す間を作る。
        stub.Scenario = stub.Scenario with { DelayMs = 8000 };

        SyntheticInput.Tap(SyntheticInput.VkF10);

        AutomationElement? palette = null;

        if (!WaitUntil(() => (palette = UiaDriver.FindPalette(pids)) is not null, 10000, 200)
            || palette is null)
        {
            Console.WriteLine("  パレットが開きませんでした。");
            return 1;
        }

        Thread.Sleep(400);

        if (!StartRun(palette, "整える"))
        {
            return 1;
        }

        var run = ById(palette, "RunButton");

        if (run is null || !WaitUntil(
            () => Squash(Safe(() => run.Current.Name)) == "中止", 10000, 100))
        {
            Console.WriteLine("  「中止」に変わりませんでした(応答が速すぎた可能性があります)。");
            return 1;
        }

        Invoker(run)?.Invoke();

        var back = WaitUntil(() => Squash(Safe(() => run.Current.Name)) == "実行", 15000, 100);
        var failures = Check(back, "中止すると待機へ戻る");

        var line = log.WaitForLine("整形を中止しました", 10000, mark);
        failures += Check(line is not null, "中止が INFO で残る");
        failures += Check(
            line is not null && line.Contains("[INFO] ", StringComparison.Ordinal), "中止の行が INFO である");
        failures += Checked(() => log.AssertNoError(mark), "ERROR が出ていない");

        if (line is not null)
        {
            Console.WriteLine($"  ログ: {Trim(line, 110)}");
        }

        return failures;
    }

    // ------------------------------------------------------------------

    private static TextSourceWindow OpenSource()
    {
        _source = TextSourceWindow.Open(Sample, "試作F(取得元)")
            ?? throw new InvalidOperationException("取得元の窓を出せませんでした。");
        return _source;
    }

    /// <summary>パレットを開いて「整える」で実行し、整形が終わるまで待つ。</summary>
    private static AutomationElement? RunPalette(List<int> pids, TextSourceWindow source)
    {
        if (!Ready(source))
        {
            return null;
        }

        SyntheticInput.Tap(SyntheticInput.VkF10);

        AutomationElement? palette = null;

        if (!WaitUntil(() => (palette = UiaDriver.FindPalette(pids)) is not null, 10000, 200)
            || palette is null)
        {
            Console.WriteLine("  パレットが開きませんでした。");
            return null;
        }

        Thread.Sleep(400);

        if (!StartRun(palette, "整える"))
        {
            return null;
        }

        // 採用しない応答では「置き換える」が有効にならないため、
        // 「整形中でなくなったこと」で終わりを判断する。
        var done = WaitUntil(
            () => Squash(Safe(() => ById(palette, "RunButton")?.Current.Name)) == "実行"
                && !TextOf(palette, "StatusText").Contains("整形中", StringComparison.Ordinal),
            40000,
            100);

        if (!done)
        {
            Console.WriteLine("  整形が終わりませんでした。");
            return null;
        }

        return palette;
    }

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
        WaitUntil(() => Squash(Safe(() => run.Current.Name)) is "中止" or "実行", 5000, 50);
        return true;
    }

    /// <summary>
    /// 指定のプリセットだけを選んだ状態にする(G と同じ理由で、
    /// 入れ物からではなくパレット全体のチェックボックスを見る)。
    /// </summary>
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

    /// <summary>
    /// <see cref="LogFileAssert"/> の照合は、違反の中身を例外のメッセージで知らせる。
    /// 1件の確認として数えつつ、その中身も画面に出す。
    /// </summary>
    private static int Checked(Action assert, string what)
    {
        try
        {
            assert();
            return Check(true, what);
        }
        catch (Exception ex)
        {
            var failures = Check(false, what);
            Console.WriteLine($"      {Trim(ex.Message, 160)}");
            return failures;
        }
    }
}
