using System.Diagnostics;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 実機確認手順「E. 書き戻し先の検証(意図しないウィンドウへ貼り付けない)」を流す。
///
/// この章で守られるべきことは1つだけ。**取得元ではないウィンドウへ貼り付けない。**
/// そこを判定の中心に置き、「中止になるのか、取得元へ戻って貼れるのか」は
/// 観測して出す。
///
/// 書き戻しの実装は、貼る前に取得元のウィンドウへフォーカスを戻す
/// (<c>PaletteWindow.ApplyButton_Click</c> と <c>App.OnQuickHotkey</c> の
/// <c>WindowFocus.RestoreAsync</c>)。つまり途中で別のウィンドウを前面にしても、
/// 取得元が生きていれば**戻して貼る**のが設計どおりの動きで、中止にはならない。
/// README の E3・E6 は「中止される」と書いてあるが、それは取得元へ戻せなかった
/// 場合の話である。ここでは実際にどちらになったかを出す。
///
/// 窓は2つともこの試作が出したもので、利用者のアプリは使わない。
/// </summary>
internal static class ChecklistE
{
    private const string Sample = "きょうのかいぎでけっていしたことを、かじょうがきでまとめてください。";
    private const string Other = "こちらは別のウィンドウです。ここには貼られてはいけません。";
    private const string ClipboardMark = "CLIPBOARD-MARK-7B3D";

    private const string FocusFailed = "元のウィンドウへフォーカスを戻せなかったため";
    private const string TargetChanged = "貼り付け先が取得元のウィンドウではないため";
    private const string Preserved = "整形結果をクリップボードへ退避しました";

    private static TextSourceWindow? _a;
    private static TextSourceWindow? _b;

    public static int Run(string name, string[] args, Process app, List<int> pids)
    {
        if (!UiaDriver.EndpointIsLoopback(app, out var endpoint))
        {
            Console.WriteLine("Endpoint がループバックではありません。E は流しません。");
            Console.WriteLine($"  いまの Endpoint: {endpoint}");
            return 1;
        }

        var expected = StringArg(args, "--expect", string.Empty);

        if (expected.Length == 0)
        {
            Console.WriteLine("--expect にスタブが返す文字列を渡してください。");
            return 1;
        }

        var items = new Dictionary<string, Func<List<int>, string, int>>
        {
            ["e1"] = E1,
            ["e2"] = E2,
            ["e3"] = E3,
            ["e5"] = E5,
            ["e6"] = E6,
            ["e7"] = E7,
        };

        if (name == "e")
        {
            var total = 0;

            foreach (var (key, body) in items)
            {
                total += RunOne(key, body, pids, expected);
            }

            Console.WriteLine();
            Console.WriteLine("E4(やり直せること)は E3 の中で続けて確かめています。");
            Console.WriteLine("E8(中止後のクリップボード)は E2・E3 の中で確かめています。");
            Console.WriteLine(total == 0 ? "E: 流した項目は全部期待どおり。" : $"E: 期待どおりでなかった確認: {total} 件");
            return total == 0 ? 0 : 1;
        }

        if (!items.TryGetValue(name, out var single))
        {
            Console.WriteLine($"{name} はここでは扱っていません(e1・e2・e3・e5・e6・e7、または e)。");
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
            _a?.Dispose();
            _b?.Dispose();
            _a = null;
            _b = null;
            CloseAllPalettes(pids);
        }
    }

    // ------------------------------------------------------------------

    /// <summary>E1: パレットで整形して「置き換える」。選択範囲が置き換わる。</summary>
    private static int E1(List<int> pids, string expected)
    {
        var a = OpenA();
        var palette = Format(pids, a);

        if (palette is null)
        {
            return 1;
        }

        var mark = LogMark();
        Invoker(ById(palette, "ApplyButton")!)?.Invoke();

        var replaced = WaitUntil(() => Squash(a.Content) == expected, 15000, 250);
        var failures = Check(replaced, "選択範囲が整形結果に置き換わる");
        failures += Check(CountAfter(mark, TargetChanged) == 0, "中止のログが出ない");
        return failures;
    }

