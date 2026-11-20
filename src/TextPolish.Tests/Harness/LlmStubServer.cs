using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TextPolish.Tests.Harness;

/// <summary>スタブが返す応答の種類。</summary>
internal enum StubOutcome
{
    /// <summary>正常な応答(終了理由つき)。</summary>
    Success,

    /// <summary>出力途中で打ち切られた応答(finish_reason=length / finishReason=MAX_TOKENS)。</summary>
    Truncated,

    /// <summary>終了理由を返さないサーバー(実機確認 F5 の環境)。</summary>
    NoFinishReason,

    /// <summary>本文が空の応答(推論トークンだけを消費した場合に起こる)。</summary>
    EmptyContent,

    /// <summary>HTTPエラー。<see cref="StubScenario.StatusCode"/> を返す。</summary>
    HttpError,

    /// <summary>JSONとして壊れた本文。</summary>
    Malformed,
}

/// <summary>
/// スタブの振る舞い。リクエストごとに読まれるため、実行中に差し替えてよい。
/// </summary>
internal sealed record StubScenario
{
    /// <summary>返す応答の種類。</summary>
    public StubOutcome Outcome { get; init; } = StubOutcome.Success;

    /// <summary>
    /// 応答を返すまでの待ち時間。実機確認で「整形中」の状態を正確な長さだけ作るために使う
    /// (D3・D4・E5・E6・E7 等は、この状態の最中に別の操作をする項目)。
    /// </summary>
    public int DelayMs { get; init; }

    /// <summary>整形結果として返す文字列。</summary>
    public string Text { get; init; } = "これはスタブが返した整形結果です。";

    /// <summary><see cref="StubOutcome.HttpError"/> のときに返すステータス。</summary>
    public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.Unauthorized;
}

/// <summary>スタブが受け取ったリクエスト1件。本文は検証のためメモリ上にだけ持つ。</summary>
internal sealed record StubRequest(string Method, string Target, string Body)
{
    // JSONの \uXXXX エスケープ。System.Text.Json は既定で非ASCIIをすべてこの形に逃がすため、
    // 日本語の本文はそのままでは文字列として探せない。
    private static readonly Regex UnicodeEscape = new(
        @"\\u([0-9a-fA-F]{4})", RegexOptions.Compiled);

    /// <summary>
    /// <see cref="Body"/> の \uXXXX をほどいたもの。
    ///
    /// アプリが何を送ったかを調べるには、こちらを見る必要がある。
    /// <c>JsonContent</c> は <c>JavaScriptEncoder.Default</c> を使うので、
    /// 「社外秘」は <c>社外秘</c> として届く。
    /// JSONとして壊れていてもほどけるよう、構文解析ではなく置換で処理する。
    /// </summary>
    public string DecodedBody => UnicodeEscape.Replace(
        Body,
        m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());

    /// <summary>
    /// リクエスト本文に、その文字列が含まれるか。
    /// エスケープをほどいた側でも探すため、日本語をそのまま渡してよい。
    /// </summary>
    public bool BodyContains(string text) =>
        Body.Contains(text, StringComparison.Ordinal)
        || DecodedBody.Contains(text, StringComparison.Ordinal);
}

