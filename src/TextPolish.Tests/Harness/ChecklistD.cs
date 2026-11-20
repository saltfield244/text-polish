using System.Diagnostics;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 実機確認手順「D. ホットキーの連打・同時実行」を自分で流す。
///
/// README の D1〜D12 と1対1に対応させている。番号で呼べる(<c>--drive d3</c>)ほか、
/// <c>--drive d</c> で自動化できるものを順に流す。
///
/// 期待結果の確かめ方は2通り。画面の状態は UIA で見る(パレットが何枚あるか、
/// ボタンが押せるか)。アプリが「無視した」ことはログで見る。どちらか片方だけでは
/// 「無視されたのか、そもそも押せていないのか」が区別できない。
///
/// D11・D12 はここには無い。設定画面の作りを実測していないこと、D12 はアプリを
/// 終了させてしまうことから、別に扱う。
/// </summary>
internal static class ChecklistD
{
    private const string Sample = "きょうのかいぎでけっていしたことを、かじょうがきでまとめてください。";

    /// <summary>クリップボードに置く目印。確認の前後で壊れていないかを見る。</summary>
    private const string ClipboardMark = "CLIPBOARD-MARK-7B3D";

    private const string IgnoredPalette = "整形パレットの起動を無視しました";
    private const string IgnoredQuick = "即実行を無視しました";
    private const string NoSelection = "選択テキストを取得できませんでした";

    public static int Run(string name, string[] args, Process app, List<int> pids)
    {
        if (!UiaDriver.EndpointIsLoopback(app, out var endpoint))
        {
            Console.WriteLine("Endpoint がループバックではありません。D は流しません。");
            Console.WriteLine($"  いまの Endpoint: {endpoint}");
            Console.WriteLine("  --stub を立て、appsettings.json の Endpoint をそのURLにしてください。");
            return 1;
        }

        _quickIsF8 = StringArg(args, "--quick-key", string.Empty)
            .Equals("f8", StringComparison.OrdinalIgnoreCase);

        if (_quickIsF8)
        {
            Console.WriteLine("即実行は F8 で送ります(appsettings.json の QuickHotkey も F8 であること)。");
        }

        _gapMs = IntArg(args, "--gap", 900);
        _pressGapMs = IntArg(args, "--press-gap", 120);
        _args = args;
        var expected = StringArg(args, "--expect", string.Empty);

        if (expected.Length == 0)
        {
            Console.WriteLine("--expect にスタブが返す文字列を渡してください(結果の照合に使います)。");
            return 1;
        }

        var items = new Dictionary<string, Func<List<int>, string, int>>
        {
            ["d1"] = D1,
            ["d2"] = D2,
            ["d3"] = D3,
            ["d4"] = D4,
            ["d5"] = D5,
            ["d6"] = D6,
            ["d7"] = D7,
            ["d8"] = D8,
            ["d9"] = D9,
            ["d10"] = D10,
        };

        if (name == "d")
        {
            var total = 0;

            foreach (var (key, body) in items)
            {
                // D8 は401を返すスタブが必要なので、通し運転からは外す。
                // 成功を返すスタブのまま流すと「失敗が記録されない」と出てしまう
                // (実際にそれを不具合と読みかけた)。
                if (key == "d8")
                {
                    Console.WriteLine();
                    Console.WriteLine("===== D8 =====");
                    Console.WriteLine("  401を返すスタブが必要なので、ここでは流しません。");
                    Console.WriteLine("  --stub --outcome error を立ててから --drive d8 で流してください。");
                    continue;
                }

                total += RunOne(key, body, pids, expected);
            }

            Console.WriteLine();
            Console.WriteLine(total == 0 ? "D: 流した項目は全部期待どおり。" : $"D: 期待どおりでなかった確認: {total} 件");

            if (_undecided > 0)
            {
                Console.WriteLine($"D: 前面を奪われて判定できなかった回: {_undecided} 件(環境の問題。やり直しが必要)");
            }

            return total == 0 && _undecided == 0 ? 0 : 1;
        }

        if (!items.TryGetValue(name, out var single))
        {
            Console.WriteLine($"{name} はここでは扱っていません(d1〜d10、または d)。");
            return 1;
        }

        return RunOne(name, single, pids, expected) == 0 ? 0 : 1;
    }

    private static int RunOne(
        string key, Func<List<int>, string, int> body, List<int> pids, string expected)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {key.ToUpperInvariant()} =====");

