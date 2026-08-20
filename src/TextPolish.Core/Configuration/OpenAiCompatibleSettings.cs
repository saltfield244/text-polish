namespace TextPolish.Core.Configuration;

/// <summary>
/// OpenAI互換のChat Completions APIを提供するサービス向けの設定。
/// OpenRouter / OpenAI本家 / ローカルのOpenAI互換サーバー(Ollama, LM Studio等)を想定。
/// Azure OpenAIはURL形式と認証方式が異なるため <see cref="AzureOpenAiSettings"/> を使うこと。
/// </summary>
public sealed class OpenAiCompatibleSettings
{
    /// <summary>Chat Completions のフルURL。</summary>
    public string Endpoint { get; set; } = "https://openrouter.ai/api/v1/chat/completions";

    // TODO: 平文でのAPIキー管理は開発用の暫定対応。本番運用では環境変数/Windows資格情報マネージャー等への
    //       移行を検討する。
    public string ApiKey { get; set; } = "TODO-api-key";

    /// <summary>リクエストボディの model に設定する値(例: "openai/gpt-4o-mini")。</summary>
    public string Model { get; set; } = "TODO-model";

    /// <summary>
    /// OpenRouter がランキング表示に使う任意ヘッダー(HTTP-Referer / X-Title)。
    /// 空の場合は送信しない。OpenRouter以外では不要。
    /// </summary>
    public string? SiteUrl { get; set; }

    public string? AppTitle { get; set; } = "TextPolish";

    /// <summary>
    /// GPT-5系(推論モデル)の reasoning_effort。"minimal" / "low" / "medium" / "high"。
    /// null なら送信しない(モデル側の既定値に任せる)。
    /// </summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// GPT-5系の verbosity。"low" / "medium" / "high"。null なら送信しない。
    /// 本ツールの出力は整形後テキストのみのため "low" が適する。
    /// </summary>
    public string? Verbosity { get; set; }

    /// <summary>
    /// 出力トークンの上限(max_completion_tokens)。null なら送信しない。
    /// 推論モデルは推論トークンもここから消費するため、応答が空になる場合は増やす。
    /// </summary>
    public int? MaxCompletionTokens { get; set; }
}
