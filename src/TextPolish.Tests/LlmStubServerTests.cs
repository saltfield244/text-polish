using System.IO;
using System.Net;
using System.Net.Http;
using TextPolish.Core.Configuration;
using TextPolish.Core.Llm;
using TextPolish.Core.Logging;
using TextPolish.Tests.Harness;

namespace TextPolish.Tests;

/// <summary>
/// 実機確認を自動化するための土台(<see cref="LlmStubServer"/> と <see cref="LogFileAssert"/>)が
/// 本当に使えることを固定する。
///
/// 既存の <see cref="LlmErrorLoggingTests"/> は <c>HttpMessageHandler</c> を差し替えて
/// 「Formatter の中の判断」を見ている。こちらは**本物のソケットを通して**
/// 3つの Formatter を動かし、「スタブへ向ければアプリが動く」ことを見ている。
/// 実機確認では同じスタブへアプリ本体を向けるため、ここが通らないと土台が成り立たない。
///
/// ループバックのTCPしか使わない(デスクトップもクリップボードも触らない)ため、
/// <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class LlmStubServerTests
{
    /// <summary>整形を依頼した文章に相当する文。ログに残っていないことを確かめるのに使う。</summary>
    private const string Secret = "社外秘の議事録です。来期の人員削減はPROJECT-KAMIFUSENとして進めます。";

    private const string OpenAiKey = "sk-proj-STUBKEY1234567890abcdef";
    private const string AzureKey = "azure-STUBKEY1234567890abcdef";
    private const string GeminiKey = "AIzaSyStubKEY1234567890abcdef";

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("スタブ: OpenAI互換の正常応答を採用する", OpenAiSuccess);
        yield return ("スタブ: Azureの正常応答を採用する", AzureSuccess);
        yield return ("スタブ: Geminiの正常応答を採用する", GeminiSuccess);

        yield return ("スタブ: OpenAI互換の途中終了を採用しない", OpenAiTruncatedIsRejected);
        yield return ("スタブ: Azureの途中終了を採用しない", AzureTruncatedIsRejected);
        yield return ("スタブ: Geminiの途中終了を採用しない(MAX_TOKENS)", GeminiTruncatedIsRejected);

        yield return ("スタブ: 終了理由を返さないサーバーでも整形できる(F5)", NoFinishReasonIsAccepted);
        yield return ("スタブ: GeminiでもfinishReasonなしで整形できる(F5)", GeminiNoFinishReasonIsAccepted);

        yield return ("スタブ: 401を返すと不採用になり本文もキーも残らない", HttpErrorKeepsNoSecrets);
        yield return ("スタブ: 壊れたJSONでも落ちずに不採用になる", MalformedIsRejected);
        yield return ("スタブ: 本文が空の応答を採用しない", EmptyContentIsRejected);

        yield return ("スタブ: 指定した時間だけ応答を遅らせる", DelayIsHonored);
        yield return ("スタブ: 実行中にシナリオを差し替えられる", ScenarioCanBeSwappedWhileRunning);
        yield return ("スタブ: 呼ばれた回数を数えられる", RequestCountIsObservable);
        yield return ("スタブ: 送られたリクエスト本文を検証できる", SentBodyIsObservable);
        yield return ("スタブ: GeminiのキーはURLクエリで送られ、ログには残らない", GeminiKeyGoesToQueryButNotToLog);

        yield return ("ログ照合: 原文の断片を見つける", LogAssertFindsFragment);
        yield return ("ログ照合: APIキーらしい形を見つける", LogAssertFindsKeyShape);
        yield return ("ログ照合: 改行で割れた記録を見つける", LogAssertFindsSplitRecord);
        yield return ("ログ照合: 操作の前後の差分だけを見られる", LogAssertReadsOnlyNewLines);
        yield return ("ログ照合: 行が現れるまで待てる", LogAssertWaitsForLine);
        yield return ("ログ照合: 水準ごとに数えられ、ERRORの不在を言える", LogAssertCountsLevels);
    }

    // ---- 正常系 ----

    private static void OpenAiSuccess()
    {
        var (text, log) = Run(Provider.OpenAi, new StubScenario { Text = "整えました。" });

        Assert.Equal("整えました。", text, "OpenAI互換の整形結果");
        Assert.True(!log.Contains("[ERROR]"), $"ERROR が出ている: {log}");
    }

    private static void AzureSuccess()
    {
        var (text, _) = Run(Provider.Azure, new StubScenario { Text = "整えました(Azure)。" });
        Assert.Equal("整えました(Azure)。", text, "Azureの整形結果");
    }

    private static void GeminiSuccess()
    {
        var (text, _) = Run(Provider.Gemini, new StubScenario { Text = "整えました(Gemini)。" });
        Assert.Equal("整えました(Gemini)。", text, "Geminiの整形結果");
    }

    // ---- 途中終了(実機確認 F1・F2・G5 がこの状態を作っている) ----

    private static void OpenAiTruncatedIsRejected()
    {
        var (text, log) = Run(Provider.OpenAi, new StubScenario { Outcome = StubOutcome.Truncated });

        Assert.True(text is null, "途中終了を採用してしまっている");
        AssertContains(log, "finish_reason=length");
        AssertContains(log, "判定=Truncated");
        AssertNoSecrets(log, OpenAiKey);
    }

    private static void AzureTruncatedIsRejected()
    {
        var (text, log) = Run(Provider.Azure, new StubScenario { Outcome = StubOutcome.Truncated });

        Assert.True(text is null, "途中終了を採用してしまっている(Azure)");
        AssertContains(log, "finish_reason=length");
        AssertNoSecrets(log, AzureKey);
    }

    private static void GeminiTruncatedIsRejected()
    {
        var (text, log) = Run(Provider.Gemini, new StubScenario { Outcome = StubOutcome.Truncated });

        Assert.True(text is null, "途中終了を採用してしまっている(Gemini)");
        AssertContains(log, "finishReason=MAX_TOKENS");
        AssertNoSecrets(log, GeminiKey);
    }

    // ---- 終了理由を返さないサーバー(実機確認 F5。環境が無く「省略可」になっていた項目) ----

    private static void NoFinishReasonIsAccepted()
    {
        var (text, log) = Run(
            Provider.OpenAi,
            new StubScenario { Outcome = StubOutcome.NoFinishReason, Text = "終了理由なしでも整形できる。" });

        Assert.Equal("終了理由なしでも整形できる。", text, "終了理由を返さないサーバーの整形結果");
        AssertContains(log, "finish_reason がありませんでした");
        Assert.True(!log.Contains("[ERROR]"), $"異常扱いになっている: {log}");
    }

    private static void GeminiNoFinishReasonIsAccepted()
    {
        var (text, log) = Run(
            Provider.Gemini,
            new StubScenario { Outcome = StubOutcome.NoFinishReason, Text = "Geminiでも採用される。" });

        Assert.Equal("Geminiでも採用される。", text, "finishReason なしの整形結果");
        Assert.True(!log.Contains("[ERROR]"), $"異常扱いになっている: {log}");
    }

    // ---- 失敗系 ----

    private static void HttpErrorKeepsNoSecrets()
    {
        var (text, log) = Run(
            Provider.OpenAi,
            new StubScenario { Outcome = StubOutcome.HttpError, StatusCode = HttpStatusCode.Unauthorized });

        Assert.True(text is null, "エラー応答を採用してしまっている");
        AssertContains(log, "Status=401");
        AssertNoSecrets(log, OpenAiKey);
    }

    private static void MalformedIsRejected()
    {
        var (text, log) = Run(Provider.OpenAi, new StubScenario { Outcome = StubOutcome.Malformed });

        Assert.True(text is null, "壊れたJSONを採用してしまっている");
        AssertNoSecrets(log, OpenAiKey);
    }

    private static void EmptyContentIsRejected()
    {
        var (text, log) = Run(Provider.OpenAi, new StubScenario { Outcome = StubOutcome.EmptyContent });

        Assert.True(string.IsNullOrEmpty(text), "空の本文を整形結果として採用してしまっている");
        AssertNoSecrets(log, OpenAiKey);
    }

    // ---- スタブそのものの性質 ----

    private static void DelayIsHonored()
    {
        // 実機確認で「整形中」の状態を作るための要。短くても効いていることを測る。
        const int delayMs = 400;

        var started = Environment.TickCount64;
        var (text, _) = Run(Provider.OpenAi, new StubScenario { DelayMs = delayMs, Text = "遅延つき" });
        var elapsed = Environment.TickCount64 - started;

        Assert.Equal("遅延つき", text, "遅延つきの整形結果");
        Assert.True(elapsed >= delayMs, $"指定より早く返っている: {elapsed}ms < {delayMs}ms");
    }

    private static void ScenarioCanBeSwappedWhileRunning()
    {
        using var server = new LlmStubServer(new StubScenario { Text = "1回目" });
        using var client = new HttpClient();

        var first = Format(server, client, Provider.OpenAi, out _);
        Assert.Equal("1回目", first, "差し替え前");

        server.Scenario = new StubScenario { Text = "2回目" };

        var second = Format(server, client, Provider.OpenAi, out _);
        Assert.Equal("2回目", second, "差し替え後");

        // 途中終了にも差し替えられる(G5 は成功のあとに途中終了を起こす項目)。
        server.Scenario = new StubScenario { Outcome = StubOutcome.Truncated };
        var third = Format(server, client, Provider.OpenAi, out _);
        Assert.True(third is null, "途中終了へ差し替えたのに採用されている");
    }

    private static void RequestCountIsObservable()
    {
        using var server = new LlmStubServer();
        using var client = new HttpClient();

        Assert.Equal(0, server.RequestCount, "呼ぶ前の件数");

        Format(server, client, Provider.OpenAi, out _);
        Format(server, client, Provider.OpenAi, out _);

        // ホットキーの排他(D1・D2)は「3回押しても1回しか呼ばれない」ことの確認なので、
        // 件数が読めることが要る。
        Assert.Equal(2, server.RequestCount, "呼んだ回数");
    }

    private static void SentBodyIsObservable()
    {
        using var server = new LlmStubServer();
        using var client = new HttpClient();

        Format(server, client, Provider.OpenAi, out _);

        var last = server.LastRequest;
        Assert.True(last is not null, "リクエストが記録されていない");
        Assert.Equal("POST", last!.Method, "メソッド");

        // 「何を送ったか」が読めること。周辺文脈を送らない設定(H1・H4)の確認はこれで行う。
        Assert.True(last.BodyContains(Secret), "送った文章が本文に見えない");
        Assert.True(last.BodyContains("テスト用のシステムプロンプト"), "システムプロンプトが本文に見えない");
        Assert.True(last.BodyContains("\"messages\""), "chat completions の形で送られていない");

        // 日本語は \uXXXX で届くため、生の本文にはそのままでは現れない。
        // この前提が崩れたら BodyContains の作りを見直す必要があるので、ここで固定する。
        Assert.True(!last.Body.Contains(Secret, StringComparison.Ordinal), "生の本文に日本語がそのまま載っている");
        Assert.True(last.DecodedBody.Contains(Secret, StringComparison.Ordinal), "ほどいた本文に文章がない");
    }

    private static void GeminiKeyGoesToQueryButNotToLog()
    {
        using var server = new LlmStubServer();
        using var client = new HttpClient();

        Format(server, client, Provider.Gemini, out var log);

        var last = server.LastRequest;
        Assert.True(last is not null, "リクエストが記録されていない");

        // AI Studio 経路の仕様どおり、キーはURLクエリに乗る(セキュリティレビューの指摘4番)。
        // ここでは「そうなっている」ことを固定し、ログへは出ないことを別に確かめる。
        Assert.True(last!.Target.Contains("?key="), $"キーがURLクエリに乗っていない: {last.Target}");
        Assert.True(last.BodyContains("\"contents\""), "Gemini の形で送られていない");
        Assert.True(!log.Contains(GeminiKey), $"キーがログに残っている: {log}");
    }

    // ---- ログ照合 ----

    private static void LogAssertFindsFragment()
    {
        WithTempLog((logger, log) =>
        {
            logger.Info($"整形しました。{Secret}");

            var caught = Catch(() => log.AssertNoFragmentOf(new[] { "PROJECT-KAMIFUSEN" }));
            Assert.True(caught is not null, "原文の断片を見逃している");
        });
    }

    private static void LogAssertFindsKeyShape()
    {
        WithTempLog((logger, log) =>
        {
            // FileLogger を通した行は値が (秘匿) に置き換わる。名前(?key= や Bearer)は残るので、
            // 名前だけで判定すると正常な記録を漏えいと誤判定する。まずそれが起きないことを見る。
            logger.Info($"呼び出し先: https://example.invalid/v1/models?key={GeminiKey}");
            logger.Error($"認証: Authorization: Bearer {OpenAiKey}");
            log.AssertNoApiKeyShape();

            // そのうえで、生の値が残っている行は見つけられること。
            // FileLogger は必ず秘匿するため、漏えいした状態はファイルへ直接書いて作る
            // (古い版が書いたログや、手で貼られたログを読む場合に相当する)。
            File.AppendAllText(
                TempLogPath,
                $"2026-11-20 12:00:00.000 [INFO] 呼び出し先: https://x.invalid/v1?api_key={GeminiKey}"
                + Environment.NewLine);

            var caught = Catch(() => log.AssertNoApiKeyShape());
            Assert.True(caught is not null, "生のキーが残っている行を見逃している");
            Assert.True(
                caught!.Message.Contains(GeminiKey),
                $"どの値を見つけたのか分からない: {caught.Message}");
        });
    }

    private static void LogAssertFindsSplitRecord()
    {
        WithTempLog((logger, log) =>
        {
            logger.Info("1行目");
            log.AssertEveryLineIsOneRecord();

            // FileLogger は改行を潰すので、割れた記録は自前で書き足して作る。
            File.AppendAllText(TempLogPath, "時刻のない行" + Environment.NewLine);

            var caught = Catch(() => log.AssertEveryLineIsOneRecord());
            Assert.True(caught is not null, "改行で割れた記録を見逃している");
        });
    }

    private static void LogAssertReadsOnlyNewLines()
    {
        WithTempLog((logger, log) =>
        {
            logger.Info("前の操作の記録");
            var mark = log.LineCount;

            logger.Info("今回の操作の記録");

            var after = log.LinesAfter(mark);
            Assert.Equal(1, after.Count, "差分の行数");
            Assert.True(after[0].Contains("今回の操作の記録"), $"差分の内容: {after[0]}");

            // 常駐アプリのログは前日から積み上がるため、全体を見ると判断を誤る。
            log.AssertNoFragmentOf(new[] { "前の操作の記録" }, from: mark);
        });
    }

    private static void LogAssertWaitsForLine()
    {
        WithTempLog((logger, log) =>
        {
            // 別スレッドから少し遅れて書く。GUI操作のあとログに出るまでの間を模す。
            var writer = Task.Run(() =>
            {
                Thread.Sleep(200);
                logger.Info("遅れて書かれた記録");
            });

            var hit = log.WaitForLine("遅れて書かれた記録", timeoutMs: 5000);
            writer.Wait();

            Assert.True(hit is not null, "待っても行を見つけられない");

            // 出ないものは待ち切って null を返す(固まらないこと)。
            var missing = log.WaitForLine("出るはずのない記録", timeoutMs: 300);
            Assert.True(missing is null, "無い行を見つけたと言っている");
        });
    }

    private static void LogAssertCountsLevels()
    {
        WithTempLog((logger, log) =>
        {
            logger.Info("情報");
            logger.Warn("警告");

            Assert.Equal(1, log.CountOfLevel("INFO"), "INFO の件数");
            Assert.Equal(1, log.CountOfLevel("WARN"), "WARN の件数");
            Assert.Equal(0, log.CountOfLevel("ERROR"), "ERROR の件数");

            // 中止やタイムアウトは異常ではないので ERROR で残さない(F9)。
            log.AssertNoError();

            logger.Error("本物の障害");
            var caught = Catch(() => log.AssertNoError());
            Assert.True(caught is not null, "ERROR を見逃している");
        });
    }

    // ---- 補助 ----

    private enum Provider
    {
        OpenAi,
        Azure,
        Gemini,
    }

    [ThreadStatic]
    private static string? _tempLogPath;

    private static string TempLogPath => _tempLogPath
        ?? throw new InvalidOperationException("WithTempLog の外で参照された");

    /// <summary>スタブを立てて整形を1回実行し、整形結果(不採用なら null)とログを返す。</summary>
    private static (string? Text, string Log) Run(Provider provider, StubScenario scenario)
    {
        using var server = new LlmStubServer(scenario);
        using var client = new HttpClient();

        var text = Format(server, client, provider, out var log);
        return (text, log);
    }

    /// <summary>
    /// 本物の Formatter を、本物のソケット越しにスタブへ向けて1回動かす。
    /// 不採用(<see cref="LlmRejectedException"/>)なら null を返す。
    /// </summary>
    private static string? Format(
        LlmStubServer server, HttpClient client, Provider provider, out string log)
    {
        var logger = new RecordingLogger();

        ILlmFormatter formatter = provider switch
        {
            Provider.OpenAi => new OpenAiCompatibleFormatter(
                client,
                new OpenAiCompatibleSettings
                {
                    Endpoint = server.ChatCompletionsUrl,
                    ApiKey = OpenAiKey,
                    Model = "stub-model",
                },
                new StubPromptProvider(),
                logger),

            Provider.Azure => new AzureOpenAiFormatter(
                client,
                new AzureOpenAiSettings
                {
                    Endpoint = server.AzureBaseUrl,
                    ApiKey = AzureKey,
                    Deployment = "stub-deployment",
                },
                new StubPromptProvider(),
                logger),

            _ => new GeminiFormatter(
                client,
                new GeminiSettings
                {
                    Endpoint = server.GeminiUrl,
                    ApiKey = GeminiKey,
                    AuthMode = "ApiKeyQueryParam",
                },
                new StubPromptProvider(),
                logger),
        };

        try
        {
            var text = formatter
                .FormatAsync(new LlmFormatRequest { RawText = Secret })
                .GetAwaiter()
                .GetResult();

            log = logger.Text;
            return text;
        }
        catch (LlmRejectedException)
        {
            log = logger.Text;
            return null;
        }
    }

    /// <summary>一時フォルダに本物の <see cref="FileLogger"/> を立て、後片付けまで面倒を見る。</summary>
    private static void WithTempLog(Action<FileLogger, LogFileAssert> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "TextPolishStub-" + Guid.NewGuid().ToString("N"));

        try
        {
            var logger = new FileLogger(dir);
            _tempLogPath = logger.LogFilePath;
            body(logger, new LogFileAssert(logger.LogFilePath));
        }
        finally
        {
            _tempLogPath = null;

            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // 後片付けの失敗はテストの結果に影響させない。
            }
        }
    }

    /// <summary>投げられた例外を受け取る。投げなければ null。</summary>
    private static Exception? Catch(Action body)
    {
        try
        {
            body();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static void AssertNoSecrets(string log, string apiKey)
    {
        Assert.True(!log.Contains(Secret), $"利用者の文章がログに残っている: {log}");
        Assert.True(!log.Contains(apiKey), $"APIキーがログに残っている: {log}");
        Assert.True(!log.Contains("PROJECT-KAMIFUSEN"), $"文章の一部がログに残っている: {log}");
        Assert.True(!log.Contains("Body="), $"応答本文をそのまま書いている: {log}");
    }

    private static void AssertContains(string log, string expected) =>
        Assert.True(log.Contains(expected), $"診断情報 '{expected}' がログにない: {log}");

    private sealed class StubPromptProvider : ISystemPromptProvider
    {
        public Task<string> GetSystemPromptAsync(CancellationToken ct = default) =>
            Task.FromResult("テスト用のシステムプロンプト");
    }

    /// <summary><see cref="FileLogger"/> と同じ組み立てで記録するだけのロガー。</summary>
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
