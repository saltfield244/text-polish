using System.IO;

namespace TextPolish.Core.Llm;

public interface ISystemPromptProvider
{
    Task<string> GetSystemPromptAsync(CancellationToken ct = default);
}

/// <summary>
/// 整形方針のシステムプロンプトを外部テキストファイルから読み込む。プロバイダ間で共通利用する。
/// ファイルが見つからない場合は最低限のフォールバックプロンプトを返す。
/// </summary>
public sealed class FileSystemPromptProvider : ISystemPromptProvider
{
    private const string FallbackPrompt =
        "あなたは音声入力の書き起こしテキストを整形するアシスタントです。" +
        "フィラー(えーと、あの、など)を除去し、適切な句読点を補い、意味を変えずに読みやすい文章にしてください。" +
        "整形後のテキストのみを出力し、説明や前置きは付けないでください。";

    private readonly string _filePath;

    public FileSystemPromptProvider(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<string> GetSystemPromptAsync(CancellationToken ct = default)
    {
        if (!File.Exists(_filePath))
        {
            return FallbackPrompt;
        }

        var text = await File.ReadAllTextAsync(_filePath, ct);
        return string.IsNullOrWhiteSpace(text) ? FallbackPrompt : text;
    }
}
