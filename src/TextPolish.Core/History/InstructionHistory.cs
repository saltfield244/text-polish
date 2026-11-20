using System.IO;
using TextPolish.Core.Logging;

namespace TextPolish.Core.History;

/// <summary>
/// 自由指示の入力履歴。よく使う言い回しを打ち直さずに済ませるためのもの。
///
/// 保存先を appsettings.json ではなく %AppData%\TextPolish\ 配下にしているのは、
/// これが設定ではなく実行時に増えていく状態だから。設定ファイルに混ぜると、
/// 手編集や設定画面からの保存と衝突する。
///
/// ファイルは**平文**で、残るのは指示の文面だけ(整形対象の文章やLLMの応答は入らない)。
/// それでも自由指示には実務の文脈が混ざりやすいため、保存するかどうかを
/// <see cref="Enabled"/> で切り替えられるようにし、消す手段(<see cref="Clear"/>)も置いた。
/// </summary>
public sealed class InstructionHistory
{
    /// <summary>
    /// 保持する件数。ドロップダウンに並べて選ぶ用途なので、多すぎると探せなくなる。
    /// 溢れた分は古いものから捨てる。
    /// </summary>
    private const int MaxEntries = 20;

    private readonly string _filePath;
    private readonly IAppLogger _logger;
    private readonly List<string> _entries = new();

    public InstructionHistory(IAppLogger logger, string? filePath = null, bool enabled = true)
    {
        _logger = logger;
        Enabled = enabled;
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TextPolish",
            "instruction-history.txt");
    }

    /// <summary>新しいものから順に並んだ履歴。</summary>
    public IReadOnlyList<string> Entries => _entries;

    /// <summary>
    /// 履歴を保存するかどうか(<c>Polish.SaveInstructionHistory</c>)。
    ///
    /// false の間は、積んでもファイルへ書かない。**その場の候補としても残さない。**
    /// 「保存しない」を選んだのに、閉じるまでドロップダウンに出続けるのは
    /// 期待とずれるため。設定画面から切り替えられるので、途中で変わりうる。
    /// </summary>
    public bool Enabled { get; set; }

    public void Load()
    {
        _entries.Clear();

        if (!Enabled || !File.Exists(_filePath))
        {
            return;
        }

        try
        {
            foreach (var line in File.ReadAllLines(_filePath))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !_entries.Contains(trimmed))
                {
                    _entries.Add(trimmed);
                }
            }

            if (_entries.Count > MaxEntries)
            {
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
            }
        }
        catch (Exception ex)
        {
            // 履歴は補助機能なので、読めなくても整形自体は続行させる。
            _logger.Warn($"自由指示の履歴を読み込めませんでした: {ex.Message}");
            _entries.Clear();
        }
    }

    /// <summary>
    /// 指示を履歴の先頭に積む。同じ内容が既にあれば重複させず先頭へ移動する
    /// (同じ指示を繰り返し使うほど探しやすくなる)。
    /// </summary>
    public void Add(string instruction)
    {
        var trimmed = instruction?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return;
        }

        if (!Enabled)
        {
            return;
        }

        _entries.Remove(trimmed);
        _entries.Insert(0, trimmed);

        if (_entries.Count > MaxEntries)
        {
            _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);
        }

        Save();
    }

    /// <summary>
    /// 履歴を空にし、ファイルも削除する。設定画面の「履歴を消す」と、
    /// 保存を無効にして保存したときに呼ぶ。消せたかどうかを返す。
    /// </summary>
    public bool Clear()
    {
        _entries.Clear();

        try
        {
            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }

            _logger.Info("自由指示の履歴を削除しました。");
            return true;
        }
        catch (Exception ex)
        {
            // 消せなくても整形は続けられる。黙って成功したように見せない。
            _logger.Warn($"自由指示の履歴を削除できませんでした: {ex.Message}");
            return false;
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllLines(_filePath, _entries);
        }
        catch (Exception ex)
        {
            _logger.Warn($"自由指示の履歴を保存できませんでした: {ex.Message}");
        }
    }
}
