namespace TextPolish.Core.Automation;

/// <summary>
/// ホットキーから始まる一連の処理(選択テキストの取得 → 整形 → 書き戻し)を、
/// 操作経路をまたいで1件だけに絞る。
///
/// 選択テキストの取得は <see cref="SelectionCapture"/> がクリップボードの退避・Ctrl+C送出・
/// ポーリング・復元を行うため、重複すると取得結果の取り違えや復元の取りこぼしが起こる。
/// パレットの表示有無のような「見えている状態」では、取得が終わるまでの間を守れない
/// (パレットのウィンドウが生成されるのは取得の完了後)。そのため、取得を始める前に
/// ここで排他を取り、一連の処理が終わるまで保持する。
///
/// 保持している間は <see cref="TryAcquire"/> が null を返すので、呼び出し側は
/// その入力を無視すればよい。解放は <see cref="Lease.Dispose"/> で行い、
/// 二重に呼んでも、別の経路が既に取得し直していても影響しない。
/// </summary>
public sealed class OperationGate
{
    private readonly object _gate = new();
    private Lease? _current;

    /// <summary>保持中の操作名。保持されていなければnull。ログ・通知の文面用。</summary>
    public string? CurrentOwner
    {
        get
        {
            lock (_gate)
            {
                return _current?.Owner;
            }
        }
    }

    public bool IsBusy => CurrentOwner is not null;

    /// <summary>
    /// 排他を取る。既に別の(または同じ)操作が保持している場合はnullを返す。
    /// </summary>
    /// <param name="owner">ログに出す操作名(「整形パレット」「即実行」など)。</param>
    public Lease? TryAcquire(string owner)
    {
        lock (_gate)
        {
            if (_current is not null)
            {
                return null;
            }

            var lease = new Lease(this, owner);
            _current = lease;
            return lease;
        }
    }

    private void Release(Lease lease)
    {
        lock (_gate)
        {
            // 既に別の操作が取得し直している場合は触らない。
            if (ReferenceEquals(_current, lease))
            {
                _current = null;
            }
        }
    }

    /// <summary>
    /// 保持している排他。破棄すると解放される。
    /// 例外時やキャンセル時にも確実に解放されるよう、try/finally か using で扱う。
    /// </summary>
    public sealed class Lease : IDisposable
    {
        private OperationGate? _owner;

        internal Lease(OperationGate owner, string ownerName)
        {
            _owner = owner;
            Owner = ownerName;
        }

        public string Owner { get; }

        /// <summary>解放する。複数回呼んでも2回目以降は何もしない。</summary>
        public void Dispose()
        {
            // パレットの Closed と呼び出し側の finally の双方から呼ばれ得るため、
            // 解放は1回だけ行う(でないと、間に始まった別の操作の排他を解いてしまう)。
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release(this);
        }
    }
}
