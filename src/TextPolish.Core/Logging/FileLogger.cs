using System.Diagnostics;
using System.IO;

namespace TextPolish.Core.Logging;

/// <summary>
/// %AppData%\TextPolish\logs\ 配下に日次ログファイルを書き出す簡易ロガー。
///
/// 書き込み先は1行ごとに「その行の時刻の日付」から決める。これは常駐アプリで、
/// PCを起動したまま日付をまたぐのが普通の使い方であるため。起動時にファイル名を
/// 決めてしまうと、何日分もが初日のファイルに積み上がり、日付でログを探せなくなる。
/// </summary>
public sealed class FileLogger : IAppLogger
{
    private readonly string _logDirectory;
    private readonly Func<DateTime> _clock;
    private readonly object _lock = new();

    /// <param name="logDirectory">ログの出力先。null なら %AppData%\TextPolish\logs\。</param>
    /// <param name="clock">現在時刻の取得。日付をまたぐ挙動をテストするために差し替える。</param>
    public FileLogger(string? logDirectory = null, Func<DateTime>? clock = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TextPolish", "logs");
        _clock = clock ?? (() => DateTime.Now);
        Directory.CreateDirectory(_logDirectory);
    }

    /// <summary>ログの出力先フォルダ。</summary>
    public string LogDirectory => _logDirectory;

    /// <summary>現在の日付のログファイル。日付が変わると別のファイルになる。</summary>
    public string LogFilePath => BuildLogFilePath(_clock());

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private string BuildLogFilePath(DateTime at) =>
        Path.Combine(_logDirectory, $"app-{at:yyyyMMdd}.log");

    private void Write(string level, string message)
    {
        var now = _clock();

        // 呼び出し側が取りこぼしても、APIキーらしい値はファイルへ残さない(最後の砦)。
        // 整形対象の文章は形が決まっておらず機械的には判別できないため、ここでは落とせない。
        // 文章をログへ渡さないのは呼び出し側で守る(LogSanitizer のコメント参照)。
        //
        // 改行も落とす。1件が複数行になると、あとから1行ずつ読むときに
        // どこまでが同じ記録なのか分からなくなる(例外の文面は改行を含むことがある)。
        var body = LogSanitizer.Redact(message).ReplaceLineEndings(" ");
        var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {body}";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(BuildLogFilePath(now), line + Environment.NewLine);
            }
            catch
            {
                // ログ書き込み失敗はアプリ本体の動作に影響させない。
                // ここで通知や再試行をすると、整形そのものを妨げるほうが害が大きい。
                // 失敗に気づく必要がある場面では、下の Debug 出力(デバッガ接続時)で追える。
            }
        }

        Debug.WriteLine(line);
    }
}
