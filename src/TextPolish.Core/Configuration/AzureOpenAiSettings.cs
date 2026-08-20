namespace TextPolish.Core.Configuration;

public sealed class AzureOpenAiSettings
{
    // TODO: 実リソースのエンドポイントに置き換える (例: https://{resource}.openai.azure.com)
    public string Endpoint { get; set; } = "https://TODO-your-resource.openai.azure.com";

    // TODO: 平文でのAPIキー管理は開発用の暫定対応。本番運用ではユーザーシークレット/環境変数/
    //       Windows資格情報マネージャー等への移行を検討する。
    public string ApiKey { get; set; } = "TODO-azure-openai-api-key";

    // TODO: デプロイ名を設定する
    public string Deployment { get; set; } = "TODO-deployment-name";

    public string ApiVersion { get; set; } = "2024-06-01";

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
