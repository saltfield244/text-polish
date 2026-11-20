using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace TextPolish.Tests.Harness;

/// <summary>
/// UI Automation の木を読むための共通部品。<see cref="UiaInspector"/>(覗くだけ)と
/// <see cref="UiaDriver"/>(操作する)の両方から使う。
///
/// UIA2(<c>System.Windows.Automation</c>)を使っている。Windows デスクトップ SDK に
/// 入っているため外部NuGetは増えない。アプリ本体も
/// <c>ActiveWindowContextProvider</c> で同じ UIA2 を使っている。
/// </summary>
internal static class UiaTree
{
    /// <summary>
    /// UIA の読み取りは、相手のウィンドウが閉じた瞬間などに例外になる。
    /// 木をたどる途中で落ちると何も分からないため、1要素の失敗は飛ばして続ける。
    /// </summary>
    public static T? Safe<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (ElementNotAvailableException)
        {
            return default;
        }
        catch (COMException)
        {
            return default;
        }
    }

    /// <summary>条件が満たされるまで待つ。満たされたら true。</summary>
    public static bool WaitUntil(Func<bool> condition, int timeoutMs, int pollMs = 200)
    {
        var deadline = Environment.TickCount64 + timeoutMs;

        while (true)
        {
            if (Safe(condition))
            {
                return true;
            }

            if (Environment.TickCount64 >= deadline)
            {
                return false;
            }

            Thread.Sleep(pollMs);
        }
    }

    /// <summary>このプロセスのトップレベルウィンドウ。</summary>
    public static List<AutomationElement> TopLevelWindows(List<int> pids) =>
        AutomationElement.RootElement
            .FindAll(TreeScope.Children, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(e => pids.Contains(Safe(() => e.Current.ProcessId)))
            .ToList();

    /// <summary>AutomationId で子孫を1つ探す。</summary>
    public static AutomationElement? ById(AutomationElement root, string automationId) =>
        Safe(() => root.FindFirst(
            TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, automationId)));

    /// <summary>
    /// 名前と種類で子孫を1つ探す。
    ///
    /// 種類での絞り込みが必須。整形パレットのプリセットは AutomationId を持たず、
    /// しかも同じ名前の TextBlock が隣に必ずいる(「整える」の CheckBox と Text)。
    /// 名前だけで引くと TextBlock を掴み、Toggle が無いので後段で失敗する。
    /// </summary>
    public static AutomationElement? ByName(AutomationElement root, string name, ControlType type) =>
        Safe(() => root.FindFirst(
            TreeScope.Descendants,
            new AndCondition(
                new PropertyCondition(AutomationElement.NameProperty, name),
                new PropertyCondition(AutomationElement.ControlTypeProperty, type))));

    public static InvokePattern? Invoker(AutomationElement element) => Pattern<InvokePattern>(
        element, InvokePattern.Pattern);

    public static TogglePattern? Toggler(AutomationElement element) => Pattern<TogglePattern>(
        element, TogglePattern.Pattern);

    public static ValuePattern? Valuer(AutomationElement element) => Pattern<ValuePattern>(
        element, ValuePattern.Pattern);

    private static T? Pattern<T>(AutomationElement element, AutomationPattern pattern)
        where T : BasePattern =>
        Safe(() => element.TryGetCurrentPattern(pattern, out var raw) ? raw as T : null);

    /// <summary>
    /// 通知領域を含む入れ物。タスクバー本体と、隠れたアイコンの入れ物の両方。
    ///
    /// この環境(Windows 11)のタスクバーは XAML で、クラス名は
    /// <c>SystemTray.NormalButton</c> 等。従来の <c>ToolbarWindow32</c> は無い。
    /// 隠れたアイコンは <c>TopLevelWindowForOverflowXamlIsland</c> に入り、
    /// 開くまで木に現れない。
    /// </summary>
    public static List<AutomationElement> TrayRoots() =>
        AutomationElement.RootElement
            .FindAll(TreeScope.Children, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(e =>
            {
                var cls = Safe(() => e.Current.ClassName) ?? string.Empty;
                return cls.Contains("Shell_TrayWnd")
                    || cls.Contains("NotifyIconOverflowWindow")
                    || cls.Contains("TopLevelWindowForOverflowXamlIsland");
            })
            .ToList();

    /// <summary>
    /// 通知領域から、名前の一部でアイコンを探す。
    ///
    /// 名前は <c>NotifyIcon.Text</c> がそのまま入るが、空白や改行が混じることがあるので
    /// 詰めてから部分一致で見る。<c>SetStatus</c> が状態を後ろに足すため
    /// (「- 整形中...」)、完全一致では見つからない。
    /// </summary>
    public static AutomationElement? FindTrayItem(string baseText) =>
        TrayRoots()
            .SelectMany(root => Descendants(root, 6))
            .FirstOrDefault(e => Squash(Safe(() => e.Current.Name))
                .Contains(baseText, StringComparison.Ordinal));

    /// <summary>
    /// 通知領域の「非表示のアイコンを表示する」を1回押して、隠れたアイコンを開く。
    /// ボタン自身が <c>Invoke</c> を持つので、座標やマウス送出は要らない。
    /// 押すのはこのボタンだけで、アプリには触らない。
    /// </summary>
    public static bool OpenHiddenIcons()
    {
        var button = TrayRoots()
            .Where(r => Safe(() => r.Current.ClassName)?.Contains("Shell_TrayWnd") == true)
            .SelectMany(tray => Descendants(tray, 6))
            .FirstOrDefault(e => Squash(Safe(() => e.Current.Name))
                .Contains("非表示のアイコンを表示する", StringComparison.Ordinal));

        var invoke = button is null ? null : Invoker(button);

        if (invoke is null)
        {
            return false;
        }

        invoke.Invoke();

        // 開いた直後は木に反映されていないことがある。少しだけ待つ。
        Thread.Sleep(600);
        return true;
    }

    /// <summary>1要素を1行で出す。対応パターンまで出すのが肝(Invoke があるかで操作方法が決まる)。</summary>
    public static void Describe(AutomationElement e, string indent)
    {
        var name = Squash(Safe(() => e.Current.Name));
        var cls = Safe(() => e.Current.ClassName) ?? string.Empty;
        var id = Safe(() => e.Current.AutomationId) ?? string.Empty;
        var enabled = Safe(() => e.Current.IsEnabled);
        var patterns = Safe(() => string.Join(
            ",",
            e.GetSupportedPatterns().Select(p => p.ProgrammaticName
                .Replace("PatternIdentifiers.Pattern", string.Empty)))) ?? string.Empty;

        var sb = new StringBuilder(indent)
            .Append('[').Append(TypeOf(e)).Append("] ")
            .Append('"').Append(Trim(name, 60)).Append('"');

        if (id.Length > 0)
        {
            sb.Append(" id=").Append(id);
        }

        if (cls.Length > 0)
        {
            sb.Append(" cls=").Append(cls);
        }

        sb.Append(enabled ? string.Empty : " (無効)");

        if (patterns.Length > 0)
        {
            sb.Append(" pat=").Append(patterns);
        }

        Console.WriteLine(sb.ToString());
    }

    public static string TypeOf(AutomationElement e) =>
        Safe(() => e.Current.ControlType.ProgrammaticName.Replace("ControlType.", string.Empty))
        ?? "?";

    /// <summary>子孫をすべて集める。深さを切るのは、タスクバーの木が深く広いため。</summary>
    public static List<AutomationElement> Descendants(AutomationElement element, int maxDepth)
    {
        var all = new List<AutomationElement>();
        Collect(element, 0, maxDepth, all.Add);
        return all;
    }

    /// <summary>子孫をたどって、見つけた要素ごとに処理を呼ぶ。</summary>
    public static void Collect(
        AutomationElement element, int depth, int maxDepth, Action<AutomationElement> visit)
    {
        if (depth >= maxDepth)
        {
            return;
        }

        var children = Safe(() => element.FindAll(TreeScope.Children, Condition.TrueCondition));

        if (children is null)
        {
            return;
        }

        foreach (AutomationElement child in children)
        {
            visit(child);
            Collect(child, depth + 1, maxDepth, visit);
        }
    }

    /// <summary>空白と改行を1つのスペースに詰める。トレイの名前は改行を含むため。</summary>
    public static string Squash(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string Trim(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    public static int IntArg(string[] args, string name, int fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    public static string StringArg(string[] args, string name, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return fallback;
    }
}