    /// <summary>
    /// E2: 整形したあと取得元を閉じてから「置き換える」。どこにも貼らない。
    /// あわせて E8(中止後のクリップボードは元の内容)も見る。
    /// </summary>
    private static int E2(List<int> pids, string expected)
    {
        var a = OpenA();
        var b = OpenB();
        var palette = Format(pids, a);

        if (palette is null)
        {
            return 1;
        }

        // 取得元を閉じる。
        a.Dispose();
        _a = null;
        Thread.Sleep(600);

        var mark = LogMark();
        Invoker(ById(palette, "ApplyButton")!)?.Invoke();
        Thread.Sleep(3000);

        var failures = Check(Squash(b.Content) == Squash(Other), "別のウィンドウには貼られていない");

        var aborted = CountAfter(mark, FocusFailed) + CountAfter(mark, TargetChanged);
        failures += Check(aborted >= 1, "書き戻しを中止した記録が残る");
        Console.WriteLine($"  中止の理由: フォーカス復帰失敗 {CountAfter(mark, FocusFailed)} 件 / 照合不一致 {CountAfter(mark, TargetChanged)} 件");

        // 整形結果が残っていること(やり直せること)。
        var result = ValueOf(palette, "ResultTextBox");
        failures += Check(Squash(result ?? string.Empty) == expected, "整形結果は残っている");
        Console.WriteLine($"  パレットの表示: {Trim(Squash(ValueOf(palette, "StatusText") ?? TextOf(palette, "StatusText")), 60)}");

        // E8: 中止したのだから、クリップボードは元の内容に戻っている。
        failures += Check(WaitUntil(() => ClipboardIs(ClipboardMark), 5000, 250),
            "E8: 中止後のクリップボードは元の内容(整形結果ではない)");
        return failures;
    }

    /// <summary>
    /// E3: 整形したあと別のウィンドウを前面にしてから「置き換える」。
    /// 別のウィンドウへは貼らない。E4(やり直せること)も続けて見る。
    /// </summary>
    private static int E3(List<int> pids, string expected)
    {
        var a = OpenA();
        var b = OpenB();
        var palette = Format(pids, a);

        if (palette is null)
        {
            return 1;
        }

        // 別のウィンドウを前面にする。
        if (!b.Activate())
        {
            Console.WriteLine("  別のウィンドウを前面にできませんでした。");
            return 1;
        }

        var mark = LogMark();
        Invoker(ById(palette, "ApplyButton")!)?.Invoke();
        Thread.Sleep(3000);

        var failures = Check(Squash(b.Content) == Squash(Other), "別のウィンドウには貼られていない");

        var pastedToA = Squash(a.Content) == expected;
        var aborted = CountAfter(mark, FocusFailed) + CountAfter(mark, TargetChanged) > 0;

        Console.WriteLine(pastedToA
            ? "  起きたこと: 取得元へフォーカスを戻して貼った(設計どおり。中止にはならない)"
            : aborted
                ? "  起きたこと: 書き戻しを中止した"
                : "  起きたこと: どちらでもない(貼られておらず、中止の記録もない)");

        failures += Check(pastedToA || aborted, "貼るか中止するかのどちらかで終わる");

        if (pastedToA)
        {
            Console.WriteLine("  E4(やり直し)は不要: 中止にならず貼れたため");
            return failures;
        }

        // 中止になった場合は、選び直して押せば通ること(E4)。
        if (!a.Activate())
        {
            Console.WriteLine("  取得元を前面にできませんでした。");
            return failures + 1;
        }

        Invoker(ById(palette, "ApplyButton")!)?.Invoke();
        failures += Check(WaitUntil(() => Squash(a.Content) == expected, 15000, 250),
            "E4: 選び直せば書き戻せる");
        return failures;
    }

    /// <summary>E5: 即実行の整形中に取得元を閉じる。結果はクリップボードへ退避される。</summary>
    private static int E5(List<int> pids, string expected)
    {
        var a = OpenA();

        if (!Ready(a))
        {
            return 1;
        }

        var mark = LogMark();
        SyntheticInput.TapWithControlShift(SyntheticInput.VkW);

        // 取得が終わってから閉じる(閉じるのが早すぎると取得自体が失敗する)。
        if (!WaitUntil(() => CountAfter(mark, "選択テキストを取得しました") >= 1, 10000, 100))
        {
            Console.WriteLine("  取得が始まりませんでした。");
            return 1;
        }

        a.Dispose();
        _a = null;

        var preserved = WaitUntil(() => CountAfter(mark, Preserved) >= 1, 30000, 250);
        var failures = Check(preserved, "整形結果をクリップボードへ退避した記録が残る");
        failures += Check(CountAfter(mark, FocusFailed) >= 1, "元のウィンドウへ戻せなかったことが残る");
        failures += Check(WaitUntil(() => ClipboardIs(expected), 5000, 250),
            "クリップボードから整形結果を貼れる状態になっている");
        return failures;
    }

