using TextPolish.Core.Automation;

namespace TextPolish.Tests;

/// <summary>
/// ホットキーの連打・経路をまたいだ同時実行に対する排他(<see cref="OperationGate"/>)。
///
/// App 側の実際の流れ(パレットは表示できたら閉じるまで保持、即実行は finally で解放)を
/// ここでは <see cref="RunPalette"/> / <see cref="RunQuick"/> で模して確かめる。
/// クリップボードもウィンドウも使わないため <c>--no-clipboard</c> でも実行される。
/// </summary>
internal static class OperationGateTests
{
    private const string Palette = "整形パレット";
    private const string Quick = "即実行";

    public static IEnumerable<(string, Action)> All()
    {
        yield return ("排他: 保持中の再取得は失敗する(連打)", RepeatedPressIsIgnored);
        yield return ("排他: 解放後は再取得できる", ReacquireAfterRelease);
        yield return ("排他: 保持中の操作名を参照できる", CurrentOwnerIsReported);
        yield return ("排他: 即実行中はパレットを起動しない", QuickBlocksPalette);
        yield return ("排他: パレット表示中は即実行しない", PaletteBlocksQuick);
        yield return ("排他: 取得開始前から保持する(取得完了前の連打)", HeldBeforeCaptureCompletes);
        yield return ("排他: 例外が起きても解放される", ReleasedOnException);
        yield return ("排他: キャンセルでも解放される", ReleasedOnCancellation);
        yield return ("排他: 二重解放は後続の操作に影響しない", DoubleDisposeDoesNotReleaseOthers);
        yield return ("排他: 交互操作を繰り返しても取りこぼさない", AlternatingOperations);
        yield return ("排他: 同時に取得できるのは1件だけ", ConcurrentAcquireAllowsOne);
    }

    private static void RepeatedPressIsIgnored()
    {
        var gate = new OperationGate();

        using var first = gate.TryAcquire(Palette);
        Assert.True(first is not null, "1回目の取得は成功する");

        Assert.True(gate.TryAcquire(Palette) is null, "連打した2回目は失敗する");
        Assert.True(gate.TryAcquire(Palette) is null, "連打した3回目も失敗する");
    }

    private static void ReacquireAfterRelease()
    {
        var gate = new OperationGate();

        var first = gate.TryAcquire(Quick);
        Assert.True(first is not null, "1回目の取得は成功する");
        first!.Dispose();

        Assert.True(gate.IsBusy == false, "解放後は保持されていない");
        using var second = gate.TryAcquire(Quick);
        Assert.True(second is not null, "解放後は再取得できる");
    }

    private static void CurrentOwnerIsReported()
    {
        var gate = new OperationGate();
        Assert.Equal(null, gate.CurrentOwner, "保持前の操作名");

        var lease = gate.TryAcquire(Quick);
        Assert.Equal(Quick, gate.CurrentOwner, "保持中の操作名");

        lease!.Dispose();
        Assert.Equal(null, gate.CurrentOwner, "解放後の操作名");
    }

    private static void QuickBlocksPalette()
    {
        var gate = new OperationGate();
        var log = new List<string>();

        // 即実行がLLM整形中(排他を保持したまま)に、パレットのホットキーを押す。
        RunQuick(gate, log, duringFormatting: () => RunPalette(gate, log, captureSucceeds: true));

        Assert.Equal(1, log.Count(x => x == "即実行:完了"), "即実行は完了する");
        Assert.Equal(1, log.Count(x => x == "整形パレット:無視"), "整形中のパレット起動は無視される");
        Assert.Equal(0, log.Count(x => x == "整形パレット:取得開始"), "パレット側の取得は始まらない");
    }

    private static void PaletteBlocksQuick()
    {
        var gate = new OperationGate();
        var log = new List<string>();

        // パレット表示中(閉じるまで排他を保持)に即実行のホットキーを押す。
        var palette = OpenPalette(gate, log);
        Assert.True(palette is not null, "パレットは開く");

        RunQuick(gate, log);
        Assert.Equal(1, log.Count(x => x == "即実行:無視"), "パレット表示中の即実行は無視される");

        // 閉じれば受け付ける。
        palette!.Dispose();
        RunQuick(gate, log);
        Assert.Equal(1, log.Count(x => x == "即実行:完了"), "パレットを閉じたあとは即実行できる");
    }

    private static void HeldBeforeCaptureCompletes()
    {
        var gate = new OperationGate();
        var log = new List<string>();

        // パレットのウィンドウが生成されるのは取得の完了後。取得中に押し直しても無視される必要がある。
        var palette = RunPalette(gate, log, captureSucceeds: true, duringCapture: () =>
        {
            RunPalette(gate, log, captureSucceeds: true);
            RunQuick(gate, log);
        });

        Assert.Equal(1, log.Count(x => x == "整形パレット:取得開始"), "取得は1回だけ始まる");
        Assert.Equal(1, log.Count(x => x == "整形パレット:無視"), "取得中のパレット起動は無視される");
        Assert.Equal(1, log.Count(x => x == "即実行:無視"), "取得中の即実行は無視される");

        Assert.True(gate.IsBusy, "パレット表示中は保持し続ける");
        palette!.Dispose();
        Assert.True(gate.IsBusy == false, "パレットを閉じれば解放されている");
    }

