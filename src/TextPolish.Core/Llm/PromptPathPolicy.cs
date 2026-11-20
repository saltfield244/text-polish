using System.IO;

namespace TextPolish.Core.Llm;

/// <summary>システムプロンプト・用語辞書のファイルが、どこに置かれているか。</summary>
public enum PromptPathLocation
{
    /// <summary>設定が空。既定のプロンプトで動く(辞書なら辞書なし)。</summary>
    NotSet,

    /// <summary>アプリのフォルダ配下。想定どおりの置き場所。</summary>
    InsideAppFolder,

    /// <summary>同じPCの中だが、アプリのフォルダの外。</summary>
    OutsideAppFolder,

    /// <summary>ネットワーク上の共有フォルダ(UNC)。</summary>
    NetworkShare,

    /// <summary>パスとして解釈できない。</summary>
    Unreadable,
}

/// <summary>
/// システムプロンプトと用語辞書のファイルパスの扱い。
///
/// この2つのファイルの中身は、**全文がそのままシステムプロンプトとしてLLMへ送られる**。
/// つまり「読み込むファイルを差し替えられること」は「任意のファイルの中身を外へ送らせること」
/// と同じ意味を持つ。<c>appsettings.json</c> は手で編集できるので、
/// 気づかないうちに(あるいは誰かに書き換えられて)別の場所を指しうる。
///
/// **それでも配下限定にはしない。** 用語辞書をチームの共有フォルダに置き、
/// 複数のPCで同じ辞書を使う運用があり得る。パスを塞ぐとそれを黙って壊す。
/// 設定ファイルを書ける立場が前提の話でもあるため、ここでは
/// **起動時に置き場所をログへ出し、アプリのフォルダの外なら警告する**までにとどめる。
/// 何を読んでいるか後から追えるようにするのが目的で、止めることが目的ではない。
///
/// パスの解決もここに置く。呼ぶ側が <see cref="Path.Combine"/> を書くと、
/// 判定する側と解決する側で結果がずれる(<c>..</c> の扱いなど)ため。
/// </summary>
public static class PromptPathPolicy
{
    /// <summary>
    /// 相対パスをアプリのフォルダ基準で絶対パスにする。<c>..</c> も畳む
    /// (畳まないと「配下かどうか」を文字列で判定できない)。
    /// 解釈できないパスは、そのまま返して後続のファイル操作に失敗させる。
    /// </summary>
    public static string Resolve(string path, string baseDirectory)
    {
        try
        {
            var combined = Path.IsPathRooted(path) ? path : Path.Combine(baseDirectory, path);
            return Path.GetFullPath(combined);
        }
        catch (Exception)
        {
            // ArgumentException(不正な文字)・PathTooLongException・NotSupportedException。
            // ここで投げると起動できなくなるので、判定は Classify に任せる。
            return path;
        }
    }

    /// <summary>設定値から置き場所を判定する。<paramref name="resolved"/> に解決後のパスが入る。</summary>
    public static PromptPathLocation Classify(string? path, string baseDirectory, out string resolved)
    {
        var trimmed = path?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            resolved = string.Empty;
            return PromptPathLocation.NotSet;
        }

        string full;
        string baseFull;

        try
        {
            var combined = Path.IsPathRooted(trimmed) ? trimmed : Path.Combine(baseDirectory, trimmed);
            full = Path.GetFullPath(combined);
            baseFull = Path.GetFullPath(baseDirectory);
        }
        catch (Exception)
        {
            resolved = trimmed;
            return PromptPathLocation.Unreadable;
        }

        resolved = full;

        // `\\server\share\...` はネットワーク上。`\\?\` のようなデバイス指定も同じ扱いにする
        // (想定していない書き方なので、警告が出るほうが望ましい)。
        // ネットワークドライブに割り当てた `Z:\` は見分けられない。ここで拾えるのは
        // UNC で直接書かれた場合だけで、それが共有フォルダ運用のふつうの書き方。
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return PromptPathLocation.NetworkShare;
        }

        // 末尾の区切りを付けてから比べる。付けないと `C:\App` の判定に
        // `C:\AppOther\...` が引っかかる。Windows のパスは大文字小文字を区別しない。
        var prefix = baseFull.EndsWith(Path.DirectorySeparatorChar)
            ? baseFull
            : baseFull + Path.DirectorySeparatorChar;

        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? PromptPathLocation.InsideAppFolder
            : PromptPathLocation.OutsideAppFolder;
    }

    /// <summary>
    /// ログに出す1行を作る。<paramref name="label"/> は「システムプロンプト」など設定の呼び名。
    /// 警告として出すべきかどうかは <paramref name="warn"/> で返す
    /// (配下にあるふつうの場合まで WARN にすると、本物の警告が埋もれる)。
    /// </summary>
    public static string Describe(string label, string? path, string baseDirectory, out bool warn)
    {
        var location = Classify(path, baseDirectory, out var resolved);

        switch (location)
        {
            case PromptPathLocation.NotSet:
                warn = false;
                return $"{label}のファイルは未設定です。";

            case PromptPathLocation.InsideAppFolder:
                warn = false;
                return $"{label}のファイル: {resolved}";

            case PromptPathLocation.OutsideAppFolder:
                warn = true;
                return
                    $"{label}のファイルがアプリのフォルダの外にあります: {resolved}。" +
                    "このファイルの中身は全文がLLMへ送られます。意図した置き場所か確認してください。";

            case PromptPathLocation.NetworkShare:
                warn = true;
                return
                    $"{label}のファイルがネットワーク上にあります: {resolved}。" +
                    "このファイルの中身は全文がLLMへ送られます。" +
                    "共有フォルダなら、書き換えられる相手が増えることになります。";

            default:
                warn = true;
                return $"{label}のファイルパスを解釈できません: {resolved}。既定の動作になります。";
        }
    }
}
