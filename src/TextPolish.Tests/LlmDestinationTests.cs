using TextPolish.Core.Configuration;
using TextPolish.Core.Llm;

namespace TextPolish.Tests;

/// <summary>
/// 送信先の表示(<see cref="LlmDestination"/>)。
///
/// 送信先のホストを許可リストで縛る案は捨てた。正当な送信先が増え続けて運用が破綻する
/// (OpenRouter・OpenAI・Azure・Gemini・自前のゲートウェイ・ローカルのサーバー)。
/// 代わりに**見えるようにする**方を選んだので、固定したいのは次の3つになる。
///
///   ・選んでいるプロバイダのエンドポイントを拾う(別のプロバイダの欄を見ていないこと)
///   ・**スキームとホストまでしか出さない**。パスにはモデル名や配置名が入り、
///     クエリにはAPIキーが載りうる。画面はスクリーンショットに写り、ログは不具合報告に付く
///   ・設定ファイルを手で書き換えて壊れた値になっても、例外にせず読める文字列を返す
///
/// 呼び名(「OpenAI互換」など)を1か所に寄せたのは、設定画面の警告文とログで
/// 違う名前が出ると、利用者が同じプロバイダの話だと分からないため。
/// ネットワークもデスクトップも使わないため <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class LlmDestinationTests
{
    public static IEnumerable<(string, Action)> All()
    {
        yield return ("送信先の表示: 選んでいるプロバイダのエンドポイントを出す", UsesSelectedProvider);
        yield return ("送信先の表示: パスとクエリは出さない(APIキーが載りうる)", PathAndQueryAreNotShown);
        yield return ("送信先の表示: 呼び名は設定画面と同じものを使う", ProviderNamesMatchTheSettingsWindow);
        yield return ("送信先の表示: 未設定・未知のプロバイダでも読める文字列を返す", BrokenSettingsStillDescribe);
        yield return ("送信先の表示: ループバックのスタブもそのまま出る", LoopbackIsShownAsIs);
    }

    private static AppSettings Settings(string provider)
    {
        var settings = new AppSettings { LlmProvider = provider };
        settings.AzureOpenAI.Endpoint = "https://my-resource.openai.azure.com/openai/deployments/gpt5";
        settings.Gemini.Endpoint =
            "https://generativelanguage.googleapis.com/v1beta/models/gemini:generateContent?key=AIzaSECRET";
        settings.OpenAiCompatible.Endpoint = "https://openrouter.ai/api/v1/chat/completions";
        return settings;
    }

    private static void UsesSelectedProvider()
    {
        // 別のプロバイダの欄を見ていると、画面には使っていない送信先が出る。
        Assert.Equal(
            "OpenAI互換(https://openrouter.ai)",
            LlmDestination.Describe(Settings("OpenAiCompatible")),
            "OpenAI互換を選んだときの表示");

        Assert.Equal(
            "Azure OpenAI(https://my-resource.openai.azure.com)",
            LlmDestination.Describe(Settings("AzureOpenAI")),
            "Azure を選んだときの表示");

        Assert.Equal(
            "Gemini(https://generativelanguage.googleapis.com)",
            LlmDestination.Describe(Settings("Gemini")),
            "Gemini を選んだときの表示");
    }

    private static void PathAndQueryAreNotShown()
    {
        // Gemini はクエリにAPIキーを載せる書き方が広く使われている。
        // ここが漏れると、画面のスクリーンショットとログの両方に鍵が出る。
        var shown = LlmDestination.Describe(Settings("Gemini"));

        Assert.True(!shown.Contains("AIza", StringComparison.Ordinal), $"APIキーが出ている: {shown}");
        Assert.True(!shown.Contains("key=", StringComparison.Ordinal), $"クエリが出ている: {shown}");
        Assert.True(!shown.Contains("v1beta", StringComparison.Ordinal), $"パスが出ている: {shown}");

        var azure = LlmDestination.Describe(Settings("AzureOpenAI"));
        Assert.True(!azure.Contains("deployments", StringComparison.Ordinal), $"配置名が出ている: {azure}");
    }

    private static void ProviderNamesMatchTheSettingsWindow()
    {
        Assert.Equal("Azure OpenAI", LlmDestination.ProviderName("AzureOpenAI"), "Azure の呼び名");
        Assert.Equal("Gemini", LlmDestination.ProviderName("Gemini"), "Gemini の呼び名");
        Assert.Equal("OpenAI互換", LlmDestination.ProviderName("OpenAiCompatible"), "OpenAI互換の呼び名");
    }

    private static void BrokenSettingsStillDescribe()
    {
        // appsettings.json を手で書き換えると届く。ここで落ちると起動できなくなる。
        var unknown = LlmDestination.Describe(Settings("OpenAi"));
        Assert.True(unknown.Contains("未知のプロバイダ", StringComparison.Ordinal), $"未知のプロバイダ: {unknown}");

        var empty = new AppSettings { LlmProvider = "OpenAiCompatible" };
        empty.OpenAiCompatible.Endpoint = string.Empty;
        Assert.Equal("OpenAI互換((未設定))", LlmDestination.Describe(empty), "エンドポイントが空のときの表示");

        var garbage = new AppSettings { LlmProvider = "OpenAiCompatible" };
        garbage.OpenAiCompatible.Endpoint = "これはURLではない";
        Assert.Equal(
            "OpenAI互換((URLとして読めません))",
            LlmDestination.Describe(garbage),
            "URLとして読めないときの表示");
    }

    private static void LoopbackIsShownAsIs()
    {
        // 実機確認のスタブと、ローカルのLLMを使う構成。
        // ポート番号は出ない(EndpointPolicy.Describe がホストまでしか返さないため)が、
        // 「localhost へ送っている」が分かれば用は足りる。
        var local = new AppSettings { LlmProvider = "OpenAiCompatible" };
        local.OpenAiCompatible.Endpoint = "http://127.0.0.1:51234/v1/chat/completions";

        Assert.Equal("OpenAI互換(http://127.0.0.1)", LlmDestination.Describe(local), "ローカルのサーバーの表示");
    }
}
