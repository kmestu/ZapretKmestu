namespace ZapretKmestu.Updater.Services;

/// <summary>
/// Lightweight file-based logger for the updater process.
///
/// Log location: %LocalAppData%\Zapret Kmestu\Logs\updater.log
///
/// Intentionally self-contained and independent from the WPF application's logging.
/// Each session appends to the same log file so history is preserved.
/// </summary>
public sealed class UpdaterLogger : IDisposable
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zapret Kmestu",
        "Logs",
        "updater.log");

    private readonly StreamWriter _writer;
    private bool _disposed;

    public UpdaterLogger()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);

        // Append mode so multiple update sessions accumulate history.
        _writer = new StreamWriter(LogPath, append: true, encoding: System.Text.Encoding.UTF8)
        {
            AutoFlush = true
        };
    }

    /// <summary>Writes a timestamped line to the log file.</summary>
    public void Log(string message)
    {
        if (_disposed) return;
        string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}";
        _writer.WriteLine(line);
    }

    /// <summary>Writes a visual section separator to make log sections easy to read.</summary>
    public void LogSection(string title)
    {
        Log($"--- {title} ---");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writer.Dispose();
    }
}
