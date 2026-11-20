namespace TextPolish.Core.Llm;

/// <summary>
/// LLMの送信先として許していいURLかどうかの判定。
///
/// このツールは**利用者が書いた文章とAPIキー**を、設定されたURLへそのまま送る。
/// <c>http://</c> のままだと、どちらも経路上で平文のまま流れる。
/// 設定画面に入力欄があり、<c>appsettings.json</c> は手で編集できるので、
/// 気づかないうちに(あるいは誰かに書き換えられて)平文の送信先になりうる。
///
/// **ループバックだけは http を許す。** ここを塞ぐと、ローカルのOpenAI互換サーバーを
/// 使う運用と、実機確認のスタブ(<c>http://127.0.0.1:ポート</c>)がどちらも動かなくなる。
/// 自分のPCの中で閉じている通信なので、経路上を平文で流れる問題は起きない。
///
/// 判定はここ1か所に置き、**設定の保存時**(設定画面)と**送信直前**(各Formatter)の
/// 両方から呼ぶ。保存時だけでは設定ファイルの手編集を塞げず、送信時だけでは
/// 利用者が間違いに気づくのが整形を試したあとになる。
/// </summary>
public static class EndpointPolicy
{
    /// <summary>
    /// 送信先として使ってよいかを判定する。
    /// 使えない場合、<paramref name="reason"/> にそのまま利用者へ見せられる理由が入る。
    /// </summary>
    public static bool IsAllowed(string? endpoint, out string reason)
    {
        var trimmed = endpoint?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            reason = "エンドポイントが設定されていません。";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            // 相対URLやスキームのない文字列。送る前に気づけるようにする。
            reason = $"エンドポイントがURLとして読めません: {trimmed}";
            return false;
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            reason = string.Empty;
            return true;
        }

        if (uri.Scheme == Uri.UriSchemeHttp)
        {
            // Uri.IsLoopback は localhost・127.0.0.0/8・::1 を真とする。
            if (uri.IsLoopback)
            {
                reason = string.Empty;
                return true;
            }

            reason =
                $"エンドポイントが http:// です({uri.Host})。文章とAPIキーが暗号化されないまま流れます。" +
                "https:// に直してください(自分のPCの中で動かしているサーバーだけは http:// のまま使えます)。";
            return false;
        }

        reason = $"エンドポイントのスキームが {uri.Scheme}: です。http(ループバックのみ)か https のURLを指定してください。";
        return false;
    }

    /// <summary>
    /// 送信先を人に見せる形にする。**APIキーが混ざらないよう、ホストとスキームだけ**にする
    /// (パスにモデル名や配置名が入ることがあり、クエリには鍵が載りうるため)。
    /// 読めないURLはそのまま返さず、読めなかったことだけを返す。
    /// </summary>
    public static string Describe(string? endpoint)
    {
        var trimmed = endpoint?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return "(未設定)";
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? $"{uri.Scheme}://{uri.Host}"
            : "(URLとして読めません)";
    }
}