        CloseAllPalettes(pids);
        SetClipboard(ClipboardMark);

        try
        {
            var failures = body(pids, expected);
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

    private static TextSourceWindow? _source;

    /// <summary>
    /// 即実行のホットキーを送る方法。既定は設定どおりの Ctrl+Shift+W。
    ///
    /// <c>--quick-key f8</c> を渡すと F8(修飾キーなし)で送る。修飾キーを含むと、
    /// アプリが送出する Ctrl+C との取り合いで取得が失敗することがあるため、
    /// 排他の確認そのものに集中したいときはこちらを使う
    /// (appsettings.json の QuickHotkey も F8 にしておくこと)。
    /// </summary>
    private static bool _quickIsF8;

    /// <summary>
    /// 連打するときの押す間隔(既定 120ms)。
    ///
    /// 人が「素早く3回」押すのはおよそ 100〜150ms 間隔。これより速いと、
    /// アプリが排他を掴む前に次が届き、無視の件数が合わなくなることがある。
    /// 人より速い連打を試したいときに縮める。
    /// </summary>
    private static int _pressGapMs = 120;

    private static string[] _args = Array.Empty<string>();

    /// <summary>D3 で、即実行のあとパレットのホットキーを押すまでの間(既定 900ms)。</summary>
    private static int _gapMs = 900;

    /// <summary>
    /// ほかのアプリに前面を奪われて判定できなかった回数。
    ///
    /// このPCには前面を奪うアプリ(RustDesk)が動いており、送った直後に前面が
    /// 入れ替わることがある。そうなるとアプリは「別のウィンドウの選択」を
    /// 取ろうとして失敗する。アプリの不合格とは別に数える。混ぜると、環境のせいで
    /// 失敗したものを不具合として報告してしまう(実際に報告しかけた)。
    /// </summary>
    private static int _undecided;

    private static bool PressQuick() => _quickIsF8
        ? SyntheticInput.Tap(SyntheticInput.VkF8)
        : SyntheticInput.TapWithControlShift(SyntheticInput.VkW);

    /// <summary>送った直後に前面を奪われていないかを見る。奪われていたら判定できない。</summary>
    private static bool StillFront(TextSourceWindow window)
    {
        Thread.Sleep(150);

        if (window.IsForeground)
        {
            return true;
        }

        Console.WriteLine("  送った直後にほかのアプリが前面を奪いました。この項目は判定できません。");
        _undecided++;
        return false;
    }

    // ------------------------------------------------------------------
    // D1〜D10
    // ------------------------------------------------------------------

    /// <summary>D1: パレットのホットキーを素早く3回。パレットは1枚だけ開く。</summary>
    private static int D1(List<int> pids, string expected)
    {
        var window = OpenSource(Sample);

        if (!Ready(window))
        {
            return 1;
        }

        var mark = LogMark();
        var presses = Math.Max(2, IntArg(_args, "--presses", 3));

        for (var i = 0; i < presses; i++)
        {
            SyntheticInput.Tap(SyntheticInput.VkF10);
            Thread.Sleep(_pressGapMs);
        }

        WaitUntil(() => UiaDriver.FindPalette(pids) is not null, 10000, 200);
        Thread.Sleep(1500);

        var failures = Check(CountPalettes(pids) == 1, $"パレットは1枚だけ(実際 {CountPalettes(pids)} 枚)");
        var ignored = CountAfter(mark, IgnoredPalette);
        Console.WriteLine($"  押した回数 {presses} / 「無視しました」 {ignored} 件");
        failures += Check(ignored >= 1, "2回目以降が無視される");

        var palette = UiaDriver.FindPalette(pids);
        var original = palette is null ? null : ValueOf(palette, "OriginalTextBox");
        failures += Check(original == Sample, "選択した文章がそのまま入っている");
        failures += Check(ClipboardIs(ClipboardMark), "クリップボードが壊れていない");
        return failures;
    }

    /// <summary>D2: 即実行のホットキーを素早く3回。整形と書き戻しは1回だけ。</summary>
    private static int D2(List<int> pids, string expected)
    {
        var window = OpenSource(Sample);

        if (!Ready(window))
        {
            return 1;
        }

        var mark = LogMark();

        for (var i = 0; i < 3; i++)
        {
            PressQuick();
            Thread.Sleep(_pressGapMs);
        }

        var replaced = WaitUntil(() => window.Content != Sample, 40000, 250);
        Thread.Sleep(1200);

        var failures = Check(replaced, "書き戻された");
        failures += Check(Squash(window.Content) == expected, "結果が二重になっていない(整形結果と一致)");
        failures += Check(CountAfter(mark, IgnoredQuick) == 2,
            $"「無視しました」が2件(実際 {CountAfter(mark, IgnoredQuick)} 件)");

        // 書き戻しのあと、遅延復元でクリップボードが元に戻る(既定 800ms)。
        failures += Check(WaitUntil(() => ClipboardIs(ClipboardMark), 5000, 250),
            "クリップボードが元の内容に戻る");
        return failures;
    }

    /// <summary>D3: 即実行の整形中にパレットのホットキー。パレットは開かない。</summary>
    private static int D3(List<int> pids, string expected)
    {
        var window = OpenSource(Sample);

        if (!Ready(window))
        {
            return 1;
        }

        PressQuick();

        if (!StillFront(window))
        {
            return 0;
        }

        Thread.Sleep(_gapMs);

        var mark = LogMark();
        SyntheticInput.Tap(SyntheticInput.VkF10);
        Thread.Sleep(1200);

        var failures = Check(CountPalettes(pids) == 0, "整形中にパレットは開かない");
        failures += Check(CountAfter(mark, IgnoredPalette) >= 1, "「整形パレットの起動を無視しました」が出る");

        var replaced = WaitUntil(() => window.Content != Sample, 40000, 250);
        failures += Check(replaced && Squash(window.Content) == expected, "即実行の書き戻しは完了する");
        return failures;
    }

    /// <summary>D4: パレットを開いた状態で即実行のホットキー。何も起こらない。</summary>
    private static int D4(List<int> pids, string expected)
    {
        var source = OpenSource(Sample);

        if (!Ready(source))
        {
            return 1;
        }

        var palette = OpenPalette(pids);

        if (palette is null)
        {
            Console.WriteLine("  パレットが開きませんでした。");
            return 1;
        }

        var mark = LogMark();
        PressQuick();
        Thread.Sleep(1500);

        var failures = Check(CountAfter(mark, IgnoredQuick) >= 1, "「即実行を無視しました」が出る");

        var run = ById(palette, "RunButton");
        failures += Check(run is not null && Safe(() => run.Current.IsEnabled), "パレットはそのまま使える");
        return failures;
    }

    /// <summary>D5: パレットを開いた状態でパレットのホットキー。増えない。</summary>
    private static int D5(List<int> pids, string expected)
    {
        var source5 = OpenSource(Sample);

        if (!Ready(source5))
        {
            return 1;
        }

        if (OpenPalette(pids) is null)
        {
            Console.WriteLine("  パレットが開きませんでした。");
            return 1;
        }

        SyntheticInput.Tap(SyntheticInput.VkF10);
        Thread.Sleep(1500);

        return Check(CountPalettes(pids) == 1, $"パレットは1枚のまま(実際 {CountPalettes(pids)} 枚)");
    }

    /// <summary>D6: パレットの取得中(0.1秒以内)に即実行。取得は1回だけ。</summary>
    private static int D6(List<int> pids, string expected)
    {
        var window = OpenSource(Sample);

        if (!Ready(window))
        {
            return 1;
        }

        var mark = LogMark();

        SyntheticInput.Tap(SyntheticInput.VkF10);
        Thread.Sleep(40);
        _ = _quickIsF8 ? SyntheticInput.Tap(SyntheticInput.VkF8) : SyntheticInput.TapWithControlShift(SyntheticInput.VkW, 60);

        WaitUntil(() => UiaDriver.FindPalette(pids) is not null, 10000, 200);
        Thread.Sleep(1200);

        var palette = UiaDriver.FindPalette(pids);
        var failures = Check(palette is not null, "パレットは開く");
        failures += Check(CountAfter(mark, IgnoredQuick) >= 1, "即実行は無視される");

        var original = palette is null ? null : ValueOf(palette, "OriginalTextBox");
        failures += Check(original == Sample, "選択した文章がそのまま入っている(Ctrl+C は1回だけ)");
        failures += Check(window.Content == Sample, "元の窓は書き換わっていない");
        return failures;
    }

    /// <summary>D7: 何も選択せずに両方のホットキーを3回ずつ。毎回空振りする。</summary>
    private static int D7(List<int> pids, string expected)
    {
        var empty = OpenSource(string.Empty);

        // ここは選択が無い状態を作るのが目的なので、選択の有無は確かめない。
        if (!Ready(empty, requireSelection: false))
        {
            return 1;
        }

        var mark = LogMark();

        for (var i = 0; i < 3; i++)
        {
            SyntheticInput.Tap(SyntheticInput.VkF10);
            Thread.Sleep(1500);
        }

        var palette = CountAfter(mark, NoSelection);
        var failures = Check(palette == 3, $"パレット側で3回とも空振りする(実際 {palette} 件)");

        var mark2 = LogMark();

        for (var i = 0; i < 3; i++)
        {
            PressQuick();
            Thread.Sleep(1500);
        }

        var quick = CountAfter(mark2, NoSelection);
        failures += Check(quick == 3, $"即実行側で3回とも空振りする(実際 {quick} 件)");
        failures += Check(CountPalettes(pids) == 0, "パレットは開かない");
        return failures;
    }

    /// <summary>
    /// D8: 失敗したあと、もう一度即実行できる(例外で排他が残らない)。
    /// スタブを <c>--outcome error</c> で立てておくこと。
    /// </summary>
    private static int D8(List<int> pids, string expected)
    {
        var source8 = OpenSource(Sample);

        if (!Ready(source8))
        {
            return 1;
        }

        var mark = LogMark();
        PressQuick();
        var first = WaitUntil(() => CountAfter(mark, "即実行に失敗しました") >= 1, 30000, 250);

        var mark2 = LogMark();
        Thread.Sleep(500);
        PressQuick();
        var second = WaitUntil(() => CountAfter(mark2, "即実行に失敗しました") >= 1, 30000, 250);

        var failures = Check(first, "1回目で失敗が記録される");
        failures += Check(second, "2回目も動く(排他が残っていない)");
        failures += Check(CountAfter(mark2, IgnoredQuick) == 0, "2回目が「無視」になっていない");
        return failures;
    }

    /// <summary>D9: パレットで整形して、適用せずに閉じたあと即実行できる。</summary>
    private static int D9(List<int> pids, string expected)
    {
        var window = OpenSource(Sample);

        if (!Ready(window))
        {
            return 1;
        }

        var palette = OpenPalette(pids);

        if (palette is null)
        {
            Console.WriteLine("  パレットが開きませんでした。");
            return 1;
        }

        TogglePreset(palette, "整える");
        Invoker(ById(palette, "RunButton")!)?.Invoke();

        var apply = ById(palette, "ApplyButton");
        var done = WaitUntil(() => apply is not null && Safe(() => apply.Current.IsEnabled), 40000);
        var failures = Check(done, "パレットの整形は終わる");

        Invoker(ById(palette, "CloseButton")!)?.Invoke();
        WaitUntil(() => CountPalettes(pids) == 0, 5000, 200);

        // 閉じた直後の窓は選択が外れているので、選び直してから送る。
        if (!Ready(window))
        {
            return failures + 1;
        }

        var mark = LogMark();
        PressQuick();

        var replaced = WaitUntil(() => window.Content != Sample, 40000, 250);
        failures += Check(replaced, "閉じたあと即実行が動く");
        failures += Check(CountAfter(mark, IgnoredQuick) == 0, "「無視」にならない(閉じた時点で解放されている)");
        return failures;
    }

    /// <summary>
    /// D10: パレットと即実行を交互に10回。毎回動き、無視のログが出ない。
    /// スタブの待ち時間は 0 にしておくと速い。
    /// </summary>
    private static int D10(List<int> pids, string expected)
    {
        var rounds = 10;
        var mark = LogMark();
        var failures = 0;

        for (var i = 1; i <= rounds; i++)
        {
            var window = OpenSource(Sample);

            if (!Ready(window))
            {
                failures++;
                _source?.Dispose();
                _source = null;
                continue;
            }

            // パレット: 開いて閉じるだけ(整形はしない。回数を稼ぐのが目的)。
            var palette = OpenPalette(pids);

            if (palette is null)
            {
                Console.WriteLine($"  {i} 回目: パレットが開かなかった");
                failures++;
                _source?.Dispose();
                _source = null;
                continue;
            }

            Invoker(ById(palette, "CloseButton")!)?.Invoke();
            WaitUntil(() => CountPalettes(pids) == 0, 5000, 200);

            // 即実行。
            if (!Ready(window))
            {
                Console.WriteLine($"  {i} 回目: 窓を前面にできなかった");
                failures++;
                _source?.Dispose();
                _source = null;
                continue;
            }

            PressQuick();

            if (!WaitUntil(() => window.Content != Sample, 30000, 200))
            {
                Console.WriteLine($"  {i} 回目: 書き戻されなかった");
                failures++;
            }

            _source?.Dispose();
            _source = null;
        }

        Console.WriteLine($"  {rounds} 回まわした。");
        failures += Check(CountAfter(mark, IgnoredPalette) == 0, "パレット側の「無視」が出ない");
        failures += Check(CountAfter(mark, IgnoredQuick) == 0, "即実行側の「無視」が出ない");
        failures += Check(WaitUntil(() => ClipboardIs(ClipboardMark), 5000, 250),
            "クリップボードが元の内容に戻る");
        return failures;
    }

    // ------------------------------------------------------------------
    // 道具
    // ------------------------------------------------------------------

    /// <summary>
    /// ホットキーを送る前に、用意が崩れていないかを確かめる。
    ///
    /// これが無いと、前面を取れていないだけの失敗を「アプリが取得できなかった」と
    /// 読み違える(実際に読み違えた)。崩れていたら1度だけ立て直し、それでも
    /// 駄目なら確認を中止する。アプリの合否とこちらの不手際を混ぜないため。
    /// </summary>
    private static bool Ready(TextSourceWindow window, bool requireSelection = true)
    {
        for (var i = 0; i < 2; i++)
        {
            var front = window.IsForeground;
            var selected = window.SelectedLength;

            if (front && (!requireSelection || selected > 0))
            {
                // 前面になった直後は、相手の窓がまだ落ち着いていないことがある。
                // 送るのを少し待つ(待たずに送ると取得が時々失敗する)。
                Thread.Sleep(300);
                return true;
            }

            window.Activate();
        }

        Console.WriteLine(
            $"  用意が崩れています(前面={window.IsForeground} / 選択={window.SelectedLength}文字)。中止します。");
        return false;
    }

    private static TextSourceWindow OpenSource(string text)
    {
        _source?.Dispose();
        _source = TextSourceWindow.Open(text)
            ?? throw new InvalidOperationException("文章の窓を出せませんでした。");
        return _source;
    }

    /// <summary>ホットキーを送ってパレットが開くのを待つ。</summary>
    private static AutomationElement? OpenPalette(List<int> pids)
    {
        SyntheticInput.Tap(SyntheticInput.VkF10);

        AutomationElement? palette = null;
        WaitUntil(
            () =>
            {
                palette = UiaDriver.FindPalette(pids);
                return palette is not null;
            },
            10000,
            200);

        Thread.Sleep(400);
        return palette;
    }

    private static int CountPalettes(List<int> pids) =>
        TopLevelWindows(pids).Count(w => ById(w, "RunButton") is not null);

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

        WaitUntil(() => CountPalettes(pids) == 0, 5000, 200);
    }

