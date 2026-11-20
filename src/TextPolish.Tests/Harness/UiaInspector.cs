using System.Diagnostics;
using System.Windows.Automation;
using static TextPolish.Tests.Harness.UiaTree;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 動いているアプリを UI Automation で覗いて、何が見えるかを出すだけの道具。
///
/// なぜ要るか: 実機確認の自動化で UIA2 を使えるかは、このアプリで実際に見えるかどうかで
/// 決まる。このアプリは整形パレットと設定画面が WPF、トレイメニューが WinForms
/// (<c>TrayIconManager.cs</c> の <c>ContextMenuStrip</c>)で混在しており、
/// FlaUI の README は「UIA2 は WPF に弱い」「UIA3 は WinForms にバグがありうる」と
/// 両方向の注意を書いている。どちらが当たるかは覗いてみないと分からない。
///
/// **既定では読み取りしかしない。** クリックもキー送出も行わないため、
/// 誰かが同じアプリで手作業の確認をしている最中でも流していい。
/// 例外は <c>--open-tray</c> だけで、これは通知領域の「非表示のアイコンを表示する」を
/// 1回だけ押す(アプリ自体には触らない)。隠れたアイコンは、開くまで UIA の木に現れない。
///
/// 使い方:
///   TextPolish.Tests --inspect              いまの状態を出す
///   TextPolish.Tests --inspect --wait 20    新しいウィンドウが出るまで20秒待って出す
///   TextPolish.Tests --inspect --open-tray  隠れたアイコンを開いてから探す
/// </summary>
internal static class UiaInspector
{
    /// <summary>トレイに出る名前の素。<c>TrayIconManager.BaseText</c> と同じ文字列。</summary>
    private const string TrayBaseText = "テキスト整形ツール";

    private const string ProcessName = "TextPolish";

    public static int Run(string[] args)
    {
        var waitSeconds = IntArg(args, "--wait", 0);

        var pids = Process.GetProcessesByName(ProcessName).Select(p => p.Id).ToList();

        if (pids.Count == 0)
        {
            Console.WriteLine($"{ProcessName} が起動していません。");
            Console.WriteLine("アプリを起動してからもう一度実行してください。");
            return 1;
        }

        Console.WriteLine($"{ProcessName} のプロセス: {string.Join(", ", pids)}");
        Console.WriteLine();

        if (args.Contains("--open-tray", StringComparer.OrdinalIgnoreCase))
        {
            ShowHiddenIcons();
        }

        DumpTrayItem();
        Console.WriteLine();

        if (waitSeconds > 0)
        {
            WaitForNewWindow(pids, waitSeconds);
        }

        DumpAppWindows(pids);
        return 0;
    }

    /// <summary>通知領域のこのアプリのアイコンを探して出す(探し方は UiaTree にある)。</summary>
    private static void DumpTrayItem()
    {
        Console.WriteLine("== 通知領域 ==");

        var roots = TrayRoots();

        if (roots.Count == 0)
        {
            Console.WriteLine("  Shell_TrayWnd が見つかりませんでした。");
            return;
        }

        Console.WriteLine($"  見ている入れ物: {string.Join(" / ", roots.Select(r => Safe(() => r.Current.ClassName)))}");

        var hit = FindTrayItem(TrayBaseText);

        if (hit is null)
        {
            Console.WriteLine($"  「{TrayBaseText}」を含む項目は見つかりませんでした。");
            Console.WriteLine("  隠れたアイコンの中にある可能性があります(「非表示のアイコンを表示する」を開くと出ます)。");
            return;
        }

        Describe(hit, "  ");
    }

    private static void ShowHiddenIcons()
    {
        if (OpenHiddenIcons())
        {
            Console.WriteLine("隠れたアイコンを開きました。");
            return;
        }

        Console.WriteLine("「非表示のアイコンを表示する」ボタンを押せませんでした。");
        Console.WriteLine("隠れたアイコンが無い(すべて表示されている)環境かもしれません。");
    }

    /// <summary>新しいトップレベルウィンドウが出るまで待つ。利用者がホットキーを押す間。</summary>
    private static void WaitForNewWindow(List<int> pids, int seconds)
    {
        var before = TopLevelWindows(pids).Select(w => Safe(() => w.Current.NativeWindowHandle)).ToHashSet();

        Console.WriteLine($"新しいウィンドウが出るまで最大 {seconds} 秒待ちます。");
        Console.WriteLine("いま整形パレットのホットキー(F10)か、トレイから設定画面を開いてください。");

        var deadline = Environment.TickCount64 + (seconds * 1000);

        while (Environment.TickCount64 < deadline)
        {
            var now = TopLevelWindows(pids);
            var appeared = now.FirstOrDefault(
                w => !before.Contains(Safe(() => w.Current.NativeWindowHandle)));

            if (appeared is not null)
            {
                Console.WriteLine($"ウィンドウが出ました: {Safe(() => appeared.Current.Name)}");
                Console.WriteLine();
                return;
            }

            Thread.Sleep(250);
        }

        Console.WriteLine("時間内に新しいウィンドウは出ませんでした。");
        Console.WriteLine();
    }

    /// <summary>このアプリのトップレベルウィンドウを、中身まで出す。</summary>
    private static void DumpAppWindows(List<int> pids)
    {
        Console.WriteLine("== アプリのウィンドウ ==");

        var windows = TopLevelWindows(pids);

        if (windows.Count == 0)
        {
            Console.WriteLine("  表示中のウィンドウはありません(トレイに常駐しているだけの状態)。");
            Console.WriteLine("  --wait を付けて実行し、パレットか設定画面を開くと中身が出ます。");
            return;
        }

        foreach (var window in windows)
        {
            Console.WriteLine();
            Describe(window, "  ");
            Console.WriteLine("  -- 中の要素 --");

            var shown = 0;
            Collect(window, 0, 8, e =>
            {
                if (shown >= 60)
                {
                    return;
                }

                var type = TypeOf(e);
                var name = Squash(Safe(() => e.Current.Name));

                // 名前の無い入れ物(Pane 等)は出さない。読めなくなるため。
                if (name.Length == 0 && type is not ("Edit" or "Button" or "CheckBox" or "ComboBox"))
                {
                    return;
                }

                Describe(e, "    ");
                shown++;
            });

            if (shown == 0)
            {
                Console.WriteLine("    (名前の付いた要素が見つかりませんでした)");
            }
        }
    }
}
