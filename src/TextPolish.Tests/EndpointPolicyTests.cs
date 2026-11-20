using System.Net;
using System.Net.Http;
using TextPolish.Core.Configuration;
using TextPolish.Core.Llm;
using TextPolish.Core.Logging;

namespace TextPolish.Tests;

/// <summary>
/// 送信先の検査(<see cref="EndpointPolicy"/>)。
///
/// このツールは利用者が書いた文章とAPIキーを、設定されたURLへそのまま送る。
/// <c>http://</c> のままだと、どちらも経路上を平文で流れる。設定画面に入力欄があり、
/// <c>appsettings.json</c> は手で編集できるため、気づかないうちに平文の送信先になりうる。
///
/// ここで固定したいのは2つ。
///
///   ・ループバックの http は**通す**(ローカルのOpenAI互換サーバーと、実機確認のスタブが動かなくなるため)
///   ・それ以外の http は、**1バイトも送らずに**止める(3プロバイダとも)
///
/// 後者はログやHTTPの中身ではなく、**ハンドラが一度も呼ばれないこと**で見る。
/// 送ってしまってから止めても、文章と鍵は既に出ている。
/// ネットワークもデスクトップも使わないため <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class EndpointPolicyTests
{
    private const string SecretText = "社外秘の議事録です。PROJECT-KAMIFUSEN の件。";
    private const string ApiKey = "sk-proj-SECRETKEY1234567890abcdef";

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("送信先: https は通す", HttpsIsAllowed);
        yield return ("送信先: ループバックの http は通す", LoopbackHttpIsAllowed);
        yield return ("送信先: 外へ出る http は止める", RemoteHttpIsRejected);
        yield return ("送信先: 止めた理由にホスト名を出す", ReasonNamesTheHost);
        yield return ("送信先: http も https もないスキームは止める", OtherSchemesAreRejected);
        yield return ("送信先: 空やURLでない文字列は止める", EmptyAndGarbageAreRejected);
        yield return ("送信先: 前後の空白は無視する", SurroundingSpaceIsIgnored);
        yield return ("送信先: 表示用の文字列はホストまでしか出さない", DescribeKeepsHostOnly);
        yield return ("送信先: OpenAI互換は http へ1バイトも送らない", OpenAiSendsNothingOverHttp);
        yield return ("送信先: Azure は http へ1バイトも送らない", AzureSendsNothingOverHttp);
        yield return ("送信先: Gemini は http へ1バイトも送らない", GeminiSendsNothingOverHttp);
        yield return ("送信先: 止めたことはログに残るが、鍵と文章は残らない", RejectionIsLoggedWithoutSecrets);
        yield return ("送信先: ループバックなら実際に送られる", LoopbackIsActuallySent);
    }

    // ---- 判定そのもの ----

    private static void HttpsIsAllowed()
    {
        foreach (var url in new[]
        {
            "https://openrouter.ai/api/v1/chat/completions",
            "https://generativelanguage.googleapis.com/v1beta/models/x:generateContent",
            "https://example.openai.azure.com",
        })
        {
            Assert.True(EndpointPolicy.IsAllowed(url, out var reason), $"{url} を止めている: {reason}");
        }
    }

    private static void LoopbackHttpIsAllowed()
    {
        // ここを塞ぐと、ローカルのLLMを使う運用と実機確認のスタブが動かなくなる。
        // 自分のPCの中で閉じているので、経路上を平文で流れる問題は起きない。
        foreach (var url in new[]
        {
            "http://127.0.0.1:8080/v1/chat/completions",
            "http://localhost:1234/v1/chat/completions",
            "http://[::1]:8080/v1/chat/completions",
        })
        {
            Assert.True(EndpointPolicy.IsAllowed(url, out var reason), $"{url} を止めている: {reason}");
        }
    }

    private static void RemoteHttpIsRejected()
    {
        foreach (var url in new[]
        {
            "http://openrouter.ai/api/v1/chat/completions",
            "http://192.168.1.10:8080/v1/chat/completions",
            "http://example.invalid/v1",
        })
        {
            Assert.True(EndpointPolicy.IsAllowed(url, out _) == false, $"{url} を通している");
        }
    }

    private static void ReasonNamesTheHost()
    {
        EndpointPolicy.IsAllowed("http://openrouter.ai/api/v1/chat/completions", out var reason);

        // どの設定を直せばよいか分かる文面であること。
        Assert.True(reason.Contains("openrouter.ai"), $"理由にホストがない: {reason}");
        Assert.True(reason.Contains("https"), $"直し方が書かれていない: {reason}");
    }

    private static void OtherSchemesAreRejected()
    {
        foreach (var url in new[] { "ftp://example.invalid/v1", "file:///C:/tmp/x.json", "ws://localhost:8080" })
        {
            Assert.True(EndpointPolicy.IsAllowed(url, out _) == false, $"{url} を通している");
        }
    }

    private static void EmptyAndGarbageAreRejected()
    {
        foreach (var url in new[] { null, string.Empty, "   ", "example.invalid/v1", "/v1/chat/completions" })
        {
            Assert.True(EndpointPolicy.IsAllowed(url, out _) == false, $"<{url}> を通している");
        }
    }

    private static void SurroundingSpaceIsIgnored()
    {
        // 設定ファイルを手で書くと末尾に空白や改行が入りやすい。
        Assert.True(
            EndpointPolicy.IsAllowed("  https://openrouter.ai/api/v1/chat/completions \r\n", out var reason),
            $"空白つきを止めている: {reason}");
    }

    private static void DescribeKeepsHostOnly()
    {
        // パスにはモデル名や配置名、クエリには鍵が載りうる。表示用はホストまで。
        Assert.Equal(
            "https://generativelanguage.googleapis.com",
            EndpointPolicy.Describe("https://generativelanguage.googleapis.com/v1beta/models/x:generateContent?key=AIzaSyXXXX"),
            "表示用の文字列");
        Assert.Equal("(未設定)", EndpointPolicy.Describe(" "), "未設定のとき");
        Assert.Equal("(URLとして読めません)", EndpointPolicy.Describe("example.invalid"), "読めないとき");
    }

    // ---- 送信直前で止まるか ----

    private static void OpenAiSendsNothingOverHttp()
    {
        var (sent, error, _) = Run(Provider.OpenAi, "http://openrouter.ai/api/v1/chat/completions");

        Assert.Equal(0, sent, "送ってしまった件数");
        Assert.True(error is LlmRejectedException, $"専用の例外で止めていない: {error?.GetType().Name}");
    }

    private static void AzureSendsNothingOverHttp()
    {
        var (sent, error, _) = Run(Provider.Azure, "http://example.openai.azure.com");

        Assert.Equal(0, sent, "送ってしまった件数");
        Assert.True(error is LlmRejectedException, $"専用の例外で止めていない: {error?.GetType().Name}");
    }

    private static void GeminiSendsNothingOverHttp()
    {
        var (sent, error, _) = Run(
            Provider.Gemini, "http://generativelanguage.googleapis.com/v1beta/models/x:generateContent");

        Assert.Equal(0, sent, "送ってしまった件数");
        Assert.True(error is LlmRejectedException, $"専用の例外で止めていない: {error?.GetType().Name}");
    }

    private static void RejectionIsLoggedWithoutSecrets()
    {
        var (_, error, log) = Run(Provider.OpenAi, "http://openrouter.ai/api/v1/chat/completions");

        // 何が起きたかは分からないといけない。
        Assert.True(log.Contains("openrouter.ai"), $"ログに送信先がない: {log}");

        // 利用者に見せる文面も、原因と直し方が分かるものであること。
        Assert.True(error!.Message.Contains("https"), $"直し方が伝わらない: {error.Message}");

        // そのかわり、鍵と文章は残らないこと。
        Assert.True(log.Contains(ApiKey) == false, "ログにAPIキーが残っている");
        Assert.True(log.Contains(SecretText) == false, "ログに整形対象の文章が残っている");
    }

    private static void LoopbackIsActuallySent()
    {
        // 止めすぎていないことの裏返し。ループバックなら素通りして実際に送られる。
        var (sent, error, _) = Run(Provider.OpenAi, "http://127.0.0.1:8080/v1/chat/completions");

        Assert.Equal(1, sent, "送られた件数");
        Assert.True(error is null, $"ループバックを止めている: {error?.Message}");
    }

    // ---- 足場 ----

    private enum Provider
    {
        OpenAi,
        Azure,
        Gemini,
    }

    /// <summary>
    /// 整形を1回試し、(実際に送られた件数, 投げられた例外, ログ) を返す。
    /// ハンドラは数えるだけで、成功の応答を返す。
    /// </summary>
    private static (int Sent, Exception? Error, string Log) Run(Provider provider, string endpoint)
    {
        var sent = 0;
        var logger = new RecordingLogger();
        using var client = new HttpClient(new CountingHandler(() => sent++));

        ILlmFormatter formatter = provider switch
        {
            Provider.OpenAi => new OpenAiCompatibleFormatter(
                client,
                new OpenAiCompatibleSettings { Endpoint = endpoint, ApiKey = ApiKey, Model = "test-model" },
                new PromptProvider(),
                logger),

            Provider.Azure => new AzureOpenAiFormatter(
                client,
                new AzureOpenAiSettings
                {
                    Endpoint = endpoint,
                    ApiKey = ApiKey,
                    Deployment = "test-deploy",
                    ApiVersion = "2024-10-21",
                },
                new PromptProvider(),
                logger),

            _ => new GeminiFormatter(
                client,
                new GeminiSettings { Endpoint = endpoint, ApiKey = ApiKey, AuthMode = "ApiKeyQueryParam" },
                new PromptProvider(),
                logger),
        };

        Exception? error = null;

        try
        {
            formatter.FormatAsync(new LlmFormatRequest
            {
                RawText = SecretText,
                TaskInstructions = new[] { "整えてください" },
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            error = ex;
        }

        return (sent, error, logger.Text);
    }

    /// <summary>送られてきたら数えて、採用できる応答を返すだけのハンドラ。</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Action _onSend;

        public CountingHandler(Action onSend) => _onSend = onSend;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onSend();

            // どのプロバイダでも採用される形にはしない。ここで見たいのは
            // 「送られたかどうか」だけなので、採否は問わない。
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]," +
                    "\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"ok\"}]},\"finishReason\":\"STOP\"}]}"),
            });
        }
    }

    private sealed class PromptProvider : ISystemPromptProvider
    {
        public Task<string> GetSystemPromptAsync(CancellationToken ct = default) =>
            Task.FromResult("あなたは文章を整える編集者です。");
    }

    private sealed class RecordingLogger : IAppLogger
    {
        private readonly List<string> _lines = new();

        public string Text => string.Join("\n", _lines);

        public void Info(string message) => _lines.Add($"INFO {message}");

        public void Warn(string message) => _lines.Add($"WARN {message}");

        public void Error(string message, Exception? ex = null) =>
            _lines.Add($"ERROR {message} {ex}");
    }
}
