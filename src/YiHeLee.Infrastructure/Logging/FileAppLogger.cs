using System.Text;
using YiHeLee.Application.Abstractions;

namespace YiHeLee.Infrastructure.Logging;

public sealed class FileAppLogger : IAppLogger
{
    private readonly string _logDirectory;
    private readonly object _syncRoot = new();

    public FileAppLogger(string logDirectory)
    {
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public void Info(string message) => Write("INFO", message, null);
    public void Warning(string message) => Write("WARN", message, null);
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            var path = Path.Combine(_logDirectory, $"YiHeLee_{DateTime.Now:yyyyMMdd}.log");
            var builder = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(" [").Append(level).Append("] ")
                .AppendLine(message);
            if (exception is not null)
            {
                builder.AppendLine(exception.ToString());
            }

            lock (_syncRoot)
            {
                File.AppendAllText(path, builder.ToString(), new UTF8Encoding(false));
            }
        }
        catch
        {
            // Logging 本身不可讓主流程再次失敗。
        }
    }
}
