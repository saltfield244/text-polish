using System.Diagnostics;
using System.IO;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 実機確認手順「J. 送信先の検査(HTTPS)」を流す。
///
/// 送信直前の検査は、3つの Formatter を本物のまま動かす単体テスト
/// (<c>EndpointPolicyTests</c>)で「http へは1バイトも送らない」ところまで固定してある。
/// **ここでしか見られないのは、設定画面の配線**である。保存ボタンに判定がつながって
/// いなければ、利用者は平文の送信先を保存でき、整形して初めて失敗に気づくことになる。
///
/// そのため J は設定画面だけを見る。整形は走らせないので、LLMへは何も飛ばない。
/// **保存を1回行うため <c>appsettings.json</c> を書き換える**(流した側が戻すこと)。
///
/// 見ているのはいま選ばれているプロバイダの欄。判定も同じで、使っていないプロバイダの
/// 欄は塞がない(切り替えの準備中に保存できなくなるため)。
/// </summary>
internal static class ChecklistJ
{
    /// <summary>保存できてはいけない送信先。実在のホストだが、ここへは何も送らない。</summary>
    private const string PlainHttp = "http://openrouter.ai/api/v1/chat/completions";

    private static string _exePath = string.Empty;

    public static int Run(string name, string[] args, Process app, List<int> pids)
    {
        _exePath = app.MainModule?.FileName ?? string.Empty;

        if (_exePath.Length == 0)
        {
            Console.WriteLine("アプリの場所が読めませんでした。設定ファイルを見られないため流しません。");
            return 1;
        }

        Console.WriteLine($"設定ファイル: {SettingsPath()}");
        Console.WriteLine("**J2 は保存するため、設定ファイルを書き換えます。**");
        Console.WriteLine("整形は走らせません(LLMへは何も飛びません)。");

        var items = new (string Key, Func<List<int>, int> Body)[]
        {
            ("j1", J1),
            ("j2", J2),
        };

        if (name == "j")
        {
            var total = items.Sum(item => RunOne(item.Key, item.Body, pids));

            Console.WriteLine();
            Console.WriteLine(total == 0 ? "J: 流した項目は全部期待どおり。" : $"J: 期待どおりでなかった確認: {total} 件");
            return total == 0 ? 0 : 1;
        }

        var one = items.FirstOrDefault(i => i.Key == name);

        if (one.Body is null)
        {
            Console.WriteLine($"{name} はここでは扱っていません(j1・j2、または j)。");
            return 1;
        }

        return RunOne(name, one.Body, pids) == 0 ? 0 : 1;
    }

    private static int RunOne(string key, Func<List<int>, int> body, List<int> pids)
    {
        Console.WriteLine();
        Console.WriteLine($"===== {key.ToUpperInvariant()} =====");

        try
        {
            var failures = body(pids);
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
            CloseSettingsWindows(pids);
        }
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// J1: いま選んでいるプロバイダの欄を http:// にして保存を押す。
    /// **保存されず、画面も閉じず、理由が出る。** 設定ファイルも変わらない。
    /// </summary>
    private static int J1(List<int> pids)
    {
        var settings = OpenSettings(pids);

        if (settings is null)
        {
            return 1;
        }

        if (!SelectLlmTab(settings))
        {
            Cancel(settings);
            return 1;
        }

        var box = SelectedEndpointBox(settings, out var providerName);

        if (box is null)
        {
            Console.WriteLine("  選ばれているプロバイダのエンドポイント欄が見つかりません。");
            Cancel(settings);
            return 1;
        }

        Console.WriteLine($"  いま選ばれているプロバイダ: {providerName}");

        var before = File.ReadAllText(SettingsPath());
        var original = Valuer(box)?.Current.Value ?? string.Empty;

        Valuer(box)?.SetValue(PlainHttp);
        Thread.Sleep(200);

        Invoker(ById(settings, "SaveButton")!)?.Invoke();
        Thread.Sleep(800);

        var stillOpen = TopLevelWindows(pids).Any(w => ById(w, "SaveButton") is not null);
        var failures = Check(stillOpen, "保存されず、設定画面が開いたまま");

        var warning = ById(settings, "EndpointWarning");
        var shown = warning is not null
            && Safe(() => warning.Current.IsOffscreen) == false
            && (Safe(() => warning.Current.Name) ?? string.Empty).Length > 0;

        failures += Check(shown, "理由が画面に出る");

        if (shown)
        {
            Console.WriteLine($"  出ていた文面: {warning!.Current.Name}");
        }

        failures += Check(File.ReadAllText(SettingsPath()) == before, "設定ファイルは変わっていない");

        // 元の値へ戻してからキャンセル。閉じるだけなので設定ファイルは触らない。
        Valuer(box)?.SetValue(original);
        Thread.Sleep(150);
        Cancel(settings);
        return failures;
    }

    /// <summary>
    /// J2: https のまま保存すると、ふつうに保存できて閉じる。
    /// J1 で止めすぎていないことの裏返し。
    /// </summary>
    private static int J2(List<int> pids)
    {
        var settings = OpenSettings(pids);

        if (settings is null)
        {
            return 1;
        }

        if (!SelectLlmTab(settings))
        {
            Cancel(settings);
            return 1;
        }

        var box = SelectedEndpointBox(settings, out _);
        var value = box is null ? string.Empty : Valuer(box)?.Current.Value ?? string.Empty;

        if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"  いまの送信先が https ではありません({value})。J2 は流せません。");
            Cancel(settings);
            return 1;
        }

