namespace TextPolish.Core.Llm;

/// <summary>音声認識の生テキストをLLMで整形するための抽象。プロバイダ実装を差し替え可能にする。</summary>
public interface ILlmFormatter
{
    Task<string> FormatAsync(LlmFormatRequest request, CancellationToken ct = default);
}

public sealed class LlmFormatRequest
{
    public required string RawText { get; init; }

    /// <summary>Phase5: アクティブウィンドウのアプリ名(取得できない場合はnull)。</summary>
    public string? ActiveAppName { get; init; }

    /// <summary>Phase5: カーソル周辺の既存テキスト(取得できない場合はnull)。</summary>
    public string? SurroundingContextText { get; init; }

    /// <summary>
    /// アクティブなアプリに応じた文体の指示。共通のシステムプロンプトを上書きする形で使う。
    /// </summary>
    public string? StyleInstruction { get; init; }

    /// <summary>
    /// 整形パレットで選んだプリセットと、その場で入力した自由指示。
    /// 指定された場合はこれが整形タスクの主たる指示になる。
    ///
    /// 複数指定された場合は「上から順に適用する手順」として扱う。
    /// 順序に意味があるため、呼び出し側は適用したい順に並べて渡すこと。
    /// </summary>
    public IReadOnlyList<string>? TaskInstructions { get; init; }
}
