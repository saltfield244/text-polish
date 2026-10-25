using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
                // 応答本文は整形対象の文章を含みうるため、ログへは残さない。
                // 障害調査に必要なHTTPステータスとエラー種別だけを LlmErrorSummary で要約する。
                _logger.Error(
                    $"OpenAI互換API 呼び出しが失敗しました。Status={(int)response.StatusCode} " +
                    LlmErrorSummary.Describe(responseText));
                throw new LlmRejectedException(
                    $"OpenAI互換API呼び出しに失敗しました(HTTP {(int)response.StatusCode})。");
            }

            // finish_reason を見て、途中で打ち切られた文章を採用しないようにする。
            // 原文へのフォールバックもしない。推論モデルは出力上限に達すると推論トークンだけを
            // 消費して content が空になることがあり、黙って原文を返すと「整形したのに
            // 変わっていない」テキストがそのまま置換されてしまうため。
            var parsed = LlmResponseReader.ReadChatCompletion(responseText);

            if (parsed.RejectionReason is { } rejection)
            {
                _logger.Error(
                    $"OpenAI互換API の応答を整形結果として採用できませんでした" +
                    $"(判定={parsed.Status} finish_reason={LogSanitizer.DescribeToken(parsed.FinishReason)})。" +
                    LlmErrorSummary.Describe(responseText));
                throw new LlmRejectedException(rejection);
            }

            if (parsed.Status == LlmFinishStatus.Unspecified)
            {
                // 終了理由を返さないOpenAI互換サーバーもあるため、異常扱いにはしない。
                _logger.Info("OpenAI互換API の応答に finish_reason がありませんでした。正常終了として扱います。");
            }

            return parsed.Text!;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 利用者が「中止」を押した場合。異常ではないので ERROR にしない。
            // 中止の記録は、どの操作を中止したか分かる呼び出し側が INFO で残す。
            throw;
        }
        catch (OperationCanceledException ex)
        {
            // ct が立っていない取り消しは HttpClient のタイムアウト。
            // 障害ではあるが利用者の操作で復帰できるため WARN に留める。
            _logger.Warn($"OpenAI互換API 呼び出しがタイムアウトしました。{LogSanitizer.DescribeException(ex)}");
            throw;
        }
        catch (Exception ex) when (ex is not LlmRejectedException)
        {
            // 例外もサーバーの応答を文中に含むことがあるため、種別と短いメッセージだけを残す。
            _logger.Error($"OpenAI互換API 呼び出し中に例外が発生しました。{LogSanitizer.DescribeException(ex)}");
            throw;
        }
    }
}
