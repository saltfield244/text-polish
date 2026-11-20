using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 動いているアプリを UI Automation で**操作する**試作。
///
/// <see cref="UiaInspector"/>(覗くだけ)との違いは、押す・入れる・待つをすること。
/// 実機確認の D・E・G は「整形中に別の操作をする」項目が多く、人手では
/// タイミングを揃えられない。<see cref="LlmStubServer"/> で応答時間を固定し、
/// ここで操作を送れば、同じ手順を何度でも繰り返せる。
///
/// **他の人が同じアプリで手作業の確認をしている最中には流さないこと。**
/// 本物のクリックが飛ぶため、相手の操作と混ざる。
///
/// 安全のために決めていること:
///   - 「置き換える」は押さない。押すと利用者が書いた文章が置き換わる
///   - 取得済みの原文・整形結果は画面に出さない(長さと一致判定だけ出す)
///   - run は Endpoint がループバックでなければ拒否する(本物のAPIを呼ぶと課金される)
///     (本物へ投げたい F7 などは --allow-real を明示したときだけ通る)
///
/// 使い方:
///   TextPolish.Tests --drive palette            開いているパレットを操作して状態を出す
///   TextPolish.Tests --drive palette --close    最後に「キャンセル」で閉じる
///   TextPolish.Tests --drive tray               トレイを右クリックしてメニューを出す
///   TextPolish.Tests --drive run                整形を実行して結果を読む(スタブ必須)
///   TextPolish.Tests --drive quick              即実行のホットキーを連打する(スタブ必須)
///   TextPolish.Tests --drive d                  実機確認の D1〜D10 を流す(スタブ必須)
///   TextPolish.Tests --drive e                  実機確認の E を流す(スタブ必須)
/// </summary>
internal static class UiaDriver
{
    private const string ProcessName = "TextPolish";
    private const string TrayBaseText = "テキスト整形ツール";

    /// <summary>自由指示に入れる印。読み返して一致を見るためだけの文字列。</summary>
    private const string InstructionMark = "UIA試作の印";

    /// <summary>この試作が出した文章の窓。終わったら閉じる。</summary>
    private static TextSourceWindow? _source;

    public static int Run(string[] args)
    {
        var processes = Process.GetProcessesByName(ProcessName);

        if (processes.Length == 0)
        {
            Console.WriteLine($"{ProcessName} が起動していません。");
            return 1;
        }

        var pids = processes.Select(p => p.Id).ToList();
        var scenario = StringArg(args, "--drive", "palette").ToLowerInvariant();

        Console.WriteLine($"{ProcessName} のプロセス: {string.Join(", ", pids)}");
        Console.WriteLine($"試すこと: {scenario}");
        Console.WriteLine();

        if (scenario.Length == 2 && scenario[0] == 'd' && char.IsDigit(scenario[1]))
        {
            return ChecklistD.Run(scenario, args, processes[0], pids);
        }

        if (scenario is "d" or "d10" or "d11" or "d12")
        {
            return ChecklistD.Run(scenario, args, processes[0], pids);
        }

        if (scenario.Length >= 1 && scenario[0] == 'e'
            && (scenario.Length == 1 || char.IsDigit(scenario[1])))
        {
            return ChecklistE.Run(scenario, args, processes[0], pids);
        }

        if (scenario.Length >= 1 && scenario[0] == 'g'
            && (scenario.Length == 1 || char.IsDigit(scenario[1])))
        {
            return ChecklistG.Run(scenario, args, processes[0], pids);
        }

        return scenario switch
        {
            "palette" => DrivePalette(pids, args),
            "tray" => DriveTray(),
            "run" => DriveRun(pids, args, processes[0]),
            "quick" => DriveQuick(args, processes[0]),
            _ => Unknown(scenario),
        };
    }

    private static int Unknown(string scenario)
    {
        Console.WriteLine($"知らない試作名です: {scenario}");
        Console.WriteLine("使えるのは palette / tray / run / quick");
        return 1;
    }

    // ------------------------------------------------------------------
    // パレットの操作
    // ------------------------------------------------------------------

