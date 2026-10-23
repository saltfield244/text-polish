using TextPolish.Core.Llm;

namespace TextPolish.Tests;

/// <summary>
/// LLMへ何を送るか(<see cref="LlmPromptBuilder"/>)。
///
/// 選択していない周辺の文章が送られるかどうかはプライバシーに関わるため、
/// 設定でオフにしたときに本当にプロンプトへ入らないことをここで固定する。
/// アプリ名による文体切り替えは、その設定に関わらず機能し続ける必要がある。
/// </summary>
internal static class PromptTests
{
    private const string Selection = "整形したい文章";
    private const string Surrounding = "選択していない周辺の文章";

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("プロンプト: 周辺文脈を渡すとプロンプトに入る", SurroundingContextIsIncluded);
        yield return ("プロンプト: 周辺文脈を渡さなければプロンプトに入らない", SurroundingContextIsOmitted);
        yield return ("プロンプト: 空の周辺文脈は見出しごと出さない", BlankSurroundingContextIsOmitted);
        yield return ("プロンプト: 周辺文脈なしでもアプリ名と文体指示は残る", AppNameAndStyleSurviveWithoutContext);
        yield return ("プロンプト: 整形の指示と原文は常に入る", InstructionsAndRawTextAlwaysIncluded);
    }

    private static void SurroundingContextIsIncluded()
    {
        var prompt = LlmPromptBuilder.BuildUserPrompt(new LlmFormatRequest
        {
            RawText = Selection,
            ActiveAppName = "notepad",
            SurroundingContextText = Surrounding,
        });

        Assert.True(prompt.Contains("[カーソル周辺の既存テキスト"), "周辺文脈の見出しがある");
        Assert.True(prompt.Contains(Surrounding), "周辺文脈の本文がある");
    }

    private static void SurroundingContextIsOmitted()
    {
        // 設定がオフのとき、呼び出し元は null を渡す(取得自体もしない)。
        var prompt = LlmPromptBuilder.BuildUserPrompt(new LlmFormatRequest
        {
            RawText = Selection,
            ActiveAppName = "notepad",
            SurroundingContextText = null,
        });

        Assert.True(prompt.Contains("カーソル周辺") == false, "周辺文脈の見出しがない");
        Assert.True(prompt.Contains(Surrounding) == false, "周辺の文章が含まれない");
        Assert.True(prompt.Contains(Selection), "選択した文章は送る");
    }

    private static void BlankSurroundingContextIsOmitted()
    {
        var prompt = LlmPromptBuilder.BuildUserPrompt(new LlmFormatRequest
        {
            RawText = Selection,
            SurroundingContextText = "   \r\n ",
        });

        Assert.True(prompt.Contains("カーソル周辺") == false, "空白だけなら見出しも出さない");
    }

    private static void AppNameAndStyleSurviveWithoutContext()
    {
        // 周辺文脈を送らなくても、アプリ名による文体切り替えは効き続ける必要がある。
        var prompt = LlmPromptBuilder.BuildUserPrompt(new LlmFormatRequest
        {
            RawText = Selection,
            ActiveAppName = "OUTLOOK",
            StyleInstruction = "ですます調にする",
            SurroundingContextText = null,
        });

        Assert.True(prompt.Contains("[アクティブアプリ: OUTLOOK]"), "アプリ名を送る");
        Assert.True(prompt.Contains("ですます調にする"), "文体指示を送る");
        Assert.True(prompt.Contains("カーソル周辺") == false, "周辺文脈は送らない");
    }

    private static void InstructionsAndRawTextAlwaysIncluded()
    {
        var prompt = LlmPromptBuilder.BuildUserPrompt(new LlmFormatRequest
        {
            RawText = Selection,
            TaskInstructions = new[] { "誤変換だけ直す", "英語に翻訳する" },
            SurroundingContextText = null,
        });

        Assert.True(prompt.Contains("1. 誤変換だけ直す"), "1番目の指示が手順として入る");
        Assert.True(prompt.Contains("2. 英語に翻訳する"), "2番目の指示が手順として入る");
        Assert.True(prompt.Contains("[書き起こしテキスト]"), "原文の見出しがある");
        Assert.True(prompt.TrimEnd().EndsWith(Selection), "原文が末尾に入る");
    }
}
