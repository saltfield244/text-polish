namespace TextPolish.Core.Polish;

/// <summary>
/// 整形パレットの「整形結果」の状態。書き戻してよいかどうかを一箇所で決める。
///
/// パレットは同じウィンドウで何度でも再整形できる。以前は結果欄が空でなければ
/// 「置き換える」を押せたため、1回目が成功したあと2回目(別のプリセットや自由指示)が
/// 失敗・タイムアウト・中止になると、前回の結果を今回の結果として書き戻せてしまった
/// (「整える」で成功 → 「英訳」で失敗 → 英訳されていない文章を適用、など)。
///
/// そこで「最後に実行した整形が成功したか」を持ち、成功していないあいだは
/// <see cref="CanApply"/> を false にする。前回の結果そのものは消さずに残すので、
/// 利用者は内容を読み返したりコピーしたりはできる(<see cref="HasStaleText"/>)。
/// </summary>
public sealed class PolishResultState
{
    private bool _lastRunSucceeded;

    /// <summary>表示中の整形結果。まだ一度も成功していなければ空。</summary>
    public string Text { get; private set; } = string.Empty;

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>書き戻してよいか。直近の整形が成功していて、内容が空でない場合だけ true。</summary>
    public bool CanApply => _lastRunSucceeded && HasText;

    /// <summary>
    /// 表示中の内容が前回の結果か(＝直近の整形が成功していないのに結果欄に文章がある)。
    /// 「表示されているのは前回の結果です」と伝えるために使う。
    /// </summary>
    public bool HasStaleText => !_lastRunSucceeded && HasText;

    /// <summary>
    /// 整形を開始する。この時点で表示中の結果は「前回の結果」に格下げされ、
    /// 今回の整形が成功するまで書き戻せなくなる。
    /// </summary>
    public void BeginRun() => _lastRunSucceeded = false;

    /// <summary>整形が成功した。結果を今回のものとして受け取る。</summary>
    public void SetResult(string? text)
    {
        Text = text ?? string.Empty;

        // 空の結果は成功とみなさない(整形器側で弾いているが、ここでも書き戻させない)。
        _lastRunSucceeded = HasText;
    }

    /// <summary>
    /// 利用者が結果欄を編集した。成功済みかどうかは変えない
    /// (成功後の手直しは書き戻せるが、前回の結果を手直ししても書き戻せない)。
    /// </summary>
    public void SetEditedText(string? text) => Text = text ?? string.Empty;
}
