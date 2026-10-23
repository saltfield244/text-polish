namespace TextPolish.Core.Automation;

public sealed class ActiveWindowContext
{
    public string? AppName { get; init; }
    public string? SurroundingText { get; init; }
}

/// <summary>Phase 5: アクティブウィンドウのアプリ名・カーソル周辺テキストを取得する。</summary>
public interface IActiveWindowContextProvider
{
    /// <param name="includeSurroundingText">
    /// カーソル周辺の未選択テキストも取得するか。false のときは
    /// <see cref="ActiveWindowContext.SurroundingText"/> が常に null になり、
    /// UI Automation でのテキスト読み出しも行わない。アプリ名の取得には影響しない。
    /// </param>
    ActiveWindowContext GetContext(bool includeSurroundingText);
}
