using System.Text.Json.Serialization;

namespace TextPolish.Core.Configuration;

public sealed class GeminiSettings : IApiKeySettings
{
    // TODO: Vertex AI経由かGoogle AI Studio経由か未確定(2026-08-08時点)。確定後にEndpoint/AuthModeの
    //       既定値と GeminiFormatter の認証ヘッダー組み立てロジックを見直すこと。
    //       Google AI Studio例: https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent
    //                           (APIキーは x-goog-api-key ヘッダーで渡す。URLには載せない)
    //       Vertex AI例      : https://{region}-aiplatform.googleapis.com/v1/projects/{project}/locations/{region}/publishers/google/models/{model}:generateContent (OAuth2/Bearer)
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models/TODO-model:generateContent";

    // TODO: APIキー(AI Studio)またはアクセストークン(Vertex AI)を設定する
    //       ファイルにはDPAPIで暗号化した ApiKeyProtected として保存する(平文で書かれていれば読み込み時に移行する)。
    public string ApiKey { get; set; } = "TODO-gemini-api-key";

    /// <summary>appsettings.json 上の暗号化済みAPIキー(<see cref="IApiKeySettings"/>参照)。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ApiKeyProtected { get; set; }

    [JsonIgnore]
    public bool ApiKeyUnreadable { get; set; }

    public string Model { get; set; } = "TODO-model";

    /// <summary>
    /// "ApiKeyQueryParam"(AI Studio想定) または "BearerToken"(Vertex AI想定)。
    ///
    /// **値名のとおりのURLクエリでは送らない。** "ApiKeyQueryParam" のときは
    /// <c>x-goog-api-key</c> ヘッダーで渡す(2026-11-20 に本物の鍵で確認)。
    /// URLは例外のメッセージやプロキシのアクセスログに残りうるため、鍵を混ぜない。
    /// 値名を変えないのは、既存の設定ファイルを黙って壊さないため。
    /// </summary>
    public string AuthMode { get; set; } = "ApiKeyQueryParam";
}
