namespace ZapretKmestu.Models;

/// <summary>
/// Represents information about a zapret profile (batch file).
/// </summary>
public class ZapretProfileInfo
{
    /// <summary>Batch file name (e.g., general-FAKE-TLS-AUTO.bat)</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Full path to the batch file</summary>
    public string FullPath { get; set; } = string.Empty;

    /// <summary>Human-friendly name for the UI</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Category based on the profile type</summary>
    public string Category { get; set; } = "General";

    public override string ToString() => DisplayName;
}
