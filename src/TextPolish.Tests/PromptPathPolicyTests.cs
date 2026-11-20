using System.IO;
using TextPolish.Core.Llm;

namespace TextPolish.Tests;

/// <summary>
/// システムプロンプト・用語辞書のパスの判定(<see cref="PromptPathPolicy"/>)。
///
/// この2つのファイルの中身は**全文がそのままシステムプロンプトとしてLLMへ送られる**。
/// 読み込み先を差し替えられることは、任意のファイルの中身を外へ送らせることと同じ意味を持つ。
/// <c>appsettings.json</c> は手で編集できるため、気づかないうちに別の場所を指しうる。
///
/// ここで固定したいのは「どこを指しているか」の判定だけで、**止める動作は入れていない**。
/// 用語辞書を共有フォルダに置く運用があり、塞ぐとそれを黙って壊すため、
/// 起動時にログへ出して、アプリのフォルダの外なら警告するところまでにしている。
///
/// 特に外しやすいのは次の3つ。
///
///   ・相対パスでも <c>..</c> で配下を抜けられる(相対だから安全、ではない)
///   ・末尾の区切りを付けずに文字列で比べると、<c>C:\App2</c> が <c>C:\App</c> の配下に見える
///   ・警告を出しすぎると本物の警告が埋もれる(配下のふつうの場合は INFO)
///
/// ファイルの読み書きもネットワークもデスクトップも使わないため <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class PromptPathPolicyTests
{
    private const string BaseDir = @"C:\Program Files\TextPolish";

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("プロンプトのパス: 配下の相対パスは配下と判定する", RelativeIsInside);
        yield return ("プロンプトのパス: 配下の絶対パスは配下と判定する", AbsoluteInsideIsInside);
        yield return ("プロンプトのパス: `..` で抜ける相対パスは外と判定する", DotDotEscapesTheFolder);
        yield return ("プロンプトのパス: 別フォルダの絶対パスは外と判定する", AbsoluteOutsideIsOutside);
        yield return ("プロンプトのパス: 名前が前方一致する別フォルダを配下と誤判定しない", SiblingWithSharedPrefixIsOutside);
        yield return ("プロンプトのパス: 大文字小文字の違いは配下の判定を変えない", CaseDoesNotMatter);
        yield return ("プロンプトのパス: UNC はネットワーク上と判定する", UncIsNetworkShare);
        yield return ("プロンプトのパス: 空なら未設定と判定する", EmptyIsNotSet);
        yield return ("プロンプトのパス: 解釈できないパスはそれと分かる", GarbageIsUnreadable);
        yield return ("プロンプトのパス: 解決結果はアプリ側と同じ(相対は基準フォルダから、`..` は畳む)", ResolveMatchesClassify);
        yield return ("プロンプトのパス: 配下なら警告にしない", InsideIsNotWarned);
        yield return ("プロンプトのパス: 外・ネットワーク・解釈不能は警告にする", OutsideIsWarned);
        yield return ("プロンプトのパス: ログの文面に解決後のフルパスを出す", DescribeShowsResolvedPath);
    }

    private static void RelativeIsInside()
    {
        var location = PromptPathPolicy.Classify("PolishSystemPrompt.txt", BaseDir, out var resolved);

        Assert.Equal(PromptPathLocation.InsideAppFolder, location, "既定の相対パスの判定");
        Assert.Equal(Path.Combine(BaseDir, "PolishSystemPrompt.txt"), resolved, "解決後のパス");
    }

    private static void AbsoluteInsideIsInside()
    {
        var location = PromptPathPolicy.Classify(
            Path.Combine(BaseDir, "prompts", "Glossary.txt"), BaseDir, out _);

        Assert.Equal(PromptPathLocation.InsideAppFolder, location, "配下の絶対パスの判定");
    }

    private static void DotDotEscapesTheFolder()
    {
        // 相対パスでも配下から出られる。ここを配下扱いにすると、
        // 「相対パスなら安全」という誤解のまま任意のファイルを読める。
        var location = PromptPathPolicy.Classify(@"..\..\Users\someone\秘密.txt", BaseDir, out var resolved);

        Assert.Equal(PromptPathLocation.OutsideAppFolder, location, "`..` を含む相対パスの判定");
        Assert.True(
            resolved.Contains(@"\Users\someone\", StringComparison.OrdinalIgnoreCase),
            $"`..` が畳まれていない: {resolved}");
    }

    private static void AbsoluteOutsideIsOutside()
    {
        var location = PromptPathPolicy.Classify(@"C:\Users\someone\Desktop\メモ.txt", BaseDir, out _);

        Assert.Equal(PromptPathLocation.OutsideAppFolder, location, "外の絶対パスの判定");
    }

    private static void SiblingWithSharedPrefixIsOutside()
    {
        // 末尾の区切りを付けずに文字列で比べると、ここが配下に見えてしまう。
        var location = PromptPathPolicy.Classify(BaseDir + @"2\Glossary.txt", BaseDir, out _);

        Assert.Equal(PromptPathLocation.OutsideAppFolder, location, "前方一致する別フォルダの判定");
    }

    private static void CaseDoesNotMatter()
    {
        var location = PromptPathPolicy.Classify(
            @"c:\program files\textpolish\Glossary.txt", BaseDir, out _);

        Assert.Equal(PromptPathLocation.InsideAppFolder, location, "大文字小文字が違う配下のパスの判定");
    }

    private static void UncIsNetworkShare()
    {
        var location = PromptPathPolicy.Classify(@"\\fileserver\共有\用語辞書.txt", BaseDir, out var resolved);

        Assert.Equal(PromptPathLocation.NetworkShare, location, "UNC の判定");
        Assert.Equal(@"\\fileserver\共有\用語辞書.txt", resolved, "解決後のパス");
    }

    private static void EmptyIsNotSet()
    {
        foreach (var path in new[] { null, string.Empty, "   " })
        {
            Assert.Equal(
                PromptPathLocation.NotSet,
                PromptPathPolicy.Classify(path, BaseDir, out _),
                $"空のパス({path ?? "null"})の判定");
        }
    }

    private static void GarbageIsUnreadable()
    {
        // パスに NUL は入れられない。起動時に例外で落ちるのではなく、
        // 「解釈できない」と分かる形で返ってくることを見る。
        var location = PromptPathPolicy.Classify("C:\\prompt\u0000.txt", BaseDir, out _);

        Assert.Equal(PromptPathLocation.Unreadable, location, "解釈できないパスの判定");
    }

    private static void ResolveMatchesClassify()
    {
        // 判定する側と解決する側で結果がずれると、警告は出ないのに別のファイルを読む、
        // あるいはその逆が起きる。同じ関数を通していることを見る。
        foreach (var path in new[]
        {
            "Glossary.txt",
            @"sub\Glossary.txt",
            @"..\外.txt",
            @"C:\Users\someone\メモ.txt",
        })
        {
            PromptPathPolicy.Classify(path, BaseDir, out var classified);
            Assert.Equal(classified, PromptPathPolicy.Resolve(path, BaseDir), $"{path} の解決結果");
        }

        Assert.Equal(
            Path.Combine(BaseDir, "Glossary.txt"),
            PromptPathPolicy.Resolve("Glossary.txt", BaseDir),
            "相対パスは基準フォルダから解決する");
    }

    private static void InsideIsNotWarned()
    {
        // 配下のふつうの場合まで WARN にすると、本物の警告が埋もれる。
        PromptPathPolicy.Describe("システムプロンプト", "PolishSystemPrompt.txt", BaseDir, out var warnInside);
        PromptPathPolicy.Describe("用語辞書", null, BaseDir, out var warnNotSet);

        Assert.True(!warnInside, "配下のパスを警告にしている");
        Assert.True(!warnNotSet, "未設定を警告にしている");
    }

    private static void OutsideIsWarned()
    {
        foreach (var path in new[]
        {
            @"C:\Users\someone\メモ.txt",
            @"..\外.txt",
            @"\\fileserver\共有\用語辞書.txt",
            "C:\\prompt\u0000.txt",
        })
        {
            PromptPathPolicy.Describe("用語辞書", path, BaseDir, out var warn);
            Assert.True(warn, $"{path} を警告にしていない");
        }
    }

    private static void DescribeShowsResolvedPath()
    {
        // 「何を読んでいたか」を後からログで追えるようにするのが目的なので、
        // 設定に書かれた文字列ではなく解決後のパスが出ていないと意味がない。
        var message = PromptPathPolicy.Describe("用語辞書", @"..\共有\辞書.txt", BaseDir, out _);

        Assert.True(message.Contains("用語辞書", StringComparison.Ordinal), $"設定の呼び名が無い: {message}");
        Assert.True(
            message.Contains(@"C:\Program Files\共有\辞書.txt", StringComparison.OrdinalIgnoreCase),
            $"解決後のフルパスが無い: {message}");
    }
}
