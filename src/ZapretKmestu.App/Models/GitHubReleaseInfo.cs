namespace ZapretKmestu.Models;

/// <summary>
/// Describes the latest GitHub release for Flowseal/zapret-discord-youtube.
/// Populated by GitHubReleaseService. Does not trigger any download or install.
/// </summary>
public sealed class GitHubReleaseInfo
{
    /// <summary>Release tag, e.g. "v2025.04.10".</summary>
    public string TagName { get; init; } = "";

    /// <summary>Human-readable release name from GitHub.</summary>
    public string Name { get; init; } = "";

    /// <summary>UTC publish timestamp from GitHub.</summary>
    public DateTimeOffset? PublishedAt { get; init; }

    /// <summary>File name of the selected .zip asset, e.g. "zapret-v2025.04.10.zip".</summary>
    public string ZipAssetName { get; init; } = "";

    /// <summary>Direct browser_download_url for the .zip asset. Not downloaded in this stage.</summary>
    public string ZipAssetDownloadUrl { get; init; } = "";

    /// <summary>Size of the .zip asset in bytes.</summary>
    public long ZipAssetSize { get; init; }

    /// <summary>
    /// Optional digest/checksum string if GitHub ever provides one in the asset metadata.
    /// Currently always null — reserved for future validation.
    /// </summary>
    public string? ZipAssetDigest { get; init; }

    /// <summary>Names of all assets attached to the release, for informational purposes.</summary>
    public IReadOnlyList<string> AllAssetNames { get; init; } = [];
}
