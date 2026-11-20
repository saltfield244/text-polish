using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 実機確認手順「I. 自由指示の履歴」を流す。
///
/// 見るべきことは1つ。**「保存しない」と選んだら、本当に残らないか。**
/// 履歴は <c>%AppData%\TextPolish\instruction-history.txt</c> に**平文**で置かれる。
/// 単体テスト(<c>InstructionHistoryTests</c>)は一時フォルダで同じ判定を固定しているが、
/// 固定できないのは「設定画面の操作が、実際にそのファイルへ届くか」である。
/// チェックの読み書き・保存時の削除・ボタンの配線のどこが外れても、
/// 利用者から見れば「オフにしたのに残っている」になる。
///
/// **このファイルの中身は読まない。** 自由指示には実務の文脈が混ざるため、
/// 見るのは「あるか/ないか」と行数だけにしてある。
///
/// 流す前に既存の履歴があれば**メモリへ退避**し、終わったら書き戻す
/// (確認のために利用者の履歴を失わせない。ファイルへは複製しない)。
/// I2・I3 は設定画面から保存するため <c>appsettings.json</c> を書き換える。
/// 流した側が戻すこと(README に書いた)。
/// </summary>
internal static class ChecklistI
{
    /// <summary>確認用に置く行。利用者の文章ではないので、消えても困らない。</summary>
    private static readonly string[] Planted =
    {
        "I-HISTORY-9B31 かくにんようのしじ1",
        "I-HISTORY-9B31 かくにんようのしじ2",
    };

    private static string _exePath = string.Empty;

    /// <summary>流す前にあった履歴。ファイルへは書かず、ここだけで持つ。</summary>
    private static string[]? _saved;

    private static bool _hadFile;