        Invoker(ById(settings, "SaveButton")!)?.Invoke();

        var closed = WaitUntil(
            () => TopLevelWindows(pids).All(w => ById(w, "SaveButton") is null), 10000, 200);

        // 保存のたびにホットキーを張り直している(App.ReconfigureHotkeys)。
        Thread.Sleep(800);
        return Check(closed, "https のままなら保存できて閉じる");
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 「LLM」タブを選ぶ。WPF の TabControl は選ばれるまで中身を作らないため、
    /// 選ばずに AutomationId を引いても見つからない(最初それで J が空振りした)。
    /// </summary>
    private static bool SelectLlmTab(AutomationElement settings)
    {
        var tab = ByName(settings, "LLM", ControlType.TabItem);

        if (tab is null)
        {
            Console.WriteLine("  「LLM」タブが見つかりません。");
            return false;
        }

        var select = Safe(() =>
            tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var raw)
                ? raw as SelectionItemPattern
                : null);

        if (select is null)
        {
            Console.WriteLine("  「LLM」タブを選べません。");
            return false;
        }

        select.Select();
        Thread.Sleep(500);
        return true;
    }

    /// <summary>
    /// いま選ばれているプロバイダのエンドポイント欄。
    ///
    /// ラジオの状態は見ない。設定画面は選ばれていないプロバイダの入れ物を
    /// <c>Visibility="Collapsed"</c> にしており、畳まれた中身は UIA の木に出てこない。
    /// そのため**見つかった欄がそのまま、いま選ばれているプロバイダの欄**になる
    /// (最初ラジオの Toggle を見ようとして空振りした。RadioButton は Toggle ではなく
    /// SelectionItem のパターンを持つうえ、そもそも中身が無いので引けない)。
    /// </summary>
    private static AutomationElement? SelectedEndpointBox(
        AutomationElement settings, out string providerName)
    {
        foreach (var (box, label) in new[]
        {
            ("GeminiEndpointBox", "Gemini"),
            ("OpenAiEndpointBox", "OpenAI互換"),
            ("AzureEndpointBox", "Azure OpenAI"),
        })
        {
            var element = ById(settings, box);

            if (element is not null)
            {
                providerName = label;
                return element;
            }
        }

        providerName = "(判別できず)";
        return null;
    }

    private static AutomationElement? OpenSettings(List<int> pids)
    {
        var already = TopLevelWindows(pids).FirstOrDefault(w => ById(w, "SaveButton") is not null);

        if (already is not null)
        {
            return already;
        }

        var menu = UiaDriver.OpenTrayMenu();

        if (menu is null)
        {
            Console.WriteLine("  トレイのメニューが出ませんでした。");
            return null;
        }

        if (!UiaDriver.ClickMenuItem(menu, "設定"))
        {
            UiaDriver.CloseMenu();
            return null;
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
            return null;
        }

        Thread.Sleep(600);
        return settings;
    }

    private static void Cancel(AutomationElement settings)
    {
        var cancel = ById(settings, "CancelButton");

        if (cancel is not null)
        {
            Invoker(cancel)?.Invoke();
            Thread.Sleep(400);
        }
    }

    private static void CloseSettingsWindows(List<int> pids)
    {
        foreach (var window in TopLevelWindows(pids))
        {
            if (ById(window, "SaveButton") is not null)
            {
                Cancel(window);
            }
        }
    }

    private static string SettingsPath() =>
        Path.Combine(Path.GetDirectoryName(_exePath)!, "appsettings.json");

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "NG  ")}{what}");
        return ok ? 0 : 1;
    }
}
