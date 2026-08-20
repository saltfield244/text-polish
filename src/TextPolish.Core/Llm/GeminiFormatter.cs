using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TextPolish.Core.Configuration;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Llm;

/// <summary>
/// Gemini API 呼び出しの汎用実装。Vertex AI経由かGoogle AI Studio経由か未確定のため、
/// エンドポイントURL・認証情報はappsettings.jsonの値をそのまま使う。
/// </summary>
public sealed class GeminiFormatter : ILlmFormatter
{
    private readonly HttpClient _httpClient;
    private readonly GeminiSettings _settings;
    private readonly ISystemPromptProvider _systemPromptProvider;
    private readonly IAppLogger _logger;

    public GeminiFormatter(
        HttpClient httpClient,
        GeminiSettings settings,
        ISystemPromptProvider systemPromptProvider,
        IAppLogger logger)
    {
        _httpClient = httpClient;
        _settings = settings;
        _systemPromptProvider = systemPromptProvider;
        _logger = logger;
    }

    public async Task<string> FormatAsync(LlmFormatRequest request, CancellationToken ct = default)
    {
        var systemPrompt = await _systemPromptProvider.GetSystemPromptAsync(ct);
        var userPrompt = LlmPromptBuilder.BuildUserPrompt(request);

        // TODO: Vertex AI経由かGoogle AI Studio経由か未確定(2026-08-08時点)。確定したら
        //       AuthModeに応じたURL組み立て/認証方式や、Vertex AI特有のリクエスト形式差異を
        //       専用実装に置き換えること。現状は下記2パターンを設定切り替えのみでサポート。
        var url = _settings.AuthMode.Equals("ApiKeyQueryParam", StringComparison.OrdinalIgnoreCase)
            ? $"{_settings.Endpoint}?key={Uri.EscapeDataString(_settings.ApiKey)}"
            : _settings.Endpoint;

        var body = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = systemPrompt } },
            },
            contents = new object[]
            {
                new
                {
                    role = "user",
                    parts = new[] { new { text = userPrompt } },
                },
            },
            // temperature は指定しない。モデル側の既定値に任せる。
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        if (_settings.AuthMode.Equals("BearerToken", StringComparison.OrdinalIgnoreCase))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);
        }

        httpRequest.Content = JsonContent.Create(body);

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Error($"Gemini 呼び出しが失敗しました。Status={(int)response.StatusCode} Body={responseText}");
                throw new InvalidOperationException($"Gemini呼び出しに失敗しました(HTTP {(int)response.StatusCode})。");
            }

            using var doc = JsonDocument.Parse(responseText);
            var text = doc.RootElement
                .GetProperty("candidates")[0]
                .GetProperty("content")
                .GetProperty("parts")[0]
                .GetProperty("text")
                .GetString();

            // 原文へのフォールバックはしない(黙って未整形のまま置換されるのを避けるため)。
            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Error($"Gemini の応答に整形結果が含まれていませんでした。Body={responseText}");
                throw new InvalidOperationException("整形結果が空でした。");
            }

            return text;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.Error("Gemini 呼び出し中に例外が発生しました。", ex);
            throw;
        }
    }
}
