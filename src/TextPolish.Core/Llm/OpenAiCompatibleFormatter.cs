using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using TextPolish.Core.Configuration;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Llm;

/// <summary>
/// OpenAI互換のChat Completions APIを呼び出す実装(SDK不使用)。
/// OpenRouter / OpenAI本家 / ローカルのOpenAI互換サーバーを想定。
/// Azure OpenAIはURL形式(deployments/api-version)と認証ヘッダー(api-key)が異なるため
/// <see cref="AzureOpenAiFormatter"/> を使うこと。
/// </summary>
public sealed class OpenAiCompatibleFormatter : ILlmFormatter
{
    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleSettings _settings;
    private readonly ISystemPromptProvider _systemPromptProvider;
    private readonly IAppLogger _logger;

    public OpenAiCompatibleFormatter(
        HttpClient httpClient,
        OpenAiCompatibleSettings settings,
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

        // temperature は指定しない。GPT-5系(推論モデル)は既定値以外を受け付けず、
        // 他のモデルも整形用途では既定値で問題ないため、送らないのが両対応になる。
        var body = new Dictionary<string, object?>
        {
            ["model"] = _settings.Model,
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        ChatCompletionOptions.ApplyOptional(
            body, _settings.ReasoningEffort, _settings.Verbosity, _settings.MaxCompletionTokens);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _settings.Endpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey);

        // OpenRouterのランキング表示用の任意ヘッダー。他サービスでは無視される。
        if (!string.IsNullOrWhiteSpace(_settings.SiteUrl))
        {
            httpRequest.Headers.Add("HTTP-Referer", _settings.SiteUrl);
        }

        if (!string.IsNullOrWhiteSpace(_settings.AppTitle))
        {
            httpRequest.Headers.Add("X-Title", _settings.AppTitle);
        }

        httpRequest.Content = JsonContent.Create(body);

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Error(
                    $"OpenAI互換API 呼び出しが失敗しました。Status={(int)response.StatusCode} Body={responseText}");
                throw new InvalidOperationException(
                    $"OpenAI互換API呼び出しに失敗しました(HTTP {(int)response.StatusCode})。");
            }

            using var doc = JsonDocument.Parse(responseText);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            // 原文へのフォールバックはしない。推論モデルは出力上限に達すると推論トークンだけを
            // 消費して content が空になることがあり、黙って原文を返すと「整形したのに
            // 変わっていない」テキストがそのまま置換されてしまうため。
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.Error($"OpenAI互換API の応答に整形結果が含まれていませんでした。Body={responseText}");
                throw new InvalidOperationException(
                    "整形結果が空でした。出力上限に達した可能性があります。" +
                    "MaxCompletionTokens を増やすか ReasoningEffort を下げてください。");
            }

            return content;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.Error("OpenAI互換API 呼び出し中に例外が発生しました。", ex);
            throw;
        }
    }
}
