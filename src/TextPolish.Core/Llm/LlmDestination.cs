using TextPolish.Core.Configuration;

namespace TextPolish.Core.Llm;

/// <summary>
/// 「いまどこへ送るのか」を人に見せる文字列にする。
///
/// 送信先のホストを許可リストで縛る案もあったが、運用が破綻しやすい
/// (OpenRouter・OpenAI・Azure・Gemini・自前のゲートウェイ・ローカルのサーバー。
/// どれも正当な送信先で、増える)。代わりに**見えるようにする**。
/// 利用者が書いた文章が外へ出ていくツールなので、どこへ出るかが隠れているほうが問題。
///
/// 出すのは**スキームとホストまで**(<see cref="EndpointPolicy.Describe"/>)。
/// パスにはモデル名や配置名が入り、クエリにはAPIキーが載りうる。
/// 画面に出した文字列はスクリーンショットに写り、ログは不具合報告に添付される。
/// </summary>
public static class LlmDestination
{
    /// <summary>
    /// 設定値(<see cref="AppSettings.LlmProvider"/>)を画面に出す呼び名へ直す。
    /// 設定画面のラジオボタンの文字列と揃えること(利用者が見比べるのはそこ)。
    /// </summary>
    public static string ProviderName(string? provider) => provider switch
    {
        "AzureOpenAI" => "Azure OpenAI",
        "Gemini" => "Gemini",
        "OpenAiCompatible" => "OpenAI互換",

        // 設定ファイルを手で書き換えると届く。整形しようとした時点で
        // LlmFormatterFactory が例外にするが、その前にログへ出る可能性がある。
        null or "" => "(プロバイダが未設定)",
        _ => $"(未知のプロバイダ: {provider})",
    };

    /// <summary>いま選ばれているプロバイダの呼び名と、設定されたエンドポイントの生の値。</summary>
    public static (string ProviderName, string Endpoint) Resolve(AppSettings settings)
    {
        var endpoint = settings.LlmProvider switch
        {
            "AzureOpenAI" => settings.AzureOpenAI.Endpoint,
            "Gemini" => settings.Gemini.Endpoint,
            "OpenAiCompatible" => settings.OpenAiCompatible.Endpoint,
            _ => string.Empty,
        };

        return (ProviderName(settings.LlmProvider), endpoint ?? string.Empty);
    }

    /// <summary>
    /// 「OpenAI互換(https://openrouter.ai)」の形にする。
    /// ログと画面の両方で同じ文字列を使う(見比べられるように)。
    /// </summary>
    public static string Describe(AppSettings settings)
    {
        var (name, endpoint) = Resolve(settings);
        return $"{name}({EndpointPolicy.Describe(endpoint)})";
    }
}