    private static void TogglePreset(AutomationElement palette, string name)
    {
        var preset = ByName(palette, name, ControlType.CheckBox);
        var toggle = preset is null ? null : Toggler(preset);

        if (toggle is not null && toggle.Current.ToggleState != ToggleState.On)
        {
            toggle.Toggle();
        }
    }

    private static string? ValueOf(AutomationElement palette, string automationId)
    {
        var element = ById(palette, automationId);
        return element is null ? null : Valuer(element)?.Current.Value;
    }

    /// <summary>いまのログの行数。これより後の行だけを見れば、この操作の分が分かる。</summary>
    private static int LogMark() => LogFileAssert.Today().LineCount;

    private static int CountAfter(int mark, string text) =>
        LogFileAssert.Today().LinesAfter(mark).Count(line => line.Contains(text, StringComparison.Ordinal));

    private static void SetClipboard(string text)
    {
        try
        {
            System.Windows.Forms.Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // クリップボードはOSの共有資源で、他プロセスと競合する。
            // 目印を置けなくても確認自体は続けられる。
        }
    }

    private static bool ClipboardIs(string text)
    {
        try
        {
            return System.Windows.Forms.Clipboard.ContainsText()
                && System.Windows.Forms.Clipboard.GetText() == text;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "NG  ")}{what}");
        return ok ? 0 : 1;
    }
}
