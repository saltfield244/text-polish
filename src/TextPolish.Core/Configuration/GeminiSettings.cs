namespace TextPolish.Core.Configuration;

public sealed class GeminiSettings
{
    // TODO: Vertex AI経由かGoogle AI Studio経由か未確定(2026-08-08時点)。確定後にEndpoint/AuthModeの
    //       既定値と GeminiFormatter の認証ヘッダー組み立てロジックを見直すこと。
    //       Google AI Studio例: https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key=API_KEY
    //       Vertex AI例      : https://{region}-aiplatform.googleapis.com/v1/projects/{project}/locations/{region}/publishers/google/models/{model}:generateContent (OAuth2/Bearer)
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta/models/TODO-model:generateContent";

    // TODO: APIキー(AI Studio)またはアクセストークン(Vertex AI)を設定する
    public string ApiKey { get; set; } = "TODO-gemini-api-key";

    public string Model { get; set; } = "TODO-model";

    /// <summary>"ApiKeyQueryParam"(AI Studio想定) または "BearerToken"(Vertex AI想定)。</summary>
    public string AuthMode { get; set; } = "ApiKeyQueryParam";
}
