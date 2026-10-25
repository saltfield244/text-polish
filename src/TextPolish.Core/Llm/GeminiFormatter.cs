using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
                // 応答本文は整形対象の文章を含みうるため、ログへは残さない。
                // 障害調査に必要なHTTPステータスとエラー種別だけを LlmErrorSummary で要約する。
                _logger.Error(
                    $"Gemini 呼び出しが失敗しました。Status={(int)response.StatusCode} " +
                    LlmErrorSummary.Describe(responseText));
                throw new LlmRejectedException($"Gemini呼び出しに失敗しました(HTTP {(int)response.StatusCode})。");
            }

            // finishReason を見て、MAX_TOKENS 等で打ち切られた文章を採用しないようにする。
            // 原文へのフォールバックもしない(黙って未整形のまま置換されるのを避けるため)。
            var parsed = LlmResponseReader.ReadGemini(responseText);

            if (parsed.RejectionReason is { } rejection)
            {
                _logger.Error(
                    $"Gemini の応答を整形結果として採用できませんでした" +
                    $"(判定={parsed.Status} finishReason={LogSanitizer.DescribeToken(parsed.FinishReason)})。" +
                    LlmErrorSummary.Describe(responseText));
                throw new LlmRejectedException(rejection);
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
            _logger.Warn($"Gemini 呼び出しがタイムアウトしました。{LogSanitizer.DescribeException(ex)}");
            throw;
        }
        catch (Exception ex) when (ex is not LlmRejectedException)
        {
            // 例外にはURLが載ることがあり、AI Studio経路ではクエリにAPIキーが入る。
            // 種別と短いメッセージだけを残し、キーは LogSanitizer で落とす。
            _logger.Error($"Gemini 呼び出し中に例外が発生しました。{LogSanitizer.DescribeException(ex)}");
            throw;
        }
    }
}