    /// <summary>E6: 即実行の整形中に別のウィンドウを前面にする。別の窓へは貼らない。</summary>
    private static int E6(List<int> pids, string expected)
    {
        var a = OpenA();
        var b = OpenB();

        if (!a.Activate() || !Ready(a))
        {
            return 1;
        }

        var mark = LogMark();
        SyntheticInput.TapWithControlShift(SyntheticInput.VkW);

        if (!WaitUntil(() => CountAfter(mark, "選択テキストを取得しました") >= 1, 10000, 100))
        {
            Console.WriteLine("  取得が始まりませんでした。");
            return 1;
        }

        if (!b.Activate())
        {
            Console.WriteLine("  別のウィンドウを前面にできませんでした。");
            return 1;
        }

        Thread.Sleep(6000);

        var failures = Check(Squash(b.Content) == Squash(Other), "別のウィンドウには貼られていない");

        var pastedToA = Squash(a.Content) == expected;
        var preserved = CountAfter(mark, Preserved) >= 1;

        Console.WriteLine(pastedToA
            ? "  起きたこと: 取得元へフォーカスを戻して貼った(設計どおり)"
            : preserved
                ? "  起きたこと: 書き戻しを中止し、結果をクリップボードへ退避した"
                : "  起きたこと: どちらでもない");

        failures += Check(pastedToA || preserved, "貼るか退避するかのどちらかで終わる");
        return failures;
    }

    /// <summary>E7: 即実行で何もせず待つ。従来どおり置き換わり、中止のログが出ない。</summary>
    private static int E7(List<int> pids, string expected)
    {
        var a = OpenA();

        if (!Ready(a))
        {
            return 1;
        }

        var mark = LogMark();
        SyntheticInput.TapWithControlShift(SyntheticInput.VkW);

        var replaced = WaitUntil(() => Squash(a.Content) == expected, 40000, 250);
        var failures = Check(replaced, "選択範囲が置き換わる");
        failures += Check(CountAfter(mark, TargetChanged) == 0, "貼り付け先の中止ログが出ない");
        failures += Check(CountAfter(mark, FocusFailed) == 0, "フォーカス復帰の失敗が出ない");
        return failures;
    }

    // ------------------------------------------------------------------
    // 道具
    // ------------------------------------------------------------------

    private static TextSourceWindow OpenA()
    {
        _a = TextSourceWindow.Open(Sample, "試作A(取得元)")
            ?? throw new InvalidOperationException("取得元の窓を出せませんでした。");
        return _a;
    }

    private static TextSourceWindow OpenB()
    {
        _b = TextSourceWindow.Open(Other, "試作B(別の窓)")
            ?? throw new InvalidOperationException("別の窓を出せませんでした。");

        // 出した直後は B が前面なので、取得元へ戻しておく。
        _a?.Activate();
        return _b;
    }

    /// <summary>取得元からパレットを開いて整形し、「置き換える」が押せる状態にする。</summary>
    private static AutomationElement? Format(List<int> pids, TextSourceWindow source)
    {
        if (!Ready(source))
        {
            return null;
        }

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

        var preset = ByName(palette, "整える", ControlType.CheckBox);
        var toggle = preset is null ? null : Toggler(preset);

        if (toggle is not null && toggle.Current.ToggleState != ToggleState.On)
        {
            toggle.Toggle();
        }

        Invoker(ById(palette, "RunButton")!)?.Invoke();

        var apply = ById(palette, "ApplyButton");

        if (!WaitUntil(() => apply is not null && Safe(() => apply.Current.IsEnabled), 40000))
        {
            Console.WriteLine("  整形が終わりませんでした。");
            return null;
        }

        return palette;
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
        }
    }

    private static bool ClipboardIs(string text)
    {
        try
        {
            return System.Windows.Forms.Clipboard.ContainsText()
                && Squash(System.Windows.Forms.Clipboard.GetText()) == Squash(text);
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
