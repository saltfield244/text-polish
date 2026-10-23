namespace TextPolish.Core.Automation;

/// <summary>Ctrl+V を送ってよいかどうかの判定結果。</summary>
public enum PasteTargetCheck
{
    /// <summary>取得元のウィンドウが前面にある。送出してよい。</summary>
    Ok,

    /// <summary>取得元のウィンドウが分からないため検証していない。</summary>
    NotVerified,

    /// <summary>取得元のウィンドウが既に存在しない(閉じられた)。</summary>
    WindowGone,

    /// <summary>別のウィンドウが前面にある。</summary>
    Mismatch,
}

/// <summary>
/// 整形結果の貼り付け先が、選択テキストを取得したウィンドウのままかを確かめる。
///
/// 書き戻しは Ctrl+V の送出で行うため、送出した瞬間に前面にあるウィンドウへ貼り付く。
/// 整形には数秒かかり、その間に利用者が別のアプリへ移ることもフォーカス復帰が
/// 失敗することもあるため、送出直前に前面ウィンドウを確かめないと、
/// まったく関係のないウィンドウ(チャット欄など)へ文章を流し込んでしまう。
///
/// Win32呼び出しから切り離した純粋な判定にしているのは、デスクトップのない環境でも
/// 組み合わせを回帰テストで固定できるようにするため。
/// </summary>
public static class PasteTarget
{
    /// <param name="expected">選択テキストを取得したウィンドウ。IntPtr.Zero なら検証しない。</param>
    /// <param name="actual">送出直前に前面にあるウィンドウ。</param>
    /// <param name="expectedStillExists">取得元のウィンドウがまだ存在するか(IsWindow)。</param>
    public static PasteTargetCheck Check(IntPtr expected, IntPtr actual, bool expectedStillExists)
    {
        if (expected == IntPtr.Zero)
        {
            return PasteTargetCheck.NotVerified;
        }

        if (!expectedStillExists)
        {
            return PasteTargetCheck.WindowGone;
        }

        return expected == actual ? PasteTargetCheck.Ok : PasteTargetCheck.Mismatch;
    }
}
