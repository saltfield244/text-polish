using System.IO;
using System.Net;
using System.Net.Http;
using TextPolish.Core.Configuration;
using TextPolish.Core.Llm;
using TextPolish.Core.Logging;

namespace TextPolish.Tests;

/// <summary>
/// LLM呼び出しが失敗したときのログに、利用者の文章やAPIキーが残らないこと。
///
/// ログは <c>%AppData%\TextPolish\logs\app-YYYYMMDD.log</c> に平文で残り、不具合報告に
/// 添付されることもある。以前は応答本文を全文 <c>Body=</c> として書いていたため、整形を
/// 依頼した文章が失敗時にログへ保存されていた。ここでは機密文を含む模擬応答を3プロバイダに
/// 渡し、「診断情報は残るが機密文面は残らない」ことを固定する。
/// ネットワークもデスクトップも使わないため <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class LlmErrorLoggingTests
{
    /// <summary>応答本文に混ぜる、利用者が整形を依頼した文章に相当する文。</summary>
    private const string Secret = "社外秘の議事録です。来期の人員削減はPROJECT-KAMIFUSENとして進めます。";

    private const string OpenAiKey = "sk-proj-SECRETKEY1234567890abcdef";
    private const string AzureKey = "azure-SECRETKEY1234567890abcdef";
    private const string GeminiKey = "AIzaSySECRETKEY1234567890abcdef";

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("エラーログ: OpenAI互換のHTTPエラーで本文を残さない", OpenAiHttpErrorKeepsNoBody);
        yield return ("エラーログ: OpenAI互換の途中終了で本文を残さない", OpenAiTruncatedKeepsNoBody);
        yield return ("エラーログ: OpenAI互換の不正JSONで本文を残さない", OpenAiMalformedKeepsNoBody);
        yield return ("エラーログ: OpenAI互換の通信例外でAPIキーを残さない", OpenAiTransportExceptionKeepsNoKey);
        yield return ("エラーログ: AzureのHTTPエラーで本文を残さない", AzureHttpErrorKeepsNoBody);
        yield return ("エラーログ: Azureの途中終了で本文を残さない", AzureTruncatedKeepsNoBody);
        yield return ("エラーログ: Azureの不正JSONで本文を残さない", AzureMalformedKeepsNoBody);
        yield return ("エラーログ: Azureの通信例外でAPIキーを残さない", AzureTransportExceptionKeepsNoKey);
        yield return ("エラーログ: GeminiのHTTPエラーで本文を残さない", GeminiHttpErrorKeepsNoBody);
        yield return ("エラーログ: Geminiの途中終了で本文を残さない", GeminiTruncatedKeepsNoBody);
        yield return ("エラーログ: Geminiの不正JSONで本文を残さない", GeminiMalformedKeepsNoBody);
        yield return ("エラーログ: GeminiのURLクエリのAPIキーを残さない", GeminiTransportExceptionKeepsNoKey);
        yield return ("エラーログ: 要約はerror.messageを残さない", ErrorSummaryKeepsNoMessage);
        yield return ("エラーログ: 要約はJSONでない本文の中身を残さない", ErrorSummaryKeepsNoNonJsonBody);
        yield return ("エラーログ: 想定外に長い終了理由は残さない", LongFinishReasonIsNotLogged);
        yield return ("エラーログ: APIキーらしい値はログ行から落ちる", SanitizerRedactsKeys);
        yield return ("エラーログ: 例外は種別と短いメッセージだけに要約する", ExceptionIsSummarized);
        yield return ("エラーログ: ログファイルにAPIキーが書かれない", FileLoggerRedactsKeys);
    }

    // ---- OpenAI互換 ----

    private static void OpenAiHttpErrorKeepsNoBody()
    {
        var log = RunOpenAi(_ => Json(HttpStatusCode.Unauthorized, ErrorBody("invalid_request_error", "\"invalid_api_key\"")));

        AssertNoSecrets(log, OpenAiKey);
        AssertContains(log, "Status=401");
        AssertContains(log, "ErrorType=invalid_request_error");
        AssertContains(log, "ErrorCode=invalid_api_key");
        AssertContains(log, "BodyChars=");
    }

    private static void OpenAiTruncatedKeepsNoBody()
    {
        var log = RunOpenAi(_ => Json(HttpStatusCode.OK, ChatCompletion(Secret, "length")));

        AssertNoSecrets(log, OpenAiKey);
        AssertContains(log, "finish_reason=length");
        AssertContains(log, "判定=Truncated");
    }

    private static void OpenAiMalformedKeepsNoBody()
    {
        var log = RunOpenAi(_ => Json(HttpStatusCode.OK, "{\"choices\": [ この先は壊れている " + Secret));

        AssertNoSecrets(log, OpenAiKey);
        AssertContains(log, "判定=Unreadable");
        AssertContains(log, "BodyShape=JSONではない");
    }

    private static void OpenAiTransportExceptionKeepsNoKey()
    {
        var log = RunOpenAi(request => throw new HttpRequestException(
            $"接続できませんでした: {request.RequestUri} Authorization: Bearer {OpenAiKey}"));

        AssertNoSecrets(log, OpenAiKey);
        AssertContains(log, nameof(HttpRequestException));
        AssertContains(log, LogSanitizer.Redacted);
    }

    // ---- Azure OpenAI ----

    private static void AzureHttpErrorKeepsNoBody()
    {
        var log = RunAzure(_ => Json(HttpStatusCode.InternalServerError, ErrorBody("server_error", "500")));

        AssertNoSecrets(log, AzureKey);
        AssertContains(log, "Status=500");
        AssertContains(log, "ErrorType=server_error");
    }

    private static void AzureTruncatedKeepsNoBody()
    {
        var log = RunAzure(_ => Json(HttpStatusCode.OK, ChatCompletion(Secret, "length")));

        AssertNoSecrets(log, AzureKey);
        AssertContains(log, "finish_reason=length");
        AssertContains(log, "判定=Truncated");
    }

    private static void AzureMalformedKeepsNoBody()
    {
        var log = RunAzure(_ => Json(HttpStatusCode.OK, "<html><body>" + Secret + "</body></html>"));

        AssertNoSecrets(log, AzureKey);
        AssertContains(log, "判定=Unreadable");
        AssertContains(log, "BodyShape=JSONではない");
    }

    private static void AzureTransportExceptionKeepsNoKey()
    {
        var log = RunAzure(request => throw new HttpRequestException(
            $"接続できませんでした: {request.RequestUri} api-key: {AzureKey}"));

        AssertNoSecrets(log, AzureKey);
        AssertContains(log, nameof(HttpRequestException));
    }

    // ---- Gemini ----

    private static void GeminiHttpErrorKeepsNoBody()
    {
        var body = "{\"error\":{\"code\":403,\"message\":\"" + Secret + "\",\"status\":\"PERMISSION_DENIED\"}}";
        var log = RunGemini(_ => Json(HttpStatusCode.Forbidden, body));

        AssertNoSecrets(log, GeminiKey);
        AssertContains(log, "Status=403");
        AssertContains(log, "ErrorCode=403");
        AssertContains(log, "ErrorStatus=PERMISSION_DENIED");
    }

    private static void GeminiTruncatedKeepsNoBody()
    {
        var log = RunGemini(_ => Json(HttpStatusCode.OK, GeminiResponse(Secret, "MAX_TOKENS")));

        AssertNoSecrets(log, GeminiKey);
        AssertContains(log, "finishReason=MAX_TOKENS");
        AssertContains(log, "判定=Truncated");
    }

    private static void GeminiMalformedKeepsNoBody()
    {
        var log = RunGemini(_ => Json(HttpStatusCode.OK, "{\"candidates\": [ 壊れている " + Secret));

        AssertNoSecrets(log, GeminiKey);
        AssertContains(log, "判定=Unreadable");
    }

    private static void GeminiTransportExceptionKeepsNoKey()
    {
        // AI Studio 経路は ?key=... でAPIキーを渡すため、URLを含む例外メッセージが危ない。
        Uri? requested = null;
        var log = RunGemini(request =>
        {
            requested = request.RequestUri;
            throw new HttpRequestException($"名前解決に失敗しました: {request.RequestUri}");
        });

        Assert.True(
            requested is not null && requested.Query.Contains(GeminiKey),
            $"前提: URLのクエリにAPIキーが入っている ({requested?.Query})");

        AssertNoSecrets(log, GeminiKey);
        AssertContains(log, nameof(HttpRequestException));
        AssertContains(log, LogSanitizer.Redacted);
    }

    // ---- 要約・秘匿処理そのもの ----

    private static void ErrorSummaryKeepsNoMessage()
    {
        var summary = LlmErrorSummary.Describe(ErrorBody("invalid_request_error", "\"too_long\""));

        Assert.True(!summary.Contains(Secret), $"error.message を残さない: {summary}");
        AssertContains(summary, "ErrorType=invalid_request_error");
        AssertContains(summary, "ErrorCode=too_long");
    }

    private static void ErrorSummaryKeepsNoNonJsonBody()
    {
        var summary = LlmErrorSummary.Describe("502 Bad Gateway\n" + Secret);

        Assert.True(!summary.Contains(Secret), $"本文の中身を残さない: {summary}");
        AssertContains(summary, "BodyShape=JSONではない");

        // 長さだけは残す(応答が空だったのか本文付きだったのかで切り分けられるようにする)。
        AssertContains(summary, "BodyChars=");
    }

    private static void LongFinishReasonIsNotLogged()
    {
        // 終了理由は本来 "stop" のような短い定数だが、値を決めるのはサーバー側。
        // 文章が入っていた場合にそのまま流さないことを確かめる。
        var reason = LogSanitizer.DescribeToken(Secret);

        Assert.Equal(LogSanitizer.Unexpected, reason, "長い終了理由の扱い");
        Assert.Equal("stop", LogSanitizer.DescribeToken("stop"), "通常の終了理由はそのまま残す");
        Assert.Equal("(なし)", LogSanitizer.DescribeToken(null), "終了理由がない場合");
    }

    private static void SanitizerRedactsKeys()
    {
        var cases = new[]
        {
            $"https://example.com/v1/models/x:generateContent?key={GeminiKey}",
            $"Authorization: Bearer {OpenAiKey}",
            $"api-key: {AzureKey}",
            $"どこかの文中に {OpenAiKey} が混ざった",
            $"Googleのキー {GeminiKey} が混ざった",
        };

        foreach (var text in cases)
        {
            var redacted = LogSanitizer.Redact(text);

            Assert.True(!redacted.Contains(OpenAiKey), $"OpenAIのキーが残る: {redacted}");
            Assert.True(!redacted.Contains(AzureKey), $"Azureのキーが残る: {redacted}");
            Assert.True(!redacted.Contains(GeminiKey), $"Geminiのキーが残る: {redacted}");
            Assert.True(redacted.Contains(LogSanitizer.Redacted), $"落とした目印がない: {redacted}");
        }

        // 普通の文は変えない(ログが読めなくならないこと)。
        const string plain = "即実行の書き戻しに失敗しました(TargetChanged)。";
        Assert.Equal(plain, LogSanitizer.Redact(plain), "通常のログ行");
    }

    private static void ExceptionIsSummarized()
    {
        var inner = new HttpRequestException($"接続拒否 ?key={GeminiKey}");
        var described = LogSanitizer.DescribeException(new InvalidOperationException("送信に失敗", inner));

        Assert.True(!described.Contains(GeminiKey), $"キーが残る: {described}");
        AssertContains(described, nameof(InvalidOperationException));
        AssertContains(described, nameof(HttpRequestException));

        // スタックトレースは出さない(例外の種別と連鎖だけで切り分ける)。
        Assert.True(!described.Contains("   at "), $"スタックトレースが残る: {described}");
    }

    private static void FileLoggerRedactsKeys()
    {
        var dir = Path.Combine(Path.GetTempPath(), "TextPolishTests-" + Guid.NewGuid().ToString("N"));

        try
        {
            var logger = new FileLogger(dir);
            logger.Error($"Gemini 呼び出しに失敗: https://example.com/x?key={GeminiKey}");
            logger.Error("例外付き", new HttpRequestException($"Authorization: Bearer {OpenAiKey}"));

            var written = File.ReadAllText(logger.LogFilePath);

            Assert.True(!written.Contains(GeminiKey), "ログファイルにGeminiのキーが残る");
            Assert.True(!written.Contains(OpenAiKey), "ログファイルにOpenAIのキーが残る");
            AssertContains(written, "Gemini 呼び出しに失敗");
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 後片付けの失敗はテスト結果に影響させない。
            }
        }
    }

    // ---- 実行補助 ----

    private static string RunOpenAi(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var settings = new OpenAiCompatibleSettings
        {
            Endpoint = "https://example.invalid/v1/chat/completions",
            ApiKey = OpenAiKey,
            Model = "test-model",
        };

        return Run((client, logger) =>
            new OpenAiCompatibleFormatter(client, settings, new StubPromptProvider(), logger), respond);
    }

    private static string RunAzure(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var settings = new AzureOpenAiSettings
        {
            Endpoint = "https://example.invalid",
            ApiKey = AzureKey,
            Deployment = "test-deployment",
        };

        return Run((client, logger) =>
            new AzureOpenAiFormatter(client, settings, new StubPromptProvider(), logger), respond);
    }

    private static string RunGemini(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var settings = new GeminiSettings
        {
            Endpoint = "https://example.invalid/v1beta/models/test:generateContent",
            ApiKey = GeminiKey,
            AuthMode = "ApiKeyQueryParam",
        };

        return Run((client, logger) =>
            new GeminiFormatter(client, settings, new StubPromptProvider(), logger), respond);
    }

    /// <summary>整形を1回実行し、記録されたログをまとめて返す。失敗することを前提にしている。</summary>
    private static string Run(
        Func<HttpClient, IAppLogger, ILlmFormatter> create,
        Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var logger = new RecordingLogger();
        using var client = new HttpClient(new StubHandler(respond));
        var formatter = create(client, logger);

        var request = new LlmFormatRequest { RawText = Secret };
        var failed = false;

        try
        {
            formatter.FormatAsync(request).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            failed = true;
        }

        Assert.True(failed, "失敗する応答を渡しているので例外になるはず");
        return logger.Text;
    }

    private static void AssertNoSecrets(string log, string apiKey)
    {
        Assert.True(!log.Contains(Secret), $"利用者の文章がログに残っている: {log}");
        Assert.True(!log.Contains(apiKey), $"APIキーがログに残っている: {log}");

        // 文章の一部だけが残っていても漏えいになるため、特徴的な語でも見る。
        Assert.True(!log.Contains("PROJECT-KAMIFUSEN"), $"文章の一部がログに残っている: {log}");
        Assert.True(!log.Contains("Body="), $"応答本文をそのまま書いている: {log}");
    }

    private static void AssertContains(string log, string expected) =>
        Assert.True(log.Contains(expected), $"診断情報 '{expected}' がログにない: {log}");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private static string ErrorBody(string type, string code) =>
        "{\"error\":{\"message\":\"" + Secret + "\",\"type\":\"" + type + "\",\"code\":" + code + "}}";

    private static string ChatCompletion(string content, string finishReason) =>
        "{\"choices\":[{\"message\":{\"content\":\"" + content + "\"},\"finish_reason\":\"" + finishReason + "\"}]}";

    private static string GeminiResponse(string text, string finishReason) =>
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"" + text + "\"}]}," +
        "\"finishReason\":\"" + finishReason + "\"}]}";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_respond(request));
    }

    private sealed class StubPromptProvider : ISystemPromptProvider
    {
        public Task<string> GetSystemPromptAsync(CancellationToken ct = default) =>
            Task.FromResult("テスト用のシステムプロンプト");
    }

    /// <summary>
    /// 記録するだけのロガー。<see cref="FileLogger"/> と同じ組み立てで1行にするため、
    /// 例外オブジェクトを渡された場合の全文(<c>ToString</c>)もそのまま検査対象になる。
    /// </summary>
    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<string> _lines = new();

        public string Text => string.Join(Environment.NewLine, _lines);

        public void Info(string message) => _lines.Add($"[INFO] {message}");

        public void Warn(string message) => _lines.Add($"[WARN] {message}");

        public void Error(string message, Exception? ex = null) =>
            _lines.Add($"[ERROR] {(ex is null ? message : $"{message} :: {ex}")}");
    }
}
