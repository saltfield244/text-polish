using System.IO;
using System.Net.Http;
using TextPolish.Core.Configuration;
using TextPolish.Core.Diff;
using TextPolish.Core.Llm;
using TextPolish.Core.Logging;

namespace TextPolish.ConsoleTest;

// LLM整形の手動検証用コンソールハーネス。
// GUIを起動せずに、設定とプロンプトの調整結果を素早く確認するために使う。
internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        var logger = new FileLogger();

        if (args.Length == 0)
        {
            Console.WriteLine("使い方:");
            Console.WriteLine("  TextPolish.ConsoleTest <appsettings.jsonのパス> [整形したい文] [プリセット名(カンマ区切りで複数可)]");
            Console.WriteLine();
            Console.WriteLine("例:");
            Console.WriteLine("  TextPolish.ConsoleTest .\\appsettings.json");
            Console.WriteLine("  TextPolish.ConsoleTest .\\appsettings.json \"あーときょうのばんごはんは\" \"誤変換だけ直す\"");
            Console.WriteLine("  TextPolish.ConsoleTest .\\appsettings.json \"あーと明日の会議ですが\" \"要約,英訳\"");
            Console.WriteLine("  TextPolish.ConsoleTest diff \"原文\" \"整形後\"   (LLMを呼ばずに差分だけ確認)");
            return;
        }

        // 差分表示の確認用。LLMを呼ばないので、書き換え率の傾向を見るのにも使える。
        if (string.Equals(args[0], "diff", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3)
            {
                Console.WriteLine("使い方: TextPolish.ConsoleTest diff \"原文\" \"整形後\"");
                return;
            }

            PrintDiff(args[1], args[2]);
            return;
        }

        await RunAsync(logger, args[0], args.Length >= 2 ? args[1] : null, args.Length >= 3 ? args[2] : null);
    }

    private static async Task RunAsync(FileLogger logger, string settingsPath, string? rawTextArg, string? presetName)
    {
        if (!File.Exists(settingsPath))
        {
            Console.WriteLine($"設定ファイルが見つかりません: {settingsPath}");
            return;
        }

        var settings = AppSettingsLoader.Load(settingsPath);
        var settingsDir = Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;

        // プロンプトと用語辞書の相対パスは、設定ファイルと同じフォルダを基準に解決する。
        var promptPath = ResolveRelative(settingsDir, settings.Polish.SystemPromptFilePath);
        var glossaryPath = ResolveRelative(settingsDir, settings.Polish.GlossaryFilePath);

        var rawText = rawTextArg ?? "えーと、明日の会議はですね、あの、3時からでお願いします";

        var presets = ResolvePresets(settings.Polish.Presets, presetName);

        if (presets.Count == 0)
        {
            Console.WriteLine($"プリセットが見つかりません: {presetName}");
            Console.WriteLine("利用可能: " + string.Join(" / ", settings.Polish.Presets.Select(p => p.Name)));
            return;
        }

        WarnOnConflictingGroups(presets);

        Console.WriteLine($"プロバイダ  : {settings.LlmProvider}");
        Console.WriteLine($"プリセット  : {string.Join(" ＋ ", presets.Select(p => p.Name))}");
        Console.WriteLine($"入力(整形前): {rawText}");
        Console.WriteLine();

        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, settings.RequestTimeoutSeconds)),
        };
        ISystemPromptProvider promptProvider = new GlossarySystemPromptProvider(
            new FileSystemPromptProvider(promptPath), glossaryPath, logger);
        var formatter = LlmFormatterFactory.Create(settings, httpClient, promptProvider, logger);

        try
        {
            var result = await formatter.FormatAsync(new LlmFormatRequest
            {
                RawText = rawText,
                TaskInstructions = presets.Select(p => p.Instruction).ToList(),
            });

            Console.WriteLine($"出力(整形後): {result}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"呼び出し失敗: {ex.Message}");
            Console.WriteLine("詳細はログを確認してください: %AppData%\\TextPolish\\logs\\");
        }
    }

    /// <summary>
    /// カンマ区切りで指定されたプリセット名を解決する。
    /// 指定した順ではなく Presets の定義順に揃えることで、パレットで複数選択したときと
    /// 同じ適用順(文体 → 要約 → 翻訳)で検証できるようにしている。
    /// </summary>
    private static List<FormatPreset> ResolvePresets(List<FormatPreset> all, string? names)
    {
        if (string.IsNullOrWhiteSpace(names))
        {
            return all.Take(1).ToList();
        }

        var wanted = names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return all
            .Where(p => wanted.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// 同じ排他グループのプリセットが複数指定されていたら警告する。
    /// パレットのUIでは選べない組み合わせだが、コンソールからは指定できてしまうため。
    /// </summary>
    private static void WarnOnConflictingGroups(List<FormatPreset> presets)
    {
        var conflicts = presets
            .Where(p => !string.IsNullOrWhiteSpace(p.Group))
            .GroupBy(p => p.Group, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1);

        foreach (var group in conflicts)
        {
            Console.WriteLine(
                $"警告: 排他グループ '{group.Key}' のプリセットを複数指定しています" +
                $"({string.Join(" / ", group.Select(p => p.Name))})。" +
                "指示が矛盾するため、パレットでは同時に選べない組み合わせです。");
            Console.WriteLine();
        }
    }

    private static string ResolveRelative(string baseDir, string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path);

    private static void PrintDiff(string original, string result)
    {
        var diff = TextDiff.Compute(original, result);

        Console.WriteLine($"原文  : {original}");
        Console.WriteLine($"整形後: {result}");
        Console.WriteLine();

        if (diff.IsTooLong)
        {
            Console.WriteLine($"テキストが長すぎるため差分を計算していません(上限 {TextDiff.MaxLength} 文字)。");
            return;
        }

        Console.WriteLine($"書き換え率: {diff.RewriteRatio:P1}");
        Console.WriteLine();

        foreach (var segment in diff.Segments)
        {
            var mark = segment.Kind switch
            {
                DiffKind.Removed => "-",
                DiffKind.Added => "+",
                _ => " ",
            };

            Console.WriteLine($"{mark} {segment.Text}");
        }
    }
}
