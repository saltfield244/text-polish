using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Configuration;

/// <summary>
/// appsettings.json の読み書き。Microsoft.Extensions.Configuration系のNuGetパッケージに依存せず、
/// System.Text.Json のみで完結させている(オフライン環境でのリストア不要)。
///
/// APIキーはDPAPIで暗号化して "ApiKeyProtected" に保存し、"ApiKey" は空にする
/// (<see cref="ApiKeyProtector"/>参照)。v1.0.1以前の平文の "ApiKey" は読み込み時に
/// 暗号化して保存し直す。ユーザーが "ApiKey" を直接書き換えた場合も、それを新しい値として扱う。
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

    public static AppSettings Load(string filePath, IAppLogger? logger = null)
    {
        if (!File.Exists(filePath))
        {
            var defaults = new AppSettings();
            Save(filePath, defaults);
            return defaults;
        }

        var settings = Deserialize(File.ReadAllText(filePath));
        var hasPlainTextKey = false;

        foreach (var (section, keySettings) in settings.ApiKeySettings())
        {
            // 平文のキーがあれば、暗号文より新しい値として扱う(暗号文は保存時に作り直される)。
            if (!ApiKeyProtector.IsPlaceholder(keySettings.ApiKey))
            {
                hasPlainTextKey = true;
                continue;
            }

            if (string.IsNullOrEmpty(keySettings.ApiKeyProtected))
            {
                continue;
            }

            if (ApiKeyProtector.TryUnprotect(keySettings.ApiKeyProtected, section, out var plainText))
            {
                keySettings.ApiKey = plainText;
            }
            else
            {
                // 暗号文は残しておく(別のユーザーで暗号化された場合など、元の環境なら読めるため)。
                keySettings.ApiKey = string.Empty;
                keySettings.ApiKeyUnreadable = true;
                logger?.Warn(
                    $"{section} のAPIキーを復号できませんでした。別のWindowsユーザーやPCで保存された可能性があります。" +
                    "設定画面で入力し直してください。");
            }
        }

        if (hasPlainTextKey)
        {
            try
            {
                Save(filePath, settings);
                logger?.Info("appsettings.json の平文のAPIキーを暗号化して保存し直しました。");
            }
            catch (Exception ex)
            {
                // Save は検証が済むまで元のファイルを置き換えないため、キーが失われることはない。
                // 平文のまま動作を続け、次回起動時に再度移行を試みる。
                logger?.Error("APIキーの暗号化保存に失敗しました。appsettings.json は平文のまま変更していません。", ex);
            }
        }

        return settings;
    }

    /// <summary>
    /// 設定を保存する。APIキーは暗号化して書き出す。
    ///
    /// 一時ファイルへ書き出し、読み戻してAPIキーを復号できることを確かめてから
    /// 元のファイルと置き換える。途中で失敗しても元のファイルはそのまま残る。
    /// </summary>
    public static void Save(string filePath, AppSettings settings)
    {
        var root = JsonSerializer.SerializeToNode(settings, JsonOptions)!.AsObject();
        var protectedValues = new Dictionary<string, string?>();

        foreach (var (section, keySettings) in settings.ApiKeySettings())
        {
            var node = root[section]!.AsObject();
            string? protectedValue;

            if (!ApiKeyProtector.IsPlaceholder(keySettings.ApiKey))
            {
                protectedValue = ApiKeyProtector.Protect(keySettings.ApiKey, section);
                node["ApiKey"] = string.Empty;
            }
            else if (keySettings.ApiKeyUnreadable)
            {
                // 復号できなかった暗号文は、新しいキーが入力されるか削除されるまで残す。
                protectedValue = keySettings.ApiKeyProtected;
                node["ApiKey"] = string.Empty;
            }
            else
            {
                // 未設定・テンプレートの "TODO-..." は秘密情報ではないのでそのまま書く。
                protectedValue = null;
                node["ApiKey"] = keySettings.ApiKey;
            }

            if (protectedValue is null)
            {
                node.Remove("ApiKeyProtected");
            }
            else
            {
                node["ApiKeyProtected"] = protectedValue;
            }

            protectedValues[section] = protectedValue;
        }

        var json = root.ToJsonString(JsonOptions);
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = filePath + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            VerifyWrittenKeys(tempPath, settings);

            // 同じボリューム内の置き換えなので、元のファイルは新しい内容に一度に切り替わる。
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        foreach (var (section, keySettings) in settings.ApiKeySettings())
        {
            keySettings.ApiKeyProtected = protectedValues[section];
        }
    }

    private static AppSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();

    /// <summary>書き出したファイルからAPIキーを復号し、保存しようとした値と一致するか確かめる。</summary>
    private static void VerifyWrittenKeys(string path, AppSettings expected)
    {
        var written = Deserialize(File.ReadAllText(path));
        var writtenBySection = written.ApiKeySettings().ToDictionary(x => x.Section, x => x.Settings);

        foreach (var (section, keySettings) in expected.ApiKeySettings())
        {
            if (ApiKeyProtector.IsPlaceholder(keySettings.ApiKey))
            {
                continue;
            }

            var actual = writtenBySection[section];
            if (!ApiKeyProtector.TryUnprotect(actual.ApiKeyProtected, section, out var plainText)
                || plainText != keySettings.ApiKey)
            {
                throw new InvalidDataException($"{section} のAPIキーを暗号化して書き出せませんでした。");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
