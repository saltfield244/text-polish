namespace TextPolish.Core.Automation;

public interface ITextInserter
{
    /// <summary>アクティブウィンドウのカーソル位置にテキストを挿入する。成功可否を返す。</summary>
    Task<bool> InsertAsync(string text, CancellationToken ct = default);
}
