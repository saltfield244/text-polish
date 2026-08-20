namespace TextPolish.Core.Automation;

public sealed class ActiveWindowContext
{
    public string? AppName { get; init; }
    public string? SurroundingText { get; init; }
}

/// <summary>Phase 5: アクティブウィンドウのアプリ名・カーソル周辺テキストを取得する。</summary>
public interface IActiveWindowContextProvider
{
    ActiveWindowContext GetContext();
}
