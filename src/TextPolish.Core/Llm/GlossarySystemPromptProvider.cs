using System.IO;
using System.Text;
using TextPolish.Core.Logging;

namespace TextPolish.Core.Llm;

/// <summary>用語辞書の1エントリ。「誤変換されがちな表記 → 正しい表記」の対応。</summary>
public sealed record GlossaryEntry(string Wrong, string Correct);

/// <summary>
/// 別ファイルの用語辞書を読み込み、基となるシステムプロンプトの末尾に追記する。
///
/// 辞書を <see cref="LlmFormatRequest"/> ではなくシステムプロンプト側へ入れているのは、
/// 全リクエストで共通の固定内容だからで、プロバイダ側のプロンプトキャッシュにも乗りやすい。
///
/// 設定ファイル(appsettings.json)ではなく独立したテキストファイルにしているのは、
/// 設定画面から保存すると appsettings.json が丸ごと書き換わるため。
/// 用語は増え続けて手編集する前提なので、書き換えの巻き添えを避けている。
/// </summary>
public sealed class GlossarySystemPromptProvider : ISystemPromptProvider
{
    private readonly ISystemPromptProvider _inner;
    private readonly string _glossaryFilePath;
    private readonly IAppLogger _logger;

    public GlossarySystemPromptProvider(ISystemPromptProvider inner, string glossaryFilePath, IAppLogger logger)
    {
        _inner = inner;
        _glossaryFilePath = glossaryFilePath;
        _logger = logger;
    }

    public async Task<string> GetSystemPromptAsync(CancellationToken ct = default)
    {
        var basePrompt = await _inner.GetSystemPromptAsync(ct);
        var entries = await LoadAsync(ct);

        if (entries.Count == 0)
        {
            return basePrompt;
        }

        var sb = new StringBuilder(basePrompt);
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("# 用語辞書");
        sb.AppendLine("音声入力で誤変換されやすい表記と、正しい表記の対応です。");
        sb.AppendLine("左側の表記が現れた場合は、文脈から明らかに別の語だと判断できない限り右側に直してください。");
        sb.AppendLine("辞書にない固有名詞は、これまでどおり、判断に迷うなら変更しないこと。");

        foreach (var entry in entries)
        {
            sb.AppendLine($"- {entry.Wrong} → {entry.Correct}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 辞書ファイルを読み込む。ファイルがない場合や1行も解釈できない場合は空を返し、
    /// 辞書なしとして整形を続行する(辞書は補助であって必須ではないため)。
    /// </summary>
    private async Task<List<GlossaryEntry>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_glossaryFilePath))
        {
            return new List<GlossaryEntry>();
        }

        try
        {
            var lines = await File.ReadAllLinesAsync(_glossaryFilePath, ct);
            var entries = new List<GlossaryEntry>();

            foreach (var line in lines)
            {
                if (TryParseLine(line, out var entry))
                {
                    entries.Add(entry);
                }
            }

            return entries;
        }
        catch (Exception ex)
        {
            _logger.Warn($"用語辞書の読み込みに失敗しました。辞書なしで整形します: {ex.Message}");
            return new List<GlossaryEntry>();
        }
    }

    /// <summary>
    /// 「誤 → 正」形式の1行を解釈する。'#' で始まる行と空行は無視する。
    /// 区切りは全角矢印「→」と半角の「-&gt;」の両方を受け付ける(手入力での揺れを吸収するため)。
    /// </summary>
    private static bool TryParseLine(string line, out GlossaryEntry entry)
    {
        entry = null!;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
        {
            return false;
        }

        var separatorIndex = trimmed.IndexOf('→');
        var separatorLength = 1;

        if (separatorIndex < 0)
        {
            separatorIndex = trimmed.IndexOf("->", StringComparison.Ordinal);
            separatorLength = 2;
        }

        if (separatorIndex < 0)
        {
            return false;
        }

        var wrong = trimmed[..separatorIndex].Trim();
        var correct = trimmed[(separatorIndex + separatorLength)..].Trim();

        if (wrong.Length == 0 || correct.Length == 0)
        {
            return false;
        }

        entry = new GlossaryEntry(wrong, correct);
        return true;
    }
}
