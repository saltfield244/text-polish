using TextPolish.Core.Polish;

namespace TextPolish.Tests;

/// <summary>
/// 整形パレットの結果の状態(<see cref="PolishResultState"/>)。
///
/// パレットは同じウィンドウで何度でも再整形できる。1回目が成功したあと2回目が失敗・
/// タイムアウト・中止になったときに、前回の結果を今回の結果として書き戻せてしまわないかを確かめる。
/// WPFを使わない純粋な状態遷移なので <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class PolishResultTests
{
    public static IEnumerable<(string, Action)> All()
    {
        yield return ("パレット結果: 整形前は書き戻せない", NothingToApplyBeforeFirstRun);
        yield return ("パレット結果: 正常終了すると書き戻せる", SuccessAllowsApply);
        yield return ("パレット結果: 整形中は書き戻せない", RunningBlocksApply);
        yield return ("パレット結果: 再整形の失敗で前回の結果を書き戻せない", FailedRerunBlocksStaleResult);
        yield return ("パレット結果: タイムアウトでも前回の結果を書き戻せない", TimeoutBlocksStaleResult);
        yield return ("パレット結果: 中止でも前回の結果を書き戻せない", CancellationBlocksStaleResult);
        yield return ("パレット結果: 失敗後に再試行が成功すれば書き戻せる", RetryAfterFailureAllowsApply);
        yield return ("パレット結果: 失敗しても前回の結果は読み返せる", StaleResultStaysVisible);
        yield return ("パレット結果: 成功後の手直しは書き戻せる", EditAfterSuccessCanApply);
        yield return ("パレット結果: 前回の結果を手直ししても書き戻せない", EditOfStaleResultCannotApply);
        yield return ("パレット結果: 空にすると書き戻せない", ClearedResultCannotApply);
        yield return ("パレット結果: 空の結果は成功として扱わない", EmptySuccessIsNotApplicable);
    }

    private static void NothingToApplyBeforeFirstRun()
    {
        var state = new PolishResultState();

        Assert.True(state.CanApply == false, "整形前は書き戻せない");
        Assert.True(state.HasStaleText == false, "前回の結果もない");
        Assert.Equal(string.Empty, state.Text, "結果欄の内容");
    }

    private static void SuccessAllowsApply()
    {
        var state = new PolishResultState();

        state.BeginRun();
        state.SetResult("整えた文章");

        Assert.True(state.CanApply, "正常終了後は書き戻せる");
        Assert.Equal("整えた文章", state.Text, "結果欄の内容");
        Assert.True(state.HasStaleText == false, "今回の結果なので前回扱いにならない");
    }

    private static void RunningBlocksApply()
    {
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");

        // 2回目の整形を開始した時点で、表示中の結果は前回のものになる。
        state.BeginRun();

        Assert.True(state.CanApply == false, "整形中は書き戻せない");
        Assert.True(state.HasStaleText, "表示中の内容は前回の結果");
    }

    private static void FailedRerunBlocksStaleResult()
    {
        // 「整える」で成功 → 「英訳」で失敗。英訳されていない前回の結果を適用してはいけない。
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");

        state.BeginRun();
        // 失敗した場合、新しい結果は設定されない。

        Assert.True(state.CanApply == false, "失敗した整形の結果は書き戻せない");
        Assert.Equal("整えた文章", state.Text, "前回の結果は残っている");
    }

    private static void TimeoutBlocksStaleResult()
    {
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");

        state.BeginRun();   // タイムアウトも「結果が返らなかった」ことに変わりはない

        Assert.True(state.CanApply == false, "タイムアウト後は書き戻せない");
        Assert.True(state.HasStaleText, "前回の結果であることが分かる");
    }

    private static void CancellationBlocksStaleResult()
    {
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");

        state.BeginRun();   // 「中止」を押した場合

        Assert.True(state.CanApply == false, "中止後は書き戻せない");
    }

    private static void RetryAfterFailureAllowsApply()
    {
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");

        state.BeginRun();   // 失敗
        Assert.True(state.CanApply == false, "失敗直後は書き戻せない");

        state.BeginRun();   // 再試行
        state.SetResult("English text");

        Assert.True(state.CanApply, "再試行が成功すれば書き戻せる");
        Assert.Equal("English text", state.Text, "書き戻すのは新しい結果");
        Assert.True(state.HasStaleText == false, "前回扱いではない");
    }

    private static void StaleResultStaysVisible()
    {
        // 書き戻せないだけで、消しはしない(読み返したりコピーしたりはできる)。
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");
        state.BeginRun();

        Assert.True(state.HasText, "結果欄の内容は消えない");
        Assert.Equal("整えた文章", state.Text, "結果欄の内容");
    }

    private static void EditAfterSuccessCanApply()
    {
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");

        state.SetEditedText("整えた文章(手直し)");

        Assert.True(state.CanApply, "成功後の手直しは書き戻せる");
        Assert.Equal("整えた文章(手直し)", state.Text, "書き戻すのは手直し後の内容");
    }

    private static void EditOfStaleResultCannotApply()
    {
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");
        state.BeginRun();   // 失敗

        state.SetEditedText("前回の結果を手で直した");

        Assert.True(state.CanApply == false, "前回の結果を手直ししても書き戻せない");
    }

    private static void ClearedResultCannotApply()
    {
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("整えた文章");

        state.SetEditedText("   ");
        Assert.True(state.CanApply == false, "空にすると書き戻せない");

        state.SetEditedText("書き直した");
        Assert.True(state.CanApply, "書き直せば再び書き戻せる");
    }

    private static void EmptySuccessIsNotApplicable()
    {
        var state = new PolishResultState();
        state.BeginRun();
        state.SetResult("");

        Assert.True(state.CanApply == false, "空の結果は書き戻せない");
        Assert.True(state.HasStaleText == false, "前回の結果もない");
    }
}