    /// <summary>
    /// 開いている整形パレットを操作して、UIA から何ができるかを確かめる。
    /// 整形は走らせない(実行を押さないので、LLMは呼ばれず課金もされない)。
    /// </summary>
    private static int DrivePalette(List<int> pids, string[] args)
    {
        var palette = FindPalette(pids) ?? OpenPaletteIfAsked(pids, args);

        if (palette is null)
        {
            Console.WriteLine("整形パレットが見つかりません。F10 で開いてから実行してください。");
            Console.WriteLine("--open を付けると、メモ帳に文章を用意してホットキーまで送ります。");
            return 1;
        }

        var failures = 0;

        Console.WriteLine("== 最初の状態 ==");
        ReportState(palette);

        // 1. 原文。中身は出さない(利用者の文章なので、画面に残すと確認の意味が薄れる)。
        var original = ById(palette, "OriginalTextBox");
        var originalValue = original is null ? null : Valuer(original)?.Current.Value;
        Console.WriteLine($"  原文の文字数: {originalValue?.Length ?? -1}");
        failures += Check(originalValue is { Length: > 0 }, "原文を Value で読める");

        // 2. プリセットを入れる。AutomationId が無いので名前と種類で引く。
        var presetName = StringArg(args, "--preset", "整える");
        var preset = ByName(palette, presetName, ControlType.CheckBox);

        if (preset is null)
        {
            Console.WriteLine($"  プリセット「{presetName}」が見つかりません。");
            failures++;
        }
        else
        {
            var toggle = Toggler(preset);
            var was = toggle?.Current.ToggleState;
            toggle?.Toggle();
            var changed = WaitUntil(() => Toggler(preset)?.Current.ToggleState != was, 3000);
            var now = Toggler(preset)?.Current.ToggleState;

            Console.WriteLine($"  プリセット「{presetName}」: {was} → {now}");
            failures += Check(changed, "プリセットを Toggle で切り替えられる");
        }

        // 3. 自由指示に文字を入れて、読み返す。
        var instruction = ById(palette, "InstructionBox");
        var valuer = instruction is null ? null : Valuer(instruction);

        if (valuer is null)
        {
            Console.WriteLine("  自由指示に Value がありません。");
            failures++;
        }
        else
        {
            var before = valuer.Current.Value ?? string.Empty;
            valuer.SetValue(InstructionMark);
            var readBack = WaitUntil(
                () => Valuer(instruction!)?.Current.Value == InstructionMark, 3000);
            Console.WriteLine($"  自由指示に入れて読み返した: {readBack}");
            failures += Check(readBack, "自由指示を Value で書いて読める");

            // 入れたものは戻す。利用者の状態を変えたままにしない。
            valuer.SetValue(before);
        }

        // 4. 結果が無い間は「置き換える」が押せないこと。これが D・E・G の多くの要。
        var apply = ById(palette, "ApplyButton");
        var applyEnabled = apply is not null && Safe(() => apply.Current.IsEnabled);
        Console.WriteLine($"  「置き換える」: {(applyEnabled ? "押せる" : "押せない")}");
        failures += Check(!applyEnabled, "整形前は「置き換える」が押せない");

        // 5. プリセットは元に戻す。
        if (preset is not null)
        {
            Toggler(preset)?.Toggle();
        }

        ClosePaletteIfAsked(palette, args);
        return Report(failures);
    }

