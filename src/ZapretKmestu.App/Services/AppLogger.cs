using System.Collections.Concurrent;
using System.IO;

namespace ZapretKmestu.Services;

/// <summary>
/// Simple file-based logger with an in-memory recent-log buffer.
/// All I/O errors are silently swallowed — logging must never crash the app.
/// Log format:  [yyyy-MM-dd HH:mm:ss] [LEVEL] Message
/// </summary>
public static class AppLogger
{
    private const int MaxRecentEntries = 200;

    private static readonly ConcurrentQueue<string> _recentLog = new();
    private static string? _logFilePath;
    private static readonly object _fileLock = new();

    // ── Initialization ────────────────────────────────────────────────────────

    public static void Initialize(string logFilePath)
    {
        _logFilePath = logFilePath;
        TryEnsureLogDirectory(logFilePath);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public static void Info(string message)    => Write("INFO", message);
    public static void Warning(string message) => Write("WARN", message);
    public static void Error(string message)   => Write("ERR ", message);

    /// <summary>Returns recent log lines (newest first) for display in the Journal page.</summary>
    public static IReadOnlyList<string> GetRecentEntries()
    {
        var list = _recentLog.ToArray();
        Array.Reverse(list);
        return list;
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static void Write(string level, string message)
    {
        var ts    = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var entry = $"[{ts}] [{level}] {message}";

        // Add to in-memory queue
        _recentLog.Enqueue(entry);
        while (_recentLog.Count > MaxRecentEntries)
            _recentLog.TryDequeue(out _);

        // Append to file (fire-and-forget; errors must not propagate)
        if (_logFilePath == null) return;
        try
        {
            lock (_fileLock)
                File.AppendAllText(_logFilePath, entry + Environment.NewLine);
        }
        catch
        {
            // Swallow — logging failures must never crash the app
        }
    }

    private static void TryEnsureLogDirectory(string logFilePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(logFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch
        {
            // Swallow — directory creation failures handled silently
        }
    }
}
