using TextPolish.Core.Automation;

namespace TextPolish.Tests;

/// <summary>
/// 書き戻し先の検証(<see cref="PasteTarget"/> / <see cref="WindowFocus"/>)。
///
/// Ctrl+V は送出した瞬間に前面にあるウィンドウへ届く。整形には数秒かかるため、
/// その間に利用者が別のアプリへ移っていたり、フォーカス復帰が効いていなかったりすると、
/// 関係のないウィンドウへ文章を流し込んでしまう。判定の組み合わせを固定する。
///
/// 実際のフォーカス移動は実機確認(E)で見る。ここでは他アプリに触らない範囲だけを扱うため、
/// <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class PasteTargetTests
{
    /// <summary>存在しないウィンドウハンドル。IsWindow が false になる値。</summary>
    private static readonly IntPtr Source = new(0x1234);
    private static readonly IntPtr Other = new(0x5678);

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("書き戻し先: 取得元が前面なら送出する", SameWindowIsOk);
        yield return ("書き戻し先: 別のウィンドウが前面なら中止する", DifferentWindowIsMismatch);
        yield return ("書き戻し先: 取得元が閉じられていれば中止する", ClosedWindowIsGone);
        yield return ("書き戻し先: 取得元が不明なら検証しない", ZeroHandleIsNotVerified);
        yield return ("フォーカス復帰: ハンドルなしは失敗する", RestoreWithoutHandleFails);
        yield return ("フォーカス復帰: 存在しないウィンドウは失敗する", RestoreOfMissingWindowFails);
        yield return ("フォーカス復帰: 存在判定はハンドルなしをfalseにする", ExistsRejectsZero);
    }

    private static void SameWindowIsOk() =>
        // CIが失敗を検出できることを確かめるための、意図的に誤った期待値。
        // このコミットは直後に revert する。
        Assert.Equal(
            PasteTargetCheck.Mismatch,
            PasteTarget.Check(Source, Source, expectedStillExists: true),
            "取得元が前面にある場合");

    private static void DifferentWindowIsMismatch() =>
        Assert.Equal(
            PasteTargetCheck.Mismatch,
            PasteTarget.Check(Source, Other, expectedStillExists: true),
            "別のウィンドウが前面にある場合");

    private static void ClosedWindowIsGone()
    {
        // 閉じられたウィンドウのハンドルは再利用され得る。前面が偶然一致しても送ってはいけない。
        Assert.Equal(
            PasteTargetCheck.WindowGone,
            PasteTarget.Check(Source, Source, expectedStillExists: false),
            "取得元が閉じられていて前面が一致する場合");

        Assert.Equal(
            PasteTargetCheck.WindowGone,
            PasteTarget.Check(Source, Other, expectedStillExists: false),
            "取得元が閉じられている場合");
    }

    private static void ZeroHandleIsNotVerified() =>
        Assert.Equal(
            PasteTargetCheck.NotVerified,
            PasteTarget.Check(IntPtr.Zero, Other, expectedStillExists: true),
            "取得元が分からない場合");

    private static void RestoreWithoutHandleFails() =>
        Assert.True(
            WindowFocus.RestoreAsync(IntPtr.Zero).GetAwaiter().GetResult() == false,
            "ハンドルがなければフォーカスは戻せない");

    private static void RestoreOfMissingWindowFails()
    {
        // 存在しないウィンドウは、待たずに(SetForegroundWindowも呼ばずに)失敗する。
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var restored = WindowFocus.RestoreAsync(Source).GetAwaiter().GetResult();
        elapsed.Stop();

        Assert.True(restored == false, "存在しないウィンドウへは戻せない");
        Assert.True(
            elapsed.ElapsedMilliseconds < WindowFocus.DefaultRestoreTimeoutMs,
            $"待ち時間を使い切らずに失敗する(実測 {elapsed.ElapsedMilliseconds}ms)");
    }

    private static void ExistsRejectsZero() =>
        Assert.True(WindowFocus.Exists(IntPtr.Zero) == false, "ハンドルなしは存在しない扱い");
}