    /// <summary>
    /// <c>--close</c> が付いていればパレットを閉じる。次の確認を同じ状態から
    /// 始められるようにするため、各試作の最後に通す。
    /// </summary>
    private static void ClosePaletteIfAsked(AutomationElement palette, string[] args)
    {
        if (!args.Contains("--close", StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        var close = ById(palette, "CloseButton");

        if (close is null)
        {
            return;
        }

        Invoker(close)?.Invoke();
        Console.WriteLine();
        Console.WriteLine("「キャンセル」を押して閉じました。");
    }

    private static void ReportState(AutomationElement palette)
    {
        foreach (var id in new[] { "RunButton", "ApplyButton", "CloseButton", "ShowDiffCheckBox" })
        {
            var element = ById(palette, id);
            Console.WriteLine(element is null
                ? $"  {id}: 見つからない"
                : $"  {id}: {(Safe(() => element.Current.IsEnabled) ? "押せる" : "押せない")}");
        }
    }

    /// <summary>
    /// 整形パレットを探す。設定画面も開いていることがあるため、タイトルではなく
    /// 中身(RunButton があるか)で見分ける。表示文字列の変更に強い。
    /// </summary>
    internal static AutomationElement? FindPalette(List<int> pids) =>
        TopLevelWindows(pids).FirstOrDefault(w => ById(w, "RunButton") is not null);

    // ------------------------------------------------------------------
    // パレットを自分で開く
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>--open</c> が付いていれば、確認用の文章を自分で用意してパレットを開く。
    ///
    /// 自動化には「選択された文章がある状態」を作る工程が必要になる。人に選んで
    /// もらう前提では、D・E・G を続けて流せない。自前の窓に文章を出して全選択し、
    /// ホットキーを送る(利用者のアプリは借りない。<see cref="TextSourceWindow"/> 参照)。
    /// </summary>
    private static AutomationElement? OpenPaletteIfAsked(List<int> pids, string[] args)
    {
        if (!args.Contains("--open", StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var text = StringArg(args, "--text", "きょうのかいぎでけっていしたことを、かじょうがきでまとめてください。");
        _source = TextSourceWindow.Open(text);

        if (_source is null)
        {
            Console.WriteLine("文章の窓を出せませんでした。");
            return null;
        }

        Console.WriteLine($"文章の窓を出しました。選択されている文字数: {_source.SelectedLength}");

        // アプリは低レベルフック(WH_KEYBOARD_LL)で待っているので、
        // SendInput で送った鍵も本物の打鍵と同じに見える。
        if (!SyntheticInput.Tap(SyntheticInput.VkF10))
        {
            Console.WriteLine("ホットキーを送れませんでした(SendInput が 0 を返した)。");
            return null;
        }

        Console.WriteLine("ホットキー F10 を送りました。");

        AutomationElement? palette = null;
        var opened = WaitUntil(
            () =>
            {
                palette = FindPalette(pids);
                return palette is not null;
            },
            10000,
            250);

        Console.WriteLine(opened ? "パレットが開きました。" : "パレットが開きませんでした。");
        Console.WriteLine();
        return palette;
    }

    /// <summary>この試作が出した窓を閉じる。閉じるのは自分のものだけ。</summary>
    private static void CleanUp()
    {
        _source?.Dispose();
        _source = null;
    }

    // ------------------------------------------------------------------
    // トレイの操作
    // ------------------------------------------------------------------

    /// <summary>
    /// トレイアイコンを右クリックして、出たメニューを返す。
    ///
    /// 隠れたアイコンの中にいる場合は先に開く。メニューは押さずに返すので、
    /// 呼び出し側は必ず項目を押すか <see cref="CloseMenu"/> で閉じること。
    /// 開いたままにすると、次の操作が全部メニューに吸われる。
    /// </summary>
    internal static AutomationElement? OpenTrayMenu()
    {
        var item = FindTrayItem(TrayBaseText) ?? (OpenHiddenIcons() ? FindTrayItem(TrayBaseText) : null);

        if (item is null)
        {
            Console.WriteLine($"  「{TrayBaseText}」のアイコンが見つかりません。");
            return null;
        }

        var rect = Safe(() => item.Current.BoundingRectangle);

        // Safe は値型では既定値(幅も高さも0)を返すため、null ではなく大きさで見る。
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            Console.WriteLine("  アイコンの位置が取れません(画面に出ていない)。");
            return null;
        }

        var before = TopLevelHandles();

        if (!SyntheticInput.RightClick(
                (int)(rect.Left + (rect.Width / 2)), (int)(rect.Top + (rect.Height / 2))))
        {
            Console.WriteLine("  右クリックを送れませんでした(SendInput が 0 を返した)。");
            return null;
        }

        return WaitForNewTopLevel(before, 3000) ?? FindMenuAnywhere(3000);
    }

    /// <summary>メニューの項目を名前の先頭一致で押す。</summary>
    internal static bool ClickMenuItem(AutomationElement menu, string prefix)
    {
        var entry = Descendants(menu, 4).FirstOrDefault(e => TypeOf(e) == "MenuItem"
            && Squash(Safe(() => e.Current.Name)).StartsWith(prefix, StringComparison.Ordinal));

        if (entry is null)
        {
            Console.WriteLine($"  メニューに「{prefix}」が見つかりません。");
            return false;
        }

        // 出た直後は位置が確定していないことがある(1度、座標が取れていたのに
        // 押しても開かなかった)。少し待ってから測る。
        Thread.Sleep(250);

        // Invoke では押さない。相手の処理が返るまで待つため、モーダルのダイアログを出す
        // 「設定」では返らず UIA が時間切れになる(実際に COMException で落ちた)。
        // 「終了」も、待っている相手が消える。人と同じように座標で押す。
        var rect = Safe(() => entry.Current.BoundingRectangle);

        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            Console.WriteLine($"  「{prefix}」の位置が取れません。");
            return false;
        }

        var x = (int)(rect.Left + (rect.Width / 2));
        var y = (int)(rect.Top + (rect.Height / 2));
        Console.WriteLine($"  「{prefix}」を押す座標: ({x}, {y})");
        return SyntheticInput.LeftClick(x, y);
    }

    /// <summary>メニューを押さずに閉じる。</summary>
    internal static void CloseMenu() => SyntheticInput.Tap(SyntheticInput.VkEscape);

    /// <summary>
    /// トレイアイコンを右クリックして、出たメニューを読む(D11・D12 の足場)。
    /// 項目は押さない。終了を押すとアプリが落ちるため、出たことだけを確かめる。
    /// </summary>
    private static int DriveTray()
    {
        var item = FindTrayItem(TrayBaseText);

        if (item is null)
        {
            Console.WriteLine("隠れたアイコンを開きます。");

            if (!OpenHiddenIcons())
            {
                Console.WriteLine("「非表示のアイコンを表示する」を押せませんでした。");
                return 1;
            }

            item = FindTrayItem(TrayBaseText);
        }

        if (item is null)
        {
            Console.WriteLine($"「{TrayBaseText}」のアイコンが見つかりません。");
            return 1;
        }

        Describe(item, "  ");

        var rect = Safe(() => item.Current.BoundingRectangle);

        // Safe は値型では既定値(幅も高さも0)を返すため、null ではなく大きさで見る。
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
        {
            Console.WriteLine("  位置が取れません(画面に出ていない)。");
            return 1;
        }

        var x = (int)(rect.Left + (rect.Width / 2));
        var y = (int)(rect.Top + (rect.Height / 2));
        Console.WriteLine($"  右クリックする座標: ({x}, {y})");

        // 出たメニューは「増えたトップレベルウィンドウ」として探す。
        // クラス名で決め打ちすると、WinForms の ContextMenuStrip は Win32 の
        // #32768 ではないため見つけられない。
        var before = TopLevelHandles();

        if (!SyntheticInput.RightClick(x, y))
        {
            Console.WriteLine("  右クリックを送れませんでした(SendInput が 0 を返した)。");
            return 1;
        }

        // WinForms の ContextMenuStrip は、出るまでに少し間がある。
        var menu = WaitForNewTopLevel(before, 3000) ?? FindMenuAnywhere(3000);

        if (menu is null)
        {
            Console.WriteLine("  メニューが出ませんでした。いま出ているトップレベルを並べます。");
            foreach (var window in AllTopLevel())
            {
                Describe(window, "    ");
            }

            return 1;
        }

        Console.WriteLine();
        Console.WriteLine("== 出たメニュー ==");

        var items = Descendants(menu, 4)
            .Where(e => TypeOf(e) == "MenuItem")
            .ToList();

        foreach (var entry in items)
        {
            Describe(entry, "  ");
        }

        // 押さずに閉じる。開いたままにすると、次の操作が全部メニューに吸われる。
        SendEscape();

        Console.WriteLine();
        return Report(Check(items.Count > 0, "右クリックでメニュー項目を辿れる"));
    }

    /// <summary>
    /// メニューがどこかに出ていないか、机全体から探す。
    ///
    /// WinForms の ContextMenuStrip は、新しいトップレベルウィンドウとして現れず、
    /// 既にある隠れた親ウィンドウの子として出ることがある。増えたウィンドウで
    /// 見つからないときの受け皿。項目名(設定・終了)で当てる。
    /// </summary>
    internal static AutomationElement? FindMenuAnywhere(int timeoutMs)
    {
        AutomationElement? found = null;

        WaitUntil(
            () =>
            {
                found = AllTopLevel()
                    .SelectMany(w => Descendants(w, 3))
                    .FirstOrDefault(e => TypeOf(e) == "MenuItem"
                        && Squash(Safe(() => e.Current.Name)).StartsWith("設定", StringComparison.Ordinal));

                if (found is not null)
                {
                    // 項目そのものではなく、その入れ物(メニュー)を返す。
                    found = Safe(() => TreeWalker.ControlViewWalker.GetParent(found)) ?? found;
                }

                return found is not null;
            },
            timeoutMs,
            150);

        return found;
    }

    internal static List<AutomationElement> AllTopLevel() =>
        AutomationElement.RootElement
            .FindAll(TreeScope.Children, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .ToList();

    internal static HashSet<int> TopLevelHandles() =>
        AllTopLevel().Select(e => Safe(() => e.Current.NativeWindowHandle)).ToHashSet();

    /// <summary>直前に無かったトップレベルウィンドウが出るまで待つ。</summary>
    internal static AutomationElement? WaitForNewTopLevel(HashSet<int> before, int timeoutMs)
    {
        AutomationElement? found = null;

        WaitUntil(
            () =>
            {
                found = AllTopLevel()
                    .FirstOrDefault(e => !before.Contains(Safe(() => e.Current.NativeWindowHandle)));
                return found is not null;
            },
            timeoutMs,
            100);

        return found;
    }

    /// <summary>
    /// メニューを閉じる。Esc を送るのに <see cref="SyntheticInput"/> で Esc を送る。
    /// SendKeys は使わない(メッセージループが無いと待ちに入る)。
    /// </summary>
    private static void SendEscape()
    {
        SyntheticInput.Tap(SyntheticInput.VkEscape);
    }

    // ------------------------------------------------------------------
    // 整形を走らせる
    // ------------------------------------------------------------------

    /// <summary>
    /// 実際に整形を走らせて、結果を読む。スタブへ向いているときだけ動かす。
    /// 「置き換える」は押さない(利用者の文章を置き換えてしまう)。
    /// </summary>
    private static int DriveRun(List<int> pids, string[] args, Process app)
    {
        if (!AllowedToRun(app, args))
        {
            return 1;
        }

        var palette = FindPalette(pids) ?? OpenPaletteIfAsked(pids, args);

        if (palette is null)
        {
            Console.WriteLine("整形パレットが見つかりません。F10 で開いてから実行してください。");
            return 1;
        }

        var preset = ByName(palette, StringArg(args, "--preset", "整える"), ControlType.CheckBox);
        var toggle = preset is null ? null : Toggler(preset);

        if (toggle is null)
        {
            Console.WriteLine("プリセットが見つかりません。");
            return 1;
        }

        if (toggle.Current.ToggleState != ToggleState.On)
        {
            toggle.Toggle();
        }

        var run = ById(palette, "RunButton");
        var runner = run is null ? null : Invoker(run);

        if (runner is null)
        {
            Console.WriteLine("「実行」が見つかりません。");
            return 1;
        }

        var failures = 0;
        var presses = Math.Max(1, IntArg(args, "--presses", 1));

        for (var i = 0; i < presses; i++)
        {
            runner.Invoke();
            Thread.Sleep(150);
        }

        Console.WriteLine(presses == 1 ? "「実行」を押しました。" : $"「実行」を {presses} 回押しました。");

        // 整形中、「実行」は無効にならず**「中止」に変わる**(PaletteWindow.SetBusy)。
        // 押せるかどうかではなく、名前で見る。
        var becameBusy = WaitUntil(
            () => Squash(Safe(() => run!.Current.Name)) == "中止", 3000, 50);
        Console.WriteLine($"  整形中に「中止」へ変わった: {becameBusy}");
        failures += Check(becameBusy, "整形中かどうかを「中止」の表示で判別できる");

        // 整形中はプリセットを変えられない(PresetPanel.IsEnabled = false)。
        var presetLocked = !Safe(() => preset!.Current.IsEnabled);
        Console.WriteLine($"  整形中にプリセットが固まった: {presetLocked}");
        failures += Check(presetLocked, "整形中はプリセットを変えられない");

        var timeout = IntArg(args, "--timeout", 30) * 1000;
        var apply = ById(palette, "ApplyButton");

        if (args.Contains("--cancel", StringComparer.OrdinalIgnoreCase))
        {
            // いまの「実行」は「中止」なので、同じボタンを押すと取り消しになる。
            runner.Invoke();
            Console.WriteLine("  「中止」を押しました。");

            var backToIdle = WaitUntil(
                () => Squash(Safe(() => run!.Current.Name)) == "実行", timeout, 100);
            Console.WriteLine($"  「実行」へ戻った: {backToIdle}");
            failures += Check(backToIdle, "中止すると待機状態へ戻る");

            var applyStillOff = apply is not null && !Safe(() => apply.Current.IsEnabled);
            Console.WriteLine($"  中止後も「置き換える」は押せない: {applyStillOff}");
            failures += Check(applyStillOff, "中止した結果は書き戻せない");

            ClosePaletteIfAsked(palette, args);
            return Report(failures);
        }

        if (args.Contains("--expect-fail", StringComparer.OrdinalIgnoreCase))
        {
            // 採用しない応答(途中終了・HTTPエラー・壊れたJSON)のとき。
            // 整形は終わるが、結果は書き戻せないままであるべき。
            var settled = WaitUntil(
                () => Squash(Safe(() => run!.Current.Name)) == "実行", timeout, 100);
            Console.WriteLine($"  整形が終わって「実行」へ戻った: {settled}");
            failures += Check(settled, "採用しない応答でも待機状態へ戻る");

            var stillOff = apply is not null && !Safe(() => apply.Current.IsEnabled);
            Console.WriteLine($"  「置き換える」は押せないまま: {stillOff}");
            failures += Check(stillOff, "採用しない応答は書き戻せない");

            ClosePaletteIfAsked(palette, args);
            return Report(failures);
        }

        var finished = WaitUntil(
            () => apply is not null && Safe(() => apply.Current.IsEnabled), timeout);

        Console.WriteLine($"  「置き換える」が押せるようになった: {finished}");
        failures += Check(finished, "整形の完了を IsEnabled で待てる");

        var result = ById(palette, "ResultTextBox");
        var text = result is null ? null : Valuer(result)?.Current.Value;
        Console.WriteLine($"  結果の文字数: {text?.Length ?? -1}");

        var expected = StringArg(args, "--expect", string.Empty);

        if (expected.Length > 0)
        {
            var match = text == expected;
            Console.WriteLine($"  スタブが返した文字列と一致: {match}");
            failures += Check(match, "結果が送られてきたものと一致する");
        }

        Console.WriteLine();
        Console.WriteLine("「置き換える」は押していません(押すと元の文章が置き換わるため)。");

        ClosePaletteIfAsked(palette, args);
        return Report(failures);
    }

    // ------------------------------------------------------------------
    // 即実行(確認画面を出さない経路)
    // ------------------------------------------------------------------

    /// <summary>
    /// 即実行のホットキーを連打して、1回しか整形されないことを見る(D1・D2)。
    ///
    /// ボタンの連打では排他を見られない。整形中の「実行」は無効にならず
    /// 「中止」に変わるため、2回目は取り消しになってしまう。排他(OperationGate)が
    /// 効くのはホットキー側である。
    ///
    /// 即実行は結果を前面のウィンドウへ貼り付けて原文を置き換える。置き換わるのは
    /// この試作が出した窓の中だけなので、利用者の文章は触らない。
    /// 何回呼ばれたかはスタブの画面に出る(1件目・2件目…)。
    /// </summary>
    private static int DriveQuick(string[] args, Process app)
    {
        if (!AllowedToRun(app, args))
        {
            return 1;
        }

        var text = StringArg(args, "--text", "きょうのかいぎでけっていしたことを、かじょうがきでまとめてください。");
        _source = TextSourceWindow.Open(text);

        if (_source is null)
        {
            Console.WriteLine("文章の窓を出せませんでした。");
            return 1;
        }

        var presses = Math.Max(1, IntArg(args, "--presses", 3));
        Console.WriteLine($"文章の窓を出しました。選択されている文字数: {_source.SelectedLength}");

        for (var i = 0; i < presses; i++)
        {
            // 送る直前の状態を残す。失敗したとき「アプリが悪いのか、こちらの
            // 用意が崩れていたのか」を後から切り分けるため。
            Console.WriteLine(
                $"  送る直前: 前面={_source.IsForeground} / 選択={_source.SelectedLength}文字");

            if (!SyntheticInput.TapWithControlShift(
                SyntheticInput.VkW, IntArg(args, "--hold", 250)))
            {
                Console.WriteLine("ホットキーを送れませんでした。");
                return Report(1);
            }

            Thread.Sleep(120);
        }

        Console.WriteLine($"Ctrl+Shift+W を {presses} 回送りました。");

        var timeout = IntArg(args, "--timeout", 40) * 1000;
        var expected = StringArg(args, "--expect", string.Empty);

        var replaced = WaitUntil(() => _source.Content != text, timeout, 250);
        Console.WriteLine($"  窓の中身が置き換わった: {replaced}");

        var failures = Check(replaced, "即実行で前面のウィンドウへ書き戻せる");

        if (expected.Length > 0)
        {
            var now = Squash(_source.Content);
            var match = now == expected;
            Console.WriteLine($"  スタブが返した文字列と一致: {match}");

            if (!match)
            {
                // 一致しないときだけ中身を出す。この文章は試作が用意したものなので、
                // 利用者の文章を画面に残すことにはならない。
                Console.WriteLine($"  診断: 窓の中身は「{Trim(now, 50)}」({now.Length} 文字)");
            }

            failures += Check(match, "書き戻された文章が整形結果と一致する");
        }

        Console.WriteLine();
        Console.WriteLine("何回整形されたかは、スタブの画面の「n件目」を見てください(1件だけが正解)。");
        return Report(failures);
    }

    /// <summary>
    /// 本物のプロバイダを呼んでしまわないための関門。既定ではループバック以外を通さない。
    ///
    /// ただし F7(誤ったAPIキーで 401 を受ける)や、本物の鍵で一度だけ整形してみる確認は、
    /// 本物のエンドポイントへ投げないと成り立たない。そこで --allow-real を明示したときだけ
    /// 通し、何へ向いているかを画面に出す。付け忘れで課金されることがないよう、既定は拒否のまま。
    /// 通し運転(D・E・G)はスタブ前提なのでこの抜け道を持たせない。
    /// </summary>
    private static bool AllowedToRun(Process app, string[] args)
    {
        if (EndpointIsLoopback(app, out var endpoint))
        {
            return true;
        }

        if (args.Contains("--allow-real", StringComparer.OrdinalIgnoreCase))
        {
            Console.WriteLine("!! ループバックではないエンドポイントへ投げます(--allow-real)。");
            Console.WriteLine($"   いまの Endpoint: {endpoint}");
            Console.WriteLine("   本物のプロバイダなら課金されます。");
            return true;
        }

        Console.WriteLine("Endpoint がループバックではありません。整形を走らせません。");
        Console.WriteLine($"  いまの Endpoint: {endpoint}");
        Console.WriteLine("  --stub を立て、appsettings.json の Endpoint をそのURLにしてから実行してください。");
        Console.WriteLine("  本物のプロバイダのままだと、課金され、応答時間も揃いません。");
        Console.WriteLine("  意図して本物へ投げるときだけ --allow-real を付けてください。");
        return false;
    }

    /// <summary>
    /// 動いているアプリの設定を読んで、Endpoint がループバックかを見る。
    ///
    /// 設定ファイルは exe と同じフォルダにある(%AppData% ではない)ので、
    /// 動いているプロセスの場所から辿る。どの設定で動いているかを取り違えないため。
    /// </summary>
    internal static bool EndpointIsLoopback(Process app, out string endpoint)
    {
        endpoint = "(読めませんでした)";

        try
        {
            var exe = app.MainModule?.FileName;

            if (exe is null)
            {
                return false;
            }

            var path = Path.Combine(Path.GetDirectoryName(exe)!, "appsettings.json");

            if (!File.Exists(path))
            {
                endpoint = $"(設定が見つかりません: {path})";
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            // どのプロバイダで動いているかは LlmProvider に書いてある。
            // 選ばれていないプロバイダの Endpoint は使われないため、見ない
            // (テンプレートの TODO が残っているだけで止まってしまう)。
            var provider = root.TryGetProperty("LlmProvider", out var name)
                && name.ValueKind == JsonValueKind.String
                    ? name.GetString()
                    : null;

            if (provider is null)
            {
                endpoint = "(LlmProvider の項目がありません)";
                return false;
            }

            if (!root.TryGetProperty(provider, out var section)
                || section.ValueKind != JsonValueKind.Object
                || !section.TryGetProperty("Endpoint", out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                endpoint = $"({provider} の Endpoint が読めません)";
                return false;
            }

            endpoint = $"{provider}: {value.GetString()}";
            return IsLoopback(value.GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
            or InvalidOperationException)
        {
            endpoint = $"(読めませんでした: {ex.GetType().Name})";
            return false;
        }
    }

    private static bool IsLoopback(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.IsLoopback || uri.Host is "127.0.0.1" or "localhost");

    // ------------------------------------------------------------------

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "NG  ")}{what}");
        return ok ? 0 : 1;
    }

    private static int Report(int failures)
    {
        CleanUp();
        Console.WriteLine(failures == 0 ? "確かめたことは全部できた。" : $"できなかったこと: {failures} 件");
        return failures == 0 ? 0 : 1;
    }
}
