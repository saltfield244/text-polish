using System.Diagnostics;
using System.IO;

namespace TextPolish.Core.Logging;

/// <summary>%AppData%\TextPolish\logs\ 配下に日次ログファイルを書き出す簡易ロガー。</summary>
public sealed class FileLogger : IAppLogger
{
    private readonly string _logFilePath;
    private readonly object _lock = new();

    public FileLogger(string? logDirectory = null)
    {
        var dir = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TextPolish", "logs");
        Directory.CreateDirectory(dir);
        _logFilePath = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    public string LogFilePath => _logFilePath;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message} :: {ex}");

    private void Write(string level, string message)
    {
        // 呼び出し側が取りこぼしても、APIキーらしい値はファイルへ残さない(最後の砦)。
        // 整形対象の文章は形が決まっておらず機械的には判別できないため、ここでは落とせない。
        // 文章をログへ渡さないのは呼び出し側で守る(LogSanitizer のコメント参照)。
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {LogSanitizer.Redact(message)}";
        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
            catch
            {
                // ログ書き込み失敗はアプリ本体の動作に影響させない。
            }
        }

        Debug.WriteLine(line);
    }
}
