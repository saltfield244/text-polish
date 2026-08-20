namespace TextPolish.Core.Automation;

/// <summary>
/// ウィンドウのフォーカス操作。
/// 整形パレットを表示すると元のアプリからフォーカスが外れるため、
/// 結果を書き戻す前に元のウィンドウへ戻す必要がある。
/// </summary>
public static class WindowFocus
{
    public static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    /// <summary>指定したウィンドウを前面に戻す。成功可否を返す。</summary>
    public static bool Restore(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero)
        {
            return false;
        }

        return NativeMethods.SetForegroundWindow(hWnd);
    }
}
