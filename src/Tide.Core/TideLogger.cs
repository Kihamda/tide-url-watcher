namespace Tide.Core;

public sealed class TideLogger
{
    private readonly string _logPath;
    private readonly object _sync = new();

    public TideLogger(string? logPath = null)
    {
        _logPath = logPath ?? PortablePaths.AppLogPath;
    }

    public void Info(string message) => Write("INFO", message, null);

    public void Warn(string message, Exception? exception = null) => Write("WARN", message, exception);

    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public void Clear()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            lock (_sync)
            {
                File.WriteAllText(_logPath, string.Empty);
            }
        }
        catch
        {
            // Diagnostics must never make the app harder to start.
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
            var line = $"[{DateTimeOffset.Now:O}] {level} {message}";
            if (exception is not null)
            {
                line = $"{line}{Environment.NewLine}{exception}";
            }

            lock (_sync)
            {
                File.AppendAllText(_logPath, $"{line}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging is best-effort for portable, read-only locations.
        }
    }
}
