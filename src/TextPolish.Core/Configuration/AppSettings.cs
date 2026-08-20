namespace TextPolish.Core.Configuration;

/// <summary>appsettings.json のルート設定。</summary>
public sealed class AppSettings
{
    /// <summary>"AzureOpenAI" / "Gemini" / "OpenAiCompatible"。</summary>
    public string LlmProvider { get; set; } = "AzureOpenAI";

    /// <summary>
    /// LLM呼び出しのタイムアウト(秒)。ReasoningEffort を上げると応答が伸びるため調整できるようにしている。
    /// HttpClient の生成時にのみ反映されるため、変更後はアプリの再起動が必要。
    /// </summary>
    public int RequestTimeoutSeconds { get; set; } = 120;

    public AzureOpenAiSettings AzureOpenAI { get; set; } = new();

    public GeminiSettings Gemini { get; set; } = new();

    /// <summary>OpenAI互換エンドポイント(OpenRouter等)用の設定。</summary>
    public OpenAiCompatibleSettings OpenAiCompatible { get; set; } = new();

    /// <summary>整形の文体方針(アプリごとの切り替えを含む)。</summary>
    public FormattingSettings Formatting { get; set; } = new();

    /// <summary>テキスト挿入(クリップボード+Ctrl+V)の挙動。</summary>
    public TextInsertionSettings TextInsertion { get; set; } = new();

    /// <summary>選択テキストの整形パレット。</summary>
    public PolishSettings Polish { get; set; } = new();
}
