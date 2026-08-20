using System.IO;
using System.Text.Json;

namespace TextPolish.Core.Configuration;

/// <summary>
/// appsettings.json の読み書き。Microsoft.Extensions.Configuration系のNuGetパッケージに依存せず、
/// System.Text.Json のみで完結させている(オフライン環境でのリストア不要)。
/// </summary>
public static class AppSettingsLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // 配布用appsettings.jsonにTODOコメントを埋め込めるよう、読み込み時は "//" コメントと
        // 末尾カンマを許容する(書き込み時はコメントなしのプレーンJSONで上書きされる)。
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AppSettings Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            var defaults = new AppSettings();
            Save(filePath, defaults);
            return defaults;
        }

        var json = File.ReadAllText(filePath);
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    public static void Save(string filePath, AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(filePath, json);
    }
}
