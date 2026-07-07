namespace ZapretKmestu.Models;

/// <summary>
/// Result of a service-related operation (reinstall, start, stop).
/// </summary>
public class ZapretServiceActionResult
{
    /// <summary>
    /// Whether the operation completed successfully.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// User-friendly message (success info or error description).
    /// </summary>
    public string Message { get; set; } = string.Empty;

    public static ZapretServiceActionResult Ok(string message) => new() { Success = true, Message = message };
    public static ZapretServiceActionResult Error(string message) => new() { Success = false, Message = message };
}
