namespace TextPolish.Core.Automation;

/// <summary>テキスト挿入の結果。中止の理由を呼び出し元で出し分けるために区別する。</summary>
public enum TextInsertResult
{
    /// <summary>挿入した。</summary>
    Inserted,

    /// <summary>挿入する文字列が空だった。</summary>
    EmptyText,

    /// <summary>クリップボードへ書き込めなかった。</summary>
    ClipboardFailed,

    /// <summary>
    /// 貼り付け先が取得元のウィンドウでなくなっていたため中止した。
    /// クリップボードは元の内容に戻してあり、文章はどこにも貼り付けていない。
    /// </summary>
    TargetWindowChanged,

    /// <summary>Ctrl+V を送出できなかった。</summary>
    SendFailed,
}

public interface ITextInserter
{
    /// <summary>
    /// アクティブウィンドウのカーソル位置にテキストを挿入する。
    /// </summary>
    /// <param name="text">挿入する文字列。</param>
    /// <param name="expectedWindow">
    /// 貼り付け先として期待するウィンドウ(選択テキストを取得したウィンドウ)。
    /// Ctrl+V の送出直前に前面ウィンドウと照合し、違っていれば中止する。
    /// IntPtr.Zero を渡すと照合しない。
    /// </param>
    Task<TextInsertResult> InsertAsync(
        string text, IntPtr expectedWindow = default, CancellationToken ct = default);
}
