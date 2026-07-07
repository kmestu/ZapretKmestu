namespace ZapretKmestu.Models;

/// <summary>
/// Carries the outcome of a ZIP extraction and validation pass performed by
/// <see cref="ZapretKmestu.Services.ZapretExtractionService"/>.
/// This is a plain data object — it does not trigger any action by itself.
/// </summary>
public sealed class ZapretExtractionResult
{
    /// <summary>
    /// Full path to the source ZIP file that was extracted.
    /// Example: %LocalAppData%\Zapret Kmestu\Temp\v2025.04.10_zapret-v2025.04.10.zip
    /// </summary>
    public string SourceZipPath { get; init; } = "";

    /// <summary>
    /// Full path of the temporary staging directory created during extraction.
    /// Example: %LocalAppData%\Zapret Kmestu\Temp\zapret-extract-20260509-144200\
    /// This directory is the direct output of <see cref="System.IO.Compression.ZipFile.ExtractToDirectory"/>.
    /// </summary>
    public string StagingDirectory { get; init; } = "";

    /// <summary>
    /// Full path to the final install target directory where files were copied.
    /// Example: C:\ProgramData\Zapret Kmestu\zapret\
    /// </summary>
    public string TargetDirectory { get; init; } = "";

    /// <summary>
    /// <c>true</c> when all required files were found and the copy to <see cref="TargetDirectory"/>
    /// completed without errors; <c>false</c> otherwise.
    /// </summary>
    public bool IsValid { get; init; }

    /// <summary>
    /// Version tag parsed from the ZIP file name, e.g. "v2025.04.10", or <c>null</c>
    /// when the tag could not be inferred from the archive name.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// Relative paths (from <see cref="StagingDirectory"/>) of important files that were
    /// located during validation, e.g. <c>["service.bat", "bin\\winws.exe", "lists"]</c>.
    /// </summary>
    public IReadOnlyList<string> ImportantFilesFound { get; init; } =
        Array.Empty<string>();
}
