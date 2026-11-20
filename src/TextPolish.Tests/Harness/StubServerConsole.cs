namespace TextPolish.Tests.Harness;

/// <summary>
/// <see cref="LlmStubServer"/> を単体で立てて、手で操作できるようにする。
///
/// 実機確認の D・E・G は「整形中に別の操作をする」項目が多く、本物のAPIでは
/// 待ち時間が毎回違うため手順を再現できない。スタブへ向けておけば、
/// 「次の整形は5秒かかる」「次の整形は途中終了する」をその場で決められる。
///
/// 使い方:
///   TextPolish.Tests --stub
///   TextPolish.Tests --stub --port 8787 --delay 5000 --outcome truncated
///
/// 立ち上がると appsettings.json へ貼るURLを表示する。あとは標準入力に
/// コマンドを打って振る舞いを変える(help で一覧)。
/// </summary>
internal static class StubServerConsole
{
    public static int Run(string[] args)
    {
        var port = IntArg(args, "--port", 8787);
        var delay = IntArg(args, "--delay", 0);
        var text = StringArg(args, "--text", "これはスタブが返した整形結果です。");
        var outcome = ParseOutcome(StringArg(args, "--outcome", "success"));

        if (outcome is null)
        {
            Console.WriteLine($"--outcome の値が分かりません: {StringArg(args, "--outcome", "")}");
            Console.WriteLine("使えるのは success / truncated / nofinish / empty / error / malformed");
            return 1;
        }

        LlmStubServer server;

        try
        {
            server = new LlmStubServer(
                new StubScenario { Outcome = outcome.Value, DelayMs = delay, Text = text },
                port);
        }
        catch (Exception ex)
        {
            // ポートが使われている場合など。原因が分からないと手が止まるので理由を出す。
            Console.WriteLine($"スタブを立てられませんでした: {ex.Message}");
            Console.WriteLine("--port で別のポートを指定してください。");
            return 1;
        }

        using (server)
        {
            PrintBanner(server);
            PrintScenario(server);
            Console.WriteLine();
            Console.WriteLine("コマンドを入力してください(help で一覧、quit で終了)。");

            while (true)
            {
                Console.Write("> ");
                var line = Console.ReadLine();

                if (line is null || line.Trim() is "quit" or "exit" or "q")
                {
                    return 0;
                }

                Handle(server, line.Trim());
            }
        }
    }

    private static void PrintBanner(LlmStubServer server)
    {
        Console.WriteLine("LLMスタブを起動しました。appsettings.json の Endpoint を下のどれかにしてください。");
        Console.WriteLine();
        Console.WriteLine($"  OpenAiCompatible.Endpoint : {server.ChatCompletionsUrl}");
        Console.WriteLine($"  AzureOpenAI.Endpoint      : {server.AzureBaseUrl}");
        Console.WriteLine($"  Gemini.Endpoint           : {server.GeminiUrl}");
        Console.WriteLine();
        Console.WriteLine("  ApiKey は何でもよい(スタブは検証しない)。");
        Console.WriteLine("  設定ファイルは exe と同じフォルダにある。%AppData% ではない。");
        Console.WriteLine("  確認が終わったら Endpoint を元に戻すこと。");
    }

    private static void PrintScenario(LlmStubServer server)
    {
        var s = server.Scenario;
        Console.WriteLine();
        Console.WriteLine($"いまの設定: 応答={Describe(s.Outcome)} / 待ち時間={s.DelayMs}ms / 呼ばれた回数={server.RequestCount}");
    }

