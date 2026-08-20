using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using TextPolish.Core.Configuration;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Llm;

/// <summary>Azure OpenAI Chat Completions REST APIを直接呼び出す実装(SDK不使用)。</summary>
public sealed class AzureOpenAiFormatter : ILlmFormatter
{
    private readonly HttpClient _httpClient;
    private readonly AzureOpenAiSettings _settings;
    private readonly ISystemPromptProvider _systemPromptProvider;
    private readonly IAppLogger _logger;

    public AzureOpenAiFormatter(
        HttpClient httpClient,
        AzureOpenAiSettings settings,
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

        var url = $"{_settings.Endpoint.TrimEnd('/')}/openai/deployments/{_settings.Deployment}" +
                  $"/chat/completions?api-version={_settings.ApiVersion}";

        // temperature は指定しない。GPT-5系(推論モデル)は既定値以外を受け付けず、
        // 他のモデルも整形用途では既定値で問題ないため、送らないのが両対応になる。
        var body = new Dictionary<string, object?>
        {
            ["messages"] = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        ChatCompletionOptions.ApplyOptional(
            body, _settings.ReasoningEffort, _settings.Verbosity, _settings.MaxCompletionTokens);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url);
        httpRequest.Headers.Add("api-key", _settings.ApiKey);
        httpRequest.Content = JsonContent.Create(body);

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, ct);
            var responseText = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.Error($"Azure OpenAI 呼び出しが失敗しました。Status={(int)response.StatusCode} Body={responseText}");
                throw new InvalidOperationException($"Azure OpenAI呼び出しに失敗しました(HTTP {(int)response.StatusCode})。");
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
                _logger.Error($"Azure OpenAI の応答に整形結果が含まれていませんでした。Body={responseText}");
                throw new InvalidOperationException(
                    "整形結果が空でした。出力上限に達した可能性があります。" +
                    "MaxCompletionTokens を増やすか ReasoningEffort を下げてください。");
            }

            return content;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.Error("Azure OpenAI 呼び出し中に例外が発生しました。", ex);
            throw;
        }
    }
}
