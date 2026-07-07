namespace ZapretKmestu.Models;

/// <summary>
/// Carries progress information for a single installation step (e.g. downloading the ZIP).
/// Reported via IProgress&lt;InstallProgressInfo&gt; — does not trigger any action itself.
/// </summary>
public sealed class InstallProgressInfo
{
    /// <summary>Human-readable step name, e.g. "Загрузка архива".</summary>
    public string Step { get; init; } = "";

    /// <summary>Completion percentage 0–100, or null if unknown.</summary>
    public int? Percent { get; init; }

    /// <summary>Bytes received so far, or null if not yet started.</summary>
    public long? BytesReceived { get; init; }

    /// <summary>Total expected bytes, or null when Content-Length is absent.</summary>
    public long? TotalBytes { get; init; }

    /// <summary>Optional extra detail line shown below the progress bar.</summary>
    public string? Details { get; init; }
}
