namespace ZapretKmestu.Models;

/// <summary>
/// Model representing the status of the Windows "zapret" service.
/// Used for read-only status detection.
/// </summary>
public class ZapretServiceStatusInfo
{
    /// <summary>
    /// True if the service is found in the system.
    /// </summary>
    public bool Exists { get; set; }

    /// <summary>
    /// True if the service is currently running.
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    /// Russian status text for UI display.
    /// </summary>
    public string StatusText { get; set; } = string.Empty;

    /// <summary>
    /// Error message if service check failed (e.g. permission error).
    /// </summary>
    public string? ErrorMessage { get; set; }
}