    public static int Run(string name, string[] args, Process app, List<int> pids)
    {
        _exePath = app.MainModule?.FileName ?? string.Empty;

        if (_exePath.Length == 0)
        {
            Console.WriteLine("アプリの場所が読めませんでした。設定ファイルを見られないため流しません。");
            return 1;
        }

        Console.WriteLine($"設定ファイル: {SettingsPath()}");
        Console.WriteLine($"履歴ファイル: {HistoryPath()}");
        Console.WriteLine("**I2・I3 は設定画面から保存するため、設定ファイルを書き換えます。**");
        Console.WriteLine("履歴ファイルは退避してから触り、最後に書き戻します(中身は表示しません)。");

        var items = new (string Key, Func<List<int>, int> Body)[]
        {
            ("i1", I1),
            ("i2", I2),
            ("i3", I3),
            ("i4", I4),
        };

        BackUpHistory();

        try
        {
            if (name == "i")
            {
                var total = items.Sum(item => RunOne(item.Key, item.Body, pids));

                Console.WriteLine();
                Console.WriteLine(total == 0
                    ? "I: 流した項目は全部期待どおり。"
                    : $"I: 期待どおりでなかった確認: {total} 件");
                return total == 0 ? 0 : 1;
            }

            var one = items.FirstOrDefault(i => i.Key == name);

            if (one.Body is null)
            {
                Console.WriteLine($"{name} はここでは扱っていません(i1〜i4、または i)。");
                return 1;
            }

            return RunOne(name, one.Body, pids) == 0 ? 0 : 1;
        }
        finally
        {
            RestoreHistory();
        }
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
    /// I1: 設定画面に欄が出ているか。既定は「保存する」。
    /// 欄が無ければ以降の項目は意味を持たないので、ここで止める。
    /// </summary>
    private static int I1(List<int> pids)
    {
        var settings = OpenSettings(pids);

        if (settings is null)
        {
            return 1;
        }

        var check = ById(settings, "SaveInstructionHistoryCheckBox");
        var failures = Check(check is not null, "「自由指示の履歴」のチェックがある");

        var button = ById(settings, "ClearInstructionHistoryButton");
        failures += Check(button is not null, "「履歴を消す」ボタンがある");
        failures += Check(
            button is not null && Safe(() => button.Current.IsEnabled),
            "「履歴を消す」が押せる状態になっている");

        if (check is not null)
        {
            var toggle = Toggler(check);
            failures += Check(
                toggle is not null && Safe(() => toggle.Current.ToggleState) == ToggleState.On,
                "既定では「保存する」(オン)");
        }

        Cancel(settings);
        return failures;
    }

    /// <summary>
    /// I2: 履歴を置いてからチェックを外して保存する。設定が false になり、
    /// **ファイルそのものが消える**。残っていれば「保存しない」と言えない。
    /// </summary>
    private static int I2(List<int> pids)
    {
        Plant();

        var failures = Check(File.Exists(HistoryPath()), "確認用の履歴を置けた");

        if (failures > 0)
        {
            return failures;
        }

        var log = LogFileAssert.Today();
        var mark = log.LineCount;

        failures += SetSetting(pids, on: false);

        if (failures > 0)
        {
            return failures;
        }

        failures += Check(!SettingIsOn(), "appsettings.json の SaveInstructionHistory が false になる");
        failures += Check(!File.Exists(HistoryPath()), "履歴ファイルが消える");
        failures += Check(
            log.WaitForLine("自由指示の履歴を削除しました。", 5000, mark) is not null,
            "ログに削除したことが残る");
        return failures;
    }

    /// <summary>
    /// I3: チェックを戻して保存する。設定は true に戻るが、**消した履歴は復活しない**。
    /// 「消えたように見えて実は残っていた」の裏返しも見ておく。
    /// </summary>
    private static int I3(List<int> pids)
    {
        if (SettingIsOn())
        {
            Console.WriteLine("  いまオンです。I3 は I2 の続きなので、I2 のあとに流してください。");
            return 1;
        }

        var failures = SetSetting(pids, on: true);

        if (failures > 0)
        {
            return failures;
        }

        failures += Check(SettingIsOn(), "appsettings.json の SaveInstructionHistory が true に戻る");
        failures += Check(!File.Exists(HistoryPath()), "戻しても消した履歴は復活しない");
        return failures;
    }

    /// <summary>
    /// I4: 「履歴を消す」ボタン。確認を出して、OK でファイルを消し、結果を画面に出す。
    /// 保存を待たずにその場で効くので、設定を保存せず(キャンセルで閉じて)確かめる。
    /// </summary>
    private static int I4(List<int> pids)
    {
        Plant();

        var failures = Check(File.Exists(HistoryPath()), "確認用の履歴を置けた");

        if (failures > 0)
        {
            return failures;
        }

        var settings = OpenSettings(pids);

        if (settings is null)
        {
            return 1;
        }

        var button = ById(settings, "ClearInstructionHistoryButton");

        if (button is null)
        {
            Console.WriteLine("  「履歴を消す」ボタンが見つかりません。");
            Cancel(settings);
            return 1;
        }

        // Invoke は確認ダイアログが閉じるまで戻ってこない(モーダル)。別のスレッドから
        // 押して、こちらでダイアログを探す。トレイのメニューと同じ事情。
        var pressed = new Thread(() => Invoker(button)?.Invoke()) { IsBackground = true };
        pressed.Start();

        AutomationElement? ok = null;

        var appeared = WaitUntil(
            () => (ok = TopLevelWindows(pids)
                .Select(w => ByName(w, "OK", ControlType.Button))
                .FirstOrDefault(b => b is not null)) is not null,
            10000,
            200);

        failures += Check(appeared && ok is not null, "消す前に確認を出す");

        if (!appeared || ok is null)
        {
            Cancel(settings);
            return failures;
        }

        Invoker(ok)?.Invoke();
        pressed.Join(10000);
        Thread.Sleep(500);

        failures += Check(!File.Exists(HistoryPath()), "OK で履歴ファイルが消える");

        var status = ById(settings, "InstructionHistoryStatus");
        failures += Check(
            status is not null && (Safe(() => status.Current.Name) ?? string.Empty).Length > 0,
            "画面に結果が出る");

        // 設定は保存しない。ボタンは保存を待たずに効くはずなので、ここで閉じても消えたまま。
        Cancel(settings);
        Thread.Sleep(400);
        failures += Check(!File.Exists(HistoryPath()), "キャンセルで閉じても消えたまま");
        return failures;
    }

    // ------------------------------------------------------------------

    /// <summary>
    /// 設定画面のチェックを切り替えて保存する。すでにその状態なら何もしない。
    /// </summary>
    private static int SetSetting(List<int> pids, bool on)
    {
        if (SettingIsOn() == on)
        {
            Console.WriteLine($"  履歴の保存はすでに{(on ? "オン" : "オフ")}です。設定画面は開きません。");
            return 0;
        }

        var settings = OpenSettings(pids);

        if (settings is null)
        {
            return 1;
        }

        var box = ById(settings, "SaveInstructionHistoryCheckBox");
        var toggle = box is null ? null : Toggler(box);

        if (toggle is null)
        {
            Console.WriteLine("  「自由指示の履歴」のチェックが見つかりません。");
            Cancel(settings);
            return 1;
        }

        var isOn = Safe(() => toggle.Current.ToggleState) == ToggleState.On;

        if (isOn != on)
        {
            toggle.Toggle();
            Thread.Sleep(200);
        }

        var save = ById(settings, "SaveButton");

        if (save is null)
        {
            Console.WriteLine("  保存ボタンが見つかりません。");
            Cancel(settings);
            return 1;
        }

        Invoker(save)?.Invoke();

        if (!WaitUntil(
            () => TopLevelWindows(pids).All(w => ById(w, "SaveButton") is null), 10000, 200))
        {
            Console.WriteLine("  設定画面が閉じませんでした。");
            return 1;
        }

        // 保存のたびにホットキーを張り直している(App.ReconfigureHotkeys)。
        Thread.Sleep(800);
        Console.WriteLine($"  履歴の保存を{(on ? "オン" : "オフ")}にして保存しました。");
        return 0;
    }

    /// <summary>トレイのメニューから設定画面を開く。</summary>
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

    // ------------------------------------------------------------------

    private static string SettingsPath() =>
        Path.Combine(Path.GetDirectoryName(_exePath)!, "appsettings.json");

    private static string HistoryPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextPolish",
        "instruction-history.txt");

