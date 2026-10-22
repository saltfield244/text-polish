using System.IO;
using System.Text.Json.Nodes;
using TextPolish.Core.Configuration;

namespace TextPolish.Tests;

/// <summary>APIキーの暗号化保存と、v1.0.1以前の平文設定からの移行。</summary>
internal static class SettingsTests
{
    private const string AzureKey = "azure-secret-0123456789";
    private const string GeminiKey = "AIza-gemini-secret-ABCDEFG";
    private const string OpenAiKey = "sk-or-v1-openai-secret-xyz";

    /// <summary>v1.0.1の配布テンプレートと同じ書き方(コメント・末尾カンマあり)の平文設定。</summary>
    private const string LegacyJson = $$"""
        {
          // 旧バージョンの設定
          "LlmProvider": "OpenAiCompatible",
          "OpenAiCompatible": {
            "Endpoint": "https://openrouter.ai/api/v1/chat/completions",
            "ApiKey": "{{OpenAiKey}}",
            "Model": "openai/gpt-4o-mini",
          },
          "AzureOpenAI": { "ApiKey": "{{AzureKey}}", "Deployment": "my-deploy" },
          "Gemini": { "ApiKey": "{{GeminiKey}}", "AuthMode": "BearerToken" },
          "Polish": { "Hotkey": "Ctrl+Shift+W", "QuickHotkey": "" },
          "TextInsertion": { "RestoreDelayMs": 1200 }
        }
        """;

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("設定: 平文のAPIキーを3プロバイダとも暗号化して移行する", LegacyPlainTextIsMigrated);
        yield return ("設定: 移行してもAPIキー以外の設定は変わらない", MigrationKeepsOtherSettings);
        yield return ("設定: テンプレートの TODO- 値は暗号化しない", PlaceholdersStayPlain);
        yield return ("設定: ファイルを直接編集した平文キーが暗号文より優先される", PlainTextOverridesProtected);
        yield return ("設定: 設定画面からの更新", UpdateFromSettingsWindow);
        yield return ("設定: 設定画面からの削除", DeleteFromSettingsWindow);
        yield return ("設定: 復号できない暗号文は保存しても消えない", UnreadableKeyIsPreserved);
        yield return ("設定: 別プロバイダの暗号文を書き写しても復号できない", ProtectedValueIsBoundToProvider);
        yield return ("設定: 保存に失敗しても元のファイルとキーは失われない", FailedSaveKeepsOriginal);
        yield return ("設定: 長いトークン・非ASCIIのキーも往復できる", LongAndNonAsciiKeysRoundTrip);
    }

    private static void LegacyPlainTextIsMigrated()
    {
        using var dir = new TempDir();
        var path = dir.Write(LegacyJson);

        var settings = AppSettingsLoader.Load(path, new NullLogger());

        Assert.Equal(AzureKey, settings.AzureOpenAI.ApiKey, "メモリ上のAzureキー");
        Assert.Equal(GeminiKey, settings.Gemini.ApiKey, "メモリ上のGeminiキー");
        Assert.Equal(OpenAiKey, settings.OpenAiCompatible.ApiKey, "メモリ上のOpenAI互換キー");

        AssertNoPlainTextSecrets(path, AzureKey, GeminiKey, OpenAiKey);
        var root = ReadJson(path);
        foreach (var section in new[] { "AzureOpenAI", "Gemini", "OpenAiCompatible" })
        {
            Assert.True(
                root[section]!["ApiKeyProtected"]!.GetValue<string>().StartsWith("dpapi:v1:"),
                $"{section} が暗号化されていない");
            Assert.Equal("", root[section]!["ApiKey"]!.GetValue<string>(), $"{section}.ApiKey");
        }

        Assert.True(!File.Exists(path + ".tmp"), "一時ファイルが残っている");

        var reloaded = AppSettingsLoader.Load(path, new NullLogger());
        Assert.Equal(AzureKey, reloaded.AzureOpenAI.ApiKey, "再読み込み後のAzureキー");
        Assert.Equal(GeminiKey, reloaded.Gemini.ApiKey, "再読み込み後のGeminiキー");
        Assert.Equal(OpenAiKey, reloaded.OpenAiCompatible.ApiKey, "再読み込み後のOpenAI互換キー");
    }

    private static void MigrationKeepsOtherSettings()
    {
        using var dir = new TempDir();
        var path = dir.Write(LegacyJson);

        AppSettingsLoader.Load(path, new NullLogger());
        var reloaded = AppSettingsLoader.Load(path, new NullLogger());

        Assert.Equal("OpenAiCompatible", reloaded.LlmProvider, "LlmProvider");
        Assert.Equal("openai/gpt-4o-mini", reloaded.OpenAiCompatible.Model, "Model");
        Assert.Equal("my-deploy", reloaded.AzureOpenAI.Deployment, "Deployment");
        Assert.Equal("BearerToken", reloaded.Gemini.AuthMode, "AuthMode");
        Assert.Equal("Ctrl+Shift+W", reloaded.Polish.Hotkey, "Hotkey");
        Assert.Equal(1200, reloaded.TextInsertion.RestoreDelayMs, "RestoreDelayMs");
    }

    private static void PlaceholdersStayPlain()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "appsettings.json");

        // ファイルがなければ既定値(TODO-...)で作られる。
        var settings = AppSettingsLoader.Load(path, new NullLogger());
        var original = File.ReadAllText(path);

        Assert.True(!original.Contains("ApiKeyProtected"), "プレースホルダーが暗号化された");
        Assert.True(original.Contains("TODO-api-key"), "プレースホルダーが消えた");

        // プレースホルダーしかないファイルは、読み込んでも書き換えない(配布用のコメントを消さないため)。
        AppSettingsLoader.Load(path, new NullLogger());
        Assert.Equal(original, File.ReadAllText(path), "読み込みでファイルが書き換わった");
        Assert.Equal("TODO-api-key", settings.OpenAiCompatible.ApiKey, "メモリ上のプレースホルダー");
    }

    private static void PlainTextOverridesProtected()
    {
        using var dir = new TempDir();
        var path = dir.Write(LegacyJson);
        AppSettingsLoader.Load(path, new NullLogger());

        // 暗号文を残したまま、ユーザーがファイルの ApiKey に新しいキーを直接書いた状態。
        var root = ReadJson(path);
        root["OpenAiCompatible"]!["ApiKey"] = "sk-edited-by-hand";
        File.WriteAllText(path, root.ToJsonString());

        var settings = AppSettingsLoader.Load(path, new NullLogger());

        Assert.Equal("sk-edited-by-hand", settings.OpenAiCompatible.ApiKey, "直接編集したキー");
        Assert.Equal(AzureKey, settings.AzureOpenAI.ApiKey, "他のプロバイダのキー");
        AssertNoPlainTextSecrets(path, "sk-edited-by-hand");
        Assert.Equal(
            "sk-edited-by-hand",
            AppSettingsLoader.Load(path, new NullLogger()).OpenAiCompatible.ApiKey,
            "再読み込み後のキー");
    }

    private static void UpdateFromSettingsWindow()
    {
        using var dir = new TempDir();
        var path = dir.Write(LegacyJson);
        var settings = AppSettingsLoader.Load(path, new NullLogger());

        ApiKeyProtector.ApplyInput(settings.Gemini, "new-gemini-key", deleteRequested: false);
        AppSettingsLoader.Save(path, settings);

        AssertNoPlainTextSecrets(path, "new-gemini-key");
        Assert.Equal("new-gemini-key", AppSettingsLoader.Load(path).Gemini.ApiKey, "更新後のキー");
    }

    private static void DeleteFromSettingsWindow()
    {
        using var dir = new TempDir();
        var path = dir.Write(LegacyJson);
        var settings = AppSettingsLoader.Load(path, new NullLogger());

        // 入力欄を空にして保存 = 削除(従来どおりの挙動)。
        ApiKeyProtector.ApplyInput(settings.AzureOpenAI, "", deleteRequested: false);
        AppSettingsLoader.Save(path, settings);

        var root = ReadJson(path);
        Assert.True(root["AzureOpenAI"]!["ApiKeyProtected"] is null, "削除したキーの暗号文が残っている");
        Assert.Equal("", AppSettingsLoader.Load(path).AzureOpenAI.ApiKey, "削除後のキー");
        Assert.Equal(GeminiKey, AppSettingsLoader.Load(path).Gemini.ApiKey, "他のプロバイダのキー");
    }

    private static void UnreadableKeyIsPreserved()
    {
        using var dir = new TempDir();
        // 別のユーザーで暗号化された値の代わりに、復号できない値を置く。
        const string foreign = "dpapi:v1:AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA";
        var path = dir.Write($$"""{ "Gemini": { "ApiKey": "", "ApiKeyProtected": "{{foreign}}" } }""");

        var logger = new NullLogger();
        var settings = AppSettingsLoader.Load(path, logger);

        Assert.True(settings.Gemini.ApiKeyUnreadable, "復号失敗が検出されていない");
        Assert.Equal("", settings.Gemini.ApiKey, "復号失敗時のキー");
        Assert.True(logger.Warnings.Any(w => w.Contains("Gemini")), "復号失敗の警告がない");

        // 入力欄が空のまま保存しても暗号文は残る。
        ApiKeyProtector.ApplyInput(settings.Gemini, "", deleteRequested: false);
        AppSettingsLoader.Save(path, settings);
        Assert.Equal(foreign, ReadJson(path)["Gemini"]!["ApiKeyProtected"]!.GetValue<string>(), "暗号文が消えた");

        // 「削除」を押して保存すると消える。
        settings = AppSettingsLoader.Load(path, new NullLogger());
        ApiKeyProtector.ApplyInput(settings.Gemini, "", deleteRequested: true);
        AppSettingsLoader.Save(path, settings);
        Assert.True(ReadJson(path)["Gemini"]!["ApiKeyProtected"] is null, "削除しても暗号文が残っている");
        Assert.True(!AppSettingsLoader.Load(path).Gemini.ApiKeyUnreadable, "削除後も復号失敗のまま");
    }

    private static void ProtectedValueIsBoundToProvider()
    {
        var azureProtected = ApiKeyProtector.Protect(AzureKey, "AzureOpenAI");

        Assert.True(ApiKeyProtector.TryUnprotect(azureProtected, "AzureOpenAI", out var plain), "本来のプロバイダで復号できない");
        Assert.Equal(AzureKey, plain, "復号結果");
        Assert.True(!ApiKeyProtector.TryUnprotect(azureProtected, "Gemini", out _), "別のプロバイダで復号できてしまう");
        Assert.True(!ApiKeyProtector.TryUnprotect("not-protected", "Gemini", out _), "接頭辞のない値を復号した");
        Assert.True(!ApiKeyProtector.TryUnprotect("dpapi:v1:%%%", "Gemini", out _), "壊れたBase64で例外が出た");
    }

    private static void FailedSaveKeepsOriginal()
    {
        using var dir = new TempDir();
        var path = dir.Write(LegacyJson);
        var before = File.ReadAllBytes(path);

        // 書き込みできない状態で移行を試みる。
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var logger = new NullLogger();
            var settings = AppSettingsLoader.Load(path, logger);

            Assert.Equal(OpenAiKey, settings.OpenAiCompatible.ApiKey, "移行失敗時もメモリ上のキーは使える");
            Assert.True(before.SequenceEqual(File.ReadAllBytes(path)), "元のファイルが変更された");
            Assert.True(!File.Exists(path + ".tmp"), "一時ファイルが残っている");
            Assert.True(logger.Warnings.Count > 0, "移行失敗がログに出ていない");
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        // 書き込めるようになれば、次の読み込みで移行される。
        AppSettingsLoader.Load(path, new NullLogger());
        AssertNoPlainTextSecrets(path, AzureKey, GeminiKey, OpenAiKey);
    }

    private static void LongAndNonAsciiKeysRoundTrip()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "appsettings.json");

        // 資格情報マネージャーの上限(2560バイト)を超える長さのアクセストークンを想定。
        var longToken = "ya29." + new string('x', 5000);
        var settings = new AppSettings();
        settings.Gemini.ApiKey = longToken;
        settings.AzureOpenAI.ApiKey = "日本語を含むキー🔑";
        AppSettingsLoader.Save(path, settings);

        var reloaded = AppSettingsLoader.Load(path);
        Assert.Equal(longToken, reloaded.Gemini.ApiKey, "長いトークン");
        Assert.Equal("日本語を含むキー🔑", reloaded.AzureOpenAI.ApiKey, "非ASCIIのキー");
    }

    private static void AssertNoPlainTextSecrets(string path, params string[] secrets)
    {
        var text = File.ReadAllText(path);
        foreach (var secret in secrets)
        {
            Assert.True(!text.Contains(secret), $"平文のキーがファイルに残っている: {secret[..4]}...");
        }
    }

    private static JsonNode ReadJson(string path) => JsonNode.Parse(File.ReadAllText(path))!;

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TextPolish.Tests." + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Write(string json)
        {
            var file = System.IO.Path.Combine(Path, "appsettings.json");
            File.WriteAllText(file, json);
            return file;
        }

        public void Dispose()
        {
            foreach (var f in Directory.GetFiles(Path))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }

            Directory.Delete(Path, recursive: true);
        }
    }
}