    private static void ReleasedOnException()
    {
        var gate = new OperationGate();
        var log = new List<string>();

        // 取得後の処理で例外が起きるケース。App 側は catch するため、ここでも外へは出さない。
        RunQuick(gate, log, duringFormatting: () => throw new InvalidOperationException("整形に失敗"));

        Assert.Equal(1, log.Count(x => x == "即実行:例外"), "例外は記録される");
        Assert.True(gate.IsBusy == false, "例外が起きても解放されている");

        RunQuick(gate, log);
        Assert.Equal(1, log.Count(x => x == "即実行:完了"), "例外の次の操作は受け付ける");
    }

    private static void ReleasedOnCancellation()
    {
        var gate = new OperationGate();
        var log = new List<string>();

        RunQuick(gate, log, duringFormatting: () => throw new OperationCanceledException());

        Assert.True(gate.IsBusy == false, "キャンセルでも解放されている");

        // 取得できなかった(選択テキストなし)場合も、パレットは表示されないまま解放される。
        RunPalette(gate, log, captureSucceeds: false);
        Assert.True(gate.IsBusy == false, "選択テキストが無い場合も解放されている");

        using var next = gate.TryAcquire(Palette);
        Assert.True(next is not null, "次の操作は受け付ける");
    }

    private static void DoubleDisposeDoesNotReleaseOthers()
    {
        var gate = new OperationGate();

        // パレットは Closed と呼び出し側の finally の双方から解放され得る。
        var palette = gate.TryAcquire(Palette);
        palette!.Dispose();

        var quick = gate.TryAcquire(Quick);
        Assert.True(quick is not null, "解放後に別の操作が取得できる");

        // 既に解放済みの Lease をもう一度破棄しても、間に始まった操作の排他は解かない。
        palette.Dispose();
        Assert.Equal(Quick, gate.CurrentOwner, "二重解放後も保持者は変わらない");
        Assert.True(gate.TryAcquire(Palette) is null, "二重解放で排他が抜けていない");

        quick!.Dispose();
        Assert.True(gate.IsBusy == false, "本来の保持者が解放すれば空く");
    }

    private static void AlternatingOperations()
    {
        var gate = new OperationGate();
        var log = new List<string>();

        for (var i = 0; i < 5; i++)
        {
            RunQuick(gate, log);

            var palette = OpenPalette(gate, log);
            Assert.True(palette is not null, $"{i + 1}回目のパレットは開く");
            palette!.Dispose();
        }

        Assert.Equal(5, log.Count(x => x == "即実行:完了"), "即実行の回数");
        Assert.Equal(5, log.Count(x => x == "整形パレット:表示"), "パレットの回数");
        Assert.Equal(0, log.Count(x => x.EndsWith(":無視")), "交互操作では無視されない");
        Assert.True(gate.IsBusy == false, "最後に解放されている");
    }

    private static void ConcurrentAcquireAllowsOne()
    {
        var gate = new OperationGate();
        var leases = new OperationGate.Lease?[32];

        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[leases.Length];

        for (var i = 0; i < threads.Length; i++)
        {
            var index = i;
            threads[i] = new Thread(() =>
            {
                start.Wait();
                leases[index] = gate.TryAcquire(index % 2 == 0 ? Palette : Quick);
            });
            threads[i].Start();
        }

        start.Set();
        foreach (var thread in threads)
        {
            thread.Join();
        }

        Assert.Equal(1, leases.Count(x => x is not null), "同時取得で成功するのは1件");

        foreach (var lease in leases)
        {
            lease?.Dispose();
        }

        Assert.True(gate.IsBusy == false, "全て解放すれば空く");
    }

    /// <summary>
    /// 即実行の流れ(App.OnQuickHotkey)を模す。
    /// 取得開始前に排他を取り、何が起きても finally で解放する。
    /// </summary>
    private static void RunQuick(OperationGate gate, List<string> log, Action? duringFormatting = null)
    {
        var lease = gate.TryAcquire(Quick);
        if (lease is null)
        {
            log.Add("即実行:無視");
            return;
        }

        try
        {
            log.Add("即実行:取得開始");
            duringFormatting?.Invoke();
            log.Add("即実行:完了");
        }
        catch (Exception)
        {
            log.Add("即実行:例外");
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <summary>
    /// パレットの流れ(App.OnPolishHotkey)を模す。
    /// 表示できた場合は排他を閉じるまで保持するため、その Lease を返す。
    /// </summary>
    private static OperationGate.Lease? OpenPalette(OperationGate gate, List<string> log) =>
        RunPalette(gate, log, captureSucceeds: true);

    private static OperationGate.Lease? RunPalette(
        OperationGate gate,
        List<string> log,
        bool captureSucceeds,
        Action? duringCapture = null)
    {
        var lease = gate.TryAcquire(Palette);
        if (lease is null)
        {
            log.Add("整形パレット:無視");
            return null;
        }

        var handedToPalette = false;

        try
        {
            log.Add("整形パレット:取得開始");
            duringCapture?.Invoke();

            if (!captureSucceeds)
            {
                log.Add("整形パレット:選択なし");
                return null;
            }

            log.Add("整形パレット:表示");
            handedToPalette = true;
            return lease;
        }
        catch (Exception)
        {
            log.Add("整形パレット:例外");
            return null;
        }
        finally
        {
            if (!handedToPalette)
            {
                lease.Dispose();
            }
        }
    }
}