    private static bool SettingIsOn()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SettingsPath()));

            // 既定は true。項目が無い古い設定ファイルでも「保存する」と見なす。
            if (!document.RootElement.TryGetProperty("Polish", out var polish)
                || !polish.TryGetProperty("SaveInstructionHistory", out var value))
            {
                return true;
            }

            return value.ValueKind != JsonValueKind.False;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.WriteLine($"  設定ファイルを読めませんでした: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>
    /// 流す前にあった履歴をメモリへ退避する。確認のためにファイルを消すので、
    /// 利用者の履歴を失わせないため。**中身は表示しないし、別のファイルへも書かない。**
    /// </summary>
    private static void BackUpHistory()
    {
        _hadFile = File.Exists(HistoryPath());

        if (!_hadFile)
        {
            Console.WriteLine("  退避: 履歴ファイルはありません。");
            return;
        }

        try
        {
            _saved = File.ReadAllLines(HistoryPath());
            Console.WriteLine($"  退避: 履歴 {_saved.Length} 行をメモリに持ちました。");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"  退避できませんでした({ex.GetType().Name})。I は流しません。");
            throw;
        }
    }

    private static void RestoreHistory()
    {
        if (!_hadFile)
        {
            // 元から無かったので、置いた確認用の行を消しておく。
            try
            {
                if (File.Exists(HistoryPath()))
                {
                    File.Delete(HistoryPath());
                }
            }
            catch (IOException)
            {
            }

            return;
        }

        if (_saved is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath())!);
            File.WriteAllLines(HistoryPath(), _saved);
            Console.WriteLine($"  書き戻し: 履歴 {_saved.Length} 行を戻しました。");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"  !! 履歴を書き戻せませんでした: {ex.Message}");
        }
    }

    /// <summary>確認用の履歴を置く。利用者の文章は使わない。</summary>
    private static void Plant()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath())!);
        File.WriteAllLines(HistoryPath(), Planted);
    }

    private static int Check(bool ok, string what)
    {
        Console.WriteLine($"  {(ok ? "OK  " : "NG  ")}{what}");
        return ok ? 0 : 1;
    }
}
