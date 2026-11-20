using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace TextPolish.Tests.Harness;

/// <summary>
/// 確認用の文章を選択した状態で見せるだけの小さな窓。
///
/// なぜ自前の窓か: 整形パレットは「どこかのアプリで文章が選ばれている」状態から
/// 始まる。自動化するにはその状態を自分で作る必要がある。
///
/// 最初はメモ帳を起こしていたが、やめた。**Windows 11 のメモ帳は1つの窓を
/// タブで共有する**ため、自分が開いたタブのつもりで窓を閉じると、利用者が
/// 編集中の別タブまで閉じてしまう(実際に閉じてしまった)。他人の道具を借りると、
/// 片付けの範囲が自分のものだけに収まらない。
///
/// 自前の窓なら、閉じるのは自分のものだけで、利用者の文章も一時ファイルも
/// 一切作らない。アプリ側から見れば、ただの前面ウィンドウの選択テキストである。
/// </summary>
internal sealed class TextSourceWindow : IDisposable
{
    private readonly Form _form;
    private readonly TextBox _box;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);

    private TextSourceWindow(string text, string title)
    {
        _box = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            Text = text,
            ScrollBars = ScrollBars.Vertical,
        };

        _form = new Form
        {
            Text = title,
            Width = 520,
            Height = 220,
            StartPosition = FormStartPosition.CenterScreen,
            TopMost = true,
            ShowInTaskbar = true,
        };

        _form.Controls.Add(_box);

        // 窓は自分のメッセージループを持つ必要があるので、専用のスレッドで回す。
        // 呼び出し側(テストランナーの本体)は、そのまま UIA の操作を続ける。
        _thread = new Thread(() =>
        {
            _form.Shown += (_, _) =>
            {
                _box.Focus();
                _box.SelectAll();
                _ready.Set();
            };

            Application.Run(_form);
        })
        {
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>
    /// 窓を出して、文章を全選択した状態にする。
    /// 題名を分けられるのは、書き戻し先の確認(E)で窓を2つ使うため。
    /// </summary>
    public static TextSourceWindow? Open(
        string text, string title = "UIA試作の文章", int timeoutMs = 10000)
    {
        var window = new TextSourceWindow(text, title);
        window._thread.Start();

        if (window._ready.Wait(timeoutMs))
        {
            // 前面に出てからでないと、ホットキーを送っても別のアプリの選択を拾う。
            if (window.Activate())
            {
                return window;
            }

            Console.WriteLine("文章の窓を前面にできませんでした(ほかのアプリが前面を握っています)。");
            window.Dispose();
            return null;
        }

        window.Dispose();
        return null;
    }

    /// <summary>
    /// 前面に出して、選択を作り直す。本当に前面になったかを返す。
    ///
    /// ここを確かめるのが要。アプリはホットキーを受けると**前面のウィンドウへ**
    /// Ctrl+C を送る。前面でないまま送ると、選択テキストの取得が失敗し、
    /// 「アプリの不具合」に見えてしまう。さらに悪いことに、Ctrl+C がこちらを
    /// 動かしているコンソールへ飛ぶと、確認そのものが中断される。
    ///
    /// <c>Form.Activate</c> だけでは足りない。Windows は、最後に入力を受けた
    /// プロセス以外からの前面化を拒むため、相手のスレッドへ一時的に入力をつないでから
    /// 頼む(AttachThreadInput)。それでも拒まれることがあるので、戻り値で確かめる。
    /// </summary>
    public bool Activate()
    {
        Run(() =>
        {
            _form.Activate();
            _box.Focus();
            _box.SelectAll();
        });

        var handle = Run(() => _form.Handle);

        for (var i = 0; i < 20; i++)
        {
            if (GetForegroundWindow() == handle)
            {
                // 前面になってから選択を作り直す(焦点が移ると選択が外れることがある)。
                Run(() =>
                {
                    _box.Focus();
                    _box.SelectAll();
                });

                Thread.Sleep(150);
                return true;
            }

            ForceForeground(handle);
            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>
    /// 文章の一部だけを選択し直す。
    ///
    /// 周辺文脈の確認(H)に要る。既定の全選択では「選択範囲の外の文章」が存在しないため、
    /// 送られているかどうかを見分けられない。前面化のあとに呼ぶこと
    /// (<see cref="Activate"/> は選択を作り直すので、順番を逆にすると全選択へ戻る)。
    /// </summary>
    public void Select(int start, int length)
    {
        Run(() =>
        {
            _box.Focus();
            _box.Select(start, length);
        });

        Thread.Sleep(150);
    }

    /// <summary>入力を一時的につないでから前面化を頼む。</summary>
    private static void ForceForeground(IntPtr handle)
    {
        var foreground = GetForegroundWindow();
        var theirThread = GetWindowThreadProcessId(foreground, out _);
        var myThread = GetCurrentThreadId();
        var attached = theirThread != 0 && theirThread != myThread
            && AttachThreadInput(myThread, theirThread, true);

        try
        {
            ShowWindow(handle, 5);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(myThread, theirThread, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr handle, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint from, uint to, bool attach);

    public int SelectedLength => Run(() => _box.SelectedText.Length);

    /// <summary>この窓が前面かどうか。ホットキーを送る直前に確かめる。</summary>
    public bool IsForeground => GetForegroundWindow() == Run(() => _form.Handle);

    /// <summary>窓の中身。即実行で書き戻されたかを見るために読む。</summary>
    public string Content => Run(() => _box.Text) ?? string.Empty;

    public void Dispose()
    {
        Run(() => _form.Close());
        _thread.Join(3000);
        _ready.Dispose();
    }

    private void Run(Action action)
    {
        try
        {
            if (_form.IsDisposed)
            {
                return;
            }

            _form.Invoke(action);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (InvalidOperationException)
        {
            // 窓がまだ出ていない、あるいはもう無い。片付けの途中なら構わない。
        }
    }

    private T Run<T>(Func<T> read)
    {
        try
        {
            return _form.IsDisposed ? default! : _form.Invoke(read);
        }
        catch (ObjectDisposedException)
        {
            return default!;
        }
        catch (InvalidOperationException)
        {
            return default!;
        }
    }
}
