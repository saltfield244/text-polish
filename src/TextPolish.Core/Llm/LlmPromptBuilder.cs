using System.Text;

namespace TextPolish.Core.Llm;

/// <summary>
/// LLMへ渡すユーザープロンプトの組み立て。
/// 何がLLMへ送られるかはプライバシー上も重要なので、純粋な関数にして回帰テストで固定している。
/// </summary>
public static class LlmPromptBuilder
{
    public static string BuildUserPrompt(LlmFormatRequest request)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(request.ActiveAppName))
        {
            sb.AppendLine($"[アクティブアプリ: {request.ActiveAppName}]");
        }

        // 整形パレットで明示的に選ばれた指示。文体指示より優先される主タスク。
        var instructions = request.TaskInstructions?
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .ToList() ?? new List<string>();

        if (instructions.Count == 1)
        {
            sb.AppendLine("[整形の指示(最優先。これに従うこと)]");
            sb.AppendLine(instructions[0]);
            sb.AppendLine("[/整形の指示]");
        }
        else if (instructions.Count > 1)
        {
            // 複数選択時は手順として番号を振る。各指示は単独実行前提の文言なので、
            // 「前の手順の結果に適用する」ことを明示しないと、2段目以降が
            // 前段の出力ではなく原文を対象にしてしまう。
            sb.AppendLine("[整形の指示(最優先。これに従うこと)]");
            sb.AppendLine("次の手順を、上から順に適用してください。");
            sb.AppendLine("手順2以降は、直前の手順の結果に対して適用します。");
            sb.AppendLine("出力するのは最後の手順まで終えた最終結果だけで、途中経過は出力しないこと。");

            for (var i = 0; i < instructions.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {instructions[i]}");
            }

            sb.AppendLine("[/整形の指示]");
        }

        // 文体はアプリごとに切り替わるため、共通のシステムプロンプトより後に置いて
        // こちらを優先させる。
        if (!string.IsNullOrWhiteSpace(request.StyleInstruction))
        {
            sb.AppendLine("[このアプリでの文体指示(システムプロンプトの文体方針より優先)]");
            sb.AppendLine(request.StyleInstruction);
            sb.AppendLine("[/文体指示]");
        }

        if (!string.IsNullOrWhiteSpace(request.SurroundingContextText))
        {
            sb.AppendLine("[カーソル周辺の既存テキスト(参考情報。書き起こし文には含めないこと)]");
            sb.AppendLine(request.SurroundingContextText);
            sb.AppendLine("[/カーソル周辺の既存テキスト]");
        }

        sb.AppendLine("[書き起こしテキスト]");
        sb.Append(request.RawText);

        return sb.ToString();
    }
}