    private static void Handle(LlmStubServer server, string command)
    {
        if (command.Length == 0)
        {
            return;
        }

        var parts = command.Split(' ', 2, StringSplitOptions.TrimEntries);
        var name = parts[0].ToLowerInvariant();
        var value = parts.Length > 1 ? parts[1] : string.Empty;

        switch (name)
        {
            case "help" or "?":
                PrintHelp();
                return;

            case "status":
                PrintScenario(server);
                return;

            case "delay":
                if (int.TryParse(value, out var ms) && ms >= 0)
                {
                    server.Scenario = server.Scenario with { DelayMs = ms };
                    Console.WriteLine($"次の整形から {ms}ms 待たせます。");
                }
                else
                {
                    Console.WriteLine("使い方: delay 5000");
                }

                return;

            case "text":
                if (value.Length > 0)
                {
                    server.Scenario = server.Scenario with { Text = value };
                    Console.WriteLine("整形結果として返す文字列を変えました。");
                }
                else
                {
                    Console.WriteLine("使い方: text 整えた結果の文字列");
                }

                return;

            case "count":
                Console.WriteLine($"呼ばれた回数: {server.RequestCount}");
                return;

            case "last":
                PrintLast(server);
                return;

            default:
                var outcome = ParseOutcome(name);
                if (outcome is not null)
                {
                    server.Scenario = server.Scenario with { Outcome = outcome.Value };
                    Console.WriteLine($"次の整形から「{Describe(outcome.Value)}」を返します。");
                    return;
                }

                Console.WriteLine($"知らないコマンドです: {name}(help で一覧)");
                return;
        }
    }

    /// <summary>
    /// 最後に受け取ったリクエストの要約。周辺文脈を送っていないか(H1・H4)の確認に使う。
    /// 本文そのものは出さない(利用者の文章なので、画面に残すと確認の意味が薄れる)。
    /// </summary>
    private static void PrintLast(LlmStubServer server)
    {
        var last = server.LastRequest;

        if (last is null)
        {
            Console.WriteLine("まだ1件も受け取っていません。");
            return;
        }

        var body = last.DecodedBody;
        Console.WriteLine($"  宛先      : {last.Method} {last.Target}");
        Console.WriteLine($"  本文の長さ: {body.Length} 文字");
        Console.WriteLine($"  周辺文脈  : {(body.Contains("周辺", StringComparison.Ordinal) ? "見出しあり" : "なし")}");
        Console.WriteLine($"  アプリ名  : {(body.Contains("アクティブ", StringComparison.Ordinal) ? "あり" : "なし")}");
        Console.WriteLine($"  用語辞書  : {(body.Contains("用語", StringComparison.Ordinal) ? "あり" : "なし")}");
    }

    private static void PrintHelp()
    {
        Console.WriteLine("  success    次から正常な応答を返す");
        Console.WriteLine("  truncated  次から途中終了を返す(F1・F2・G5 の状態)");
        Console.WriteLine("  nofinish   次から終了理由なしで返す(F5 の状態)");
        Console.WriteLine("  empty      次から本文が空の応答を返す");
        Console.WriteLine("  error      次から401を返す(D8・G4 の状態)");
        Console.WriteLine("  malformed  次から壊れたJSONを返す");
        Console.WriteLine("  delay <ms> 応答までの待ち時間を変える(0で即座に返す)");
        Console.WriteLine("  text <文字列> 整形結果として返す文字列を変える");
        Console.WriteLine("  count      呼ばれた回数(排他の確認に使う)");
        Console.WriteLine("  last       最後のリクエストの要約(何を送ったか)");
        Console.WriteLine("  status     いまの設定");
        Console.WriteLine("  quit       終了");
    }

    private static StubOutcome? ParseOutcome(string value) => value.ToLowerInvariant() switch
    {
        "success" or "ok" => StubOutcome.Success,
        "truncated" or "length" => StubOutcome.Truncated,
        "nofinish" or "nofinishreason" => StubOutcome.NoFinishReason,
        "empty" => StubOutcome.EmptyContent,
        "error" or "401" => StubOutcome.HttpError,
        "malformed" => StubOutcome.Malformed,
        _ => null,
    };

    private static string Describe(StubOutcome outcome) => outcome switch
    {
        StubOutcome.Success => "正常",
        StubOutcome.Truncated => "途中終了",
        StubOutcome.NoFinishReason => "終了理由なし",
        StubOutcome.EmptyContent => "本文が空",
        StubOutcome.HttpError => "HTTPエラー",
        StubOutcome.Malformed => "壊れたJSON",
        _ => outcome.ToString(),
    };

    private static int IntArg(string[] args, string name, int fallback)
    {
        var value = StringArg(args, name, string.Empty);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static string StringArg(string[] args, string name, string fallback)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return fallback;
    }
}
