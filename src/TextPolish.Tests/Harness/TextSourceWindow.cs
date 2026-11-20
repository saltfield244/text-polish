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

    private TextSourceWindow(string text)
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
            Text = "UIA試作の文章",
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

    /// <summary>窓を出して、文章を全選択した状態にする。</summary>
    public static TextSourceWindow? Open(string text, int timeoutMs = 10000)
    {
        var window = new TextSourceWindow(text);
        window._thread.Start();

        if (window._ready.Wait(timeoutMs))
        {
            // 前面に出てからでないと、ホットキーを送っても別のアプリの選択を拾う。
            window.Activate();
            return window;
        }

        window.Dispose();
        return null;
    }

    /// <summary>前面に出して、選択を作り直す。</summary>
    public void Activate()
    {
        Run(() =>
        {
            _form.Activate();
            _box.Focus();
            _box.SelectAll();
        });

        Thread.Sleep(300);
    }

    public int SelectedLength => Run(() => _box.SelectedText.Length);

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
