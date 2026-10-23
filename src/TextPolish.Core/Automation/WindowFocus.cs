namespace TextPolish.Core.Automation;

/// <summary>
/// ウィンドウのフォーカス操作。
/// 整形パレットを表示すると元のアプリからフォーカスが外れるため、
/// 結果を書き戻す前に元のウィンドウへ戻す必要がある。
/// </summary>
public static class WindowFocus
{
    /// <summary>フォーカスが実際に移るのを待つ上限(ミリ秒)。</summary>
    public const int DefaultRestoreTimeoutMs = 600;

    private const int PollIntervalMs = 30;

    public static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>ウィンドウが存在するか(閉じられていないか)。</summary>
    public static bool Exists(IntPtr hWnd) => hWnd != IntPtr.Zero && NativeMethods.IsWindow(hWnd);

    /// <summary>指定したウィンドウを前面に戻す。SetForegroundWindow の戻り値をそのまま返す。</summary>
    public static bool Restore(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        return NativeMethods.SetForegroundWindow(hWnd);
    }

    /// <summary>
    /// 指定したウィンドウを前面に戻し、実際に前面になるまで待つ。
    ///
    /// <see cref="Restore"/> の戻り値だけでは当てにならない。SetForegroundWindow は
    /// 呼び出し元が前面でない場合などに false を返しつつ実際にはフォーカスが移ることがあり、
    /// 逆に true でも反映は非同期で、直後に Ctrl+V を送ると前のウィンドウへ届く。
    /// そのため戻り値ではなく GetForegroundWindow の実際の状態を見る。
    ///
    /// 戻せなかった場合は false。呼び出し元は書き戻しを中止すること
    /// (意図しないウィンドウへ貼り付けるより、中止して利用者に知らせるほうが安全)。
    /// </summary>
    public static async Task<bool> RestoreAsync(
        IntPtr hWnd, int timeoutMs = DefaultRestoreTimeoutMs, CancellationToken ct = default)
    {
        if (!Exists(hWnd))
        {
            return false;
        }

        // 既に前面にある場合(即実行では通常こちら)は、余計な操作をせずそのまま進む。
        if (NativeMethods.GetForegroundWindow() == hWnd)
        {
            return true;
        }

        NativeMethods.SetForegroundWindow(hWnd);

        var deadline = Environment.TickCount64 + Math.Max(0, timeoutMs);
        while (Environment.TickCount64 < deadline)
        {
            await Task.Delay(PollIntervalMs, ct);

            if (NativeMethods.GetForegroundWindow() == hWnd)
            {
                return true;
            }
        }

        return false;
    }
}
