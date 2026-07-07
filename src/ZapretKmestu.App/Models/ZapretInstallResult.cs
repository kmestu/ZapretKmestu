namespace ZapretKmestu.Models;

/// <summary>
/// Represents the outcome of the zapret installation process.
/// </summary>
public sealed class ZapretInstallResult
{
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Version { get; init; }
    public string? InstallationPath { get; init; }

    public static ZapretInstallResult Successful(string version, string path) => new()
    {
        Success = true,
        Version = version,
        InstallationPath = path
    };

    public static ZapretInstallResult Failed(string error) => new()
    {
        Success = false,
        ErrorMessage = error
    };
}