/// <summary>
/// LLMプロバイダのふりをする、ごく小さなHTTPサーバー。
///
/// なぜ必要か: 実機確認の残り項目(D・E・G)で人手が要る理由のほとんどは
/// 「整形中という状態を作ること」で、本物のAPI呼び出しに頼ると
/// 遅い・有料・タイミングが毎回違う。スタブへ向ければ、応答を正確に N ミリ秒遅らせる、
/// 途中終了を返す、401を返す、が自在になり、同じ手順を何度でも繰り返せる。
/// 設定の Endpoint はスキームを検査していないため、http://127.0.0.1:ポート を指定できる。
///
/// <see cref="HttpListener"/> ではなく <see cref="TcpListener"/> を使っている。
/// HttpListener は管理者でない場合にURL予約(netsh http add urlacl)を要求することがあり、
/// 利用者の環境でテストが動かなくなるため。必要なのはHTTP/1.1の応答1種類だけなので、
/// 自前で書くほうが前提が少ない。
///
/// 応答の形はリクエスト本文から判別する。Gemini は contents / systemInstruction を送り、
/// OpenAI互換とAzureは messages を送るため、送られてきたもので見分けられる
/// (OpenAI互換とAzureは応答の形が同じなので、この2つを区別する必要はない)。
///
/// 受け取ったリクエスト本文を保持するのは、「何を送ったか」を検証できるようにするため
/// (周辺文脈を送らない設定で本当に送っていないか、用語辞書が載っているか)。
/// メモリ上にだけ置き、ファイルへは書かない。
///
/// 外部NuGetゼロの方針どおり、BCLだけで書いている。
/// </summary>
internal sealed class LlmStubServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<StubRequest> _requests = new();
    private readonly object _lock = new();
    private readonly Task _acceptLoop;

    /// <param name="scenario">最初の振る舞い。</param>
    /// <param name="port">
    /// 待ち受けポート。既定の0はOSに空きを選ばせる(自動テストではこれでよい)。
    /// 手動確認では <c>appsettings.json</c> にURLを書くため、固定したポートを渡す。
    /// </param>
    public LlmStubServer(StubScenario? scenario = null, int port = 0)
    {
        Scenario = scenario ?? new StubScenario();

        // ポート0でOSに空きポートを選ばせる。固定すると並行実行や後片付け漏れで衝突する。
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>実行中に差し替えてよい。次のリクエストから効く。</summary>
    public StubScenario Scenario { get; set; }

    /// <summary>OSが選んだ待ち受けポート。</summary>
    public int Port { get; }

    /// <summary>OpenAI互換の Endpoint にそのまま入れられるURL。</summary>
    public string ChatCompletionsUrl => $"http://127.0.0.1:{Port}/v1/chat/completions";

    /// <summary>Azure OpenAI の Endpoint(以降のパスは Formatter が組み立てる)。</summary>
    public string AzureBaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>Gemini の Endpoint にそのまま入れられるURL。</summary>
    public string GeminiUrl => $"http://127.0.0.1:{Port}/v1beta/models/stub:generateContent";

    /// <summary>受け取ったリクエスト。新しいものが後ろ。</summary>
    public IReadOnlyList<StubRequest> Requests
    {
        get
        {
            lock (_lock)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>受け取ったリクエストの件数。排他の確認(1回しか呼ばれないこと)に使う。</summary>
    /// <summary>
    /// リクエストを受け取るたびに呼ばれる。回数と中身を渡す。
    /// 画面へ出すのは呼び出し側の役目(本文をそのまま出さないため)。
    /// </summary>
    public Action<int, StubRequest>? OnRequest { get; set; }

    public int RequestCount
    {
        get
        {
            lock (_lock)
            {
                return _requests.Count;
            }
        }
    }

    /// <summary>最後に受け取ったリクエスト。1件も無ければ null。</summary>
    public StubRequest? LastRequest
    {
        get
        {
            lock (_lock)
            {
                return _requests.Count == 0 ? null : _requests[^1];
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            _listener.Stop();
        }
        catch (SocketException)
        {
            // 停止済み。
        }

        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // 待ち受けの打ち切りによる例外は想定内。
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                return;
            }

            // 1接続ずつ別タスクで処理する。待ち時間のある応答が、次の接続受付を止めないため。
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream);

                if (request is null)
                {
                    return;
                }

                int count;

                lock (_lock)
                {
                    _requests.Add(request);
                    count = _requests.Count;
                }

                // 受け取ったことをその場で知らせる。手で確認しているときに
                // 「押したのに来ていない」「2回来ている」が画面で分かる
                // (--stub の実行中は標準入力で count を打てないため、ここで出す)。
                OnRequest?.Invoke(count, request);

                // Scenario はリクエストごとに1度だけ読む。処理の途中で差し替えられても、
                // 1件の応答の中で内容が食い違わないようにするため。
                var scenario = Scenario;

                if (scenario.DelayMs > 0)
                {
                    await Task.Delay(scenario.DelayMs, _cts.Token);
                }

                var isGemini = request.BodyContains("\"contents\"");
                var (status, body) = BuildResponse(scenario, isGemini);
                await WriteResponseAsync(stream, status, body);
                await CloseGracefullyAsync(client, stream);
            }
            catch (OperationCanceledException)
            {
                // サーバーの停止中。
            }
            catch (IOException)
            {
                // 相手が切った。テストの後片付けで普通に起きる。
            }
            catch (SocketException)
            {
                // 同上。
            }
        }
    }

    /// <summary>
    /// 応答を書いたあと、相手が読み終えるのを待ってから閉じる。
    ///
    /// 書いた直後にソケットを閉じると、相手がまだ送信中・受信中の場合に
    /// 接続が異常終了し、クライアント側は
    /// 「Error while copying content to a stream」で失敗する。
    /// FIN を送ってから相手が閉じるまで読み、正常終了させる。
    /// </summary>
    private static async Task CloseGracefullyAsync(TcpClient client, NetworkStream stream)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch (SocketException)
        {
            return;
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        // 相手が閉じるまで読み捨てる。相手が閉じないまま黙っても、
        // テストを止めないよう短い時間で切り上げる。
        client.ReceiveTimeout = 1000;
        var drain = new byte[1024];

        try
        {
            while (await stream.ReadAsync(drain, 0, drain.Length) > 0)
            {
                // 読み捨てるだけ。
            }
        }
        catch (IOException)
        {
            // 時間切れ、または相手が切った。どちらもここでは正常。
        }
    }

    /// <summary>
    /// リクエスト行とヘッダーを空行まで読み、続いて本文を読む。
    ///
    /// 本文は Content-Length とチャンク転送の両方に対応する必要がある。
    /// <c>JsonContent</c> は長さを前もって計算できないため、HttpClient は
    /// <c>Transfer-Encoding: chunked</c> で送ってくる。本文を読み切らずに応答して接続を切ると、
    /// クライアントは送信中に接続を失い "Error while copying content to a stream" で失敗する。
    /// </summary>
    private static async Task<StubRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var head = new MemoryStream();
        var one = new byte[1];

        const byte Cr = 13;
        const byte Lf = 10;

        // CRLF CRLF まで1バイトずつ読む。まとめて先読みすると本文を食ってしまう。
        var matched = 0;
        while (matched < 4)
        {
            var read = await stream.ReadAsync(one, 0, 1);
            if (read == 0)
            {
                return null;
            }

            var b = one[0];
            head.WriteByte(b);

            // 期待している文字(CR, LF, CR, LF)と一致すれば進める。外れたら
            // 「そのバイトが新しいCRかどうか」まで戻す。
            var expected = matched % 2 == 0 ? Cr : Lf;
            matched = b == expected ? matched + 1 : (b == Cr ? 1 : 0);

            if (head.Length > 64 * 1024)
            {
                return null;
            }
        }

        var headText = Encoding.ASCII.GetString(head.ToArray());
        var lines = headText.Split("\r\n");
        var requestLine = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();
        var method = requestLine.Length > 0 ? requestLine[0] : string.Empty;
        var target = requestLine.Length > 1 ? requestLine[1] : string.Empty;

        var length = 0;
        var chunked = false;

        foreach (var line in lines)
        {
            const string LengthHeader = "Content-Length:";
            const string EncodingHeader = "Transfer-Encoding:";

            if (line.StartsWith(LengthHeader, StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line[LengthHeader.Length..].Trim(), out length);
            }
            else if (line.StartsWith(EncodingHeader, StringComparison.OrdinalIgnoreCase)
                && line.Contains("chunked", StringComparison.OrdinalIgnoreCase))
            {
                chunked = true;
            }
        }

        var body = chunked
            ? await ReadChunkedBodyAsync(stream)
            : await ReadFixedLengthBodyAsync(stream, length);

        return new StubRequest(method, target, body);
    }

    private static async Task<string> ReadFixedLengthBodyAsync(NetworkStream stream, int length)
    {
        var bytes = new byte[length];
        var offset = 0;

        while (offset < length)
        {
            var read = await stream.ReadAsync(bytes, offset, length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return Encoding.UTF8.GetString(bytes, 0, offset);
    }

    /// <summary>
    /// チャンク転送の本文を読む。各チャンクは「16進の長さ CRLF 中身 CRLF」で、
    /// 長さ0のチャンクで終わる。長さの行には拡張(;で続く)が付くことがある。
    /// </summary>
    private static async Task<string> ReadChunkedBodyAsync(NetworkStream stream)
    {
        var body = new MemoryStream();

        while (true)
        {
            var sizeLine = await ReadLineAsync(stream);
            if (sizeLine is null)
            {
                break;
            }

            var semicolon = sizeLine.IndexOf(';');
            var sizeText = (semicolon >= 0 ? sizeLine[..semicolon] : sizeLine).Trim();

            if (!int.TryParse(
                    sizeText,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var size))
            {
                break;
            }

            if (size == 0)
            {
                // 終端チャンク。この後に続くトレーラーと空行まで必ず読み切る。
                // 受信バッファに未読のバイトを残したままソケットを閉じると、Windows は
                // FIN ではなく RST を送り、書き終えた応答ごと破棄されてしまう。
                while (await ReadLineAsync(stream) is { Length: > 0 })
                {
                    // トレーラー行を読み捨てる。空行が来たら終わり。
                }

                break;
            }

            var chunk = new byte[size];
            var offset = 0;
            while (offset < size)
            {
                var read = await stream.ReadAsync(chunk, offset, size - offset);
                if (read == 0)
                {
                    break;
                }

                offset += read;
            }

            body.Write(chunk, 0, offset);

            // 中身のあとの CRLF を読み捨てる。
            _ = await ReadLineAsync(stream);
        }

        return Encoding.UTF8.GetString(body.ToArray());
    }

    /// <summary>CRLF までを1行として読む。接続が切れたら null。</summary>
    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var line = new MemoryStream();
        var one = new byte[1];
        var sawCr = false;

        while (true)
        {
            var read = await stream.ReadAsync(one, 0, 1);
            if (read == 0)
            {
                return line.Length == 0 ? null : Encoding.ASCII.GetString(line.ToArray());
            }

            if (one[0] == 10 && sawCr)
            {
                return Encoding.ASCII.GetString(line.ToArray());
            }

            if (sawCr)
            {
                line.WriteByte(13);
            }

            sawCr = one[0] == 13;

            if (!sawCr)
            {
                line.WriteByte(one[0]);
            }

            if (line.Length > 8 * 1024)
            {
                return Encoding.ASCII.GetString(line.ToArray());
            }
        }
    }

    private static (HttpStatusCode Status, string Body) BuildResponse(StubScenario scenario, bool isGemini)
    {
        if (scenario.Outcome == StubOutcome.HttpError)
        {
            return (scenario.StatusCode,
                "{\"error\":{\"code\":\"stub_error\",\"message\":\"スタブが返したエラーです。\"}}");
        }

        if (scenario.Outcome == StubOutcome.Malformed)
        {
            return (HttpStatusCode.OK, "{\"choices\":[{\"message\":");
        }

        var text = scenario.Outcome == StubOutcome.EmptyContent ? string.Empty : scenario.Text;

        // 日本語や引用符をそのまま埋めると壊れるため、文字列化は JsonSerializer に任せる。
        var quoted = JsonSerializer.Serialize(text);

        if (isGemini)
        {
            var reason = scenario.Outcome switch
            {
                StubOutcome.Truncated => "\"finishReason\":\"MAX_TOKENS\",",
                StubOutcome.NoFinishReason => string.Empty,
                _ => "\"finishReason\":\"STOP\",",
            };

            return (HttpStatusCode.OK,
                "{\"candidates\":[{" + reason +
                "\"content\":{\"role\":\"model\",\"parts\":[{\"text\":" + quoted + "}]}}]}");
        }

        var finish = scenario.Outcome switch
        {
            StubOutcome.Truncated => "\"finish_reason\":\"length\",",
            StubOutcome.NoFinishReason => string.Empty,
            _ => "\"finish_reason\":\"stop\",",
        };

        return (HttpStatusCode.OK,
            "{\"choices\":[{" + finish +
            "\"message\":{\"role\":\"assistant\",\"content\":" + quoted + "}}]}");
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpStatusCode status, string body)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);

        // Connection: close にしておく。接続を使い回させないほうが、
        // 後片付けでソケットが残らず、テストの終わりが読みやすい。
        var head = new StringBuilder()
            .Append($"HTTP/1.1 {(int)status} {ReasonPhrase(status)}\r\n")
            .Append("Content-Type: application/json; charset=utf-8\r\n")
            .Append($"Content-Length: {bodyBytes.Length}\r\n")
            .Append("Connection: close\r\n")
            .Append("\r\n")
            .ToString();

        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes, 0, headBytes.Length);
        await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length);
        await stream.FlushAsync();
    }

    private static string ReasonPhrase(HttpStatusCode status) => status switch
    {
        HttpStatusCode.OK => "OK",
        HttpStatusCode.Unauthorized => "Unauthorized",
        HttpStatusCode.TooManyRequests => "Too Many Requests",
        HttpStatusCode.InternalServerError => "Internal Server Error",
        _ => status.ToString(),
    };
}
