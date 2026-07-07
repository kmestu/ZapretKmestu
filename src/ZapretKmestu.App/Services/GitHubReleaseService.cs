using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Fetches the latest release metadata for Flowseal/zapret-discord-youtube from GitHub.
/// This service only reads release information — it does not download, extract,
/// or execute any file.
/// </summary>
public sealed class GitHubReleaseService
{
    private readonly HttpClient _http;
    private readonly string _repositoryUrl;
    public GitHubReleaseService(HttpClient http)
    {
        _http = http;
        _repositoryUrl = "https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest";
    }

    /// <summary>
    /// Queries the GitHub releases API and returns a <see cref="GitHubReleaseInfo"/>
    /// describing the latest release.
    /// </summary>
    /// <exception cref="HttpRequestException">GitHub is unreachable or returns a non-success status.</exception>
    /// <exception cref="InvalidOperationException">JSON is malformed or contains no .zip asset.</exception>
    public async Task<GitHubReleaseInfo> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        string json;
        try
        {
            json = await _http.GetStringAsync(_repositoryUrl, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                "Не удалось связаться с GitHub. Проверьте интернет-соединение.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                "Запрос к GitHub превысил время ожидания.", ex);
        }

        return ParseRelease(json);
    }

    // -------------------------------------------------------------------------
    // Parsing
    // -------------------------------------------------------------------------

    private GitHubReleaseInfo ParseRelease(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "Ответ GitHub содержит некорректный JSON.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;

            string tagName     = root.GetStringOrEmpty("tag_name");
            string name        = root.GetStringOrEmpty("name");
            DateTimeOffset? publishedAt = null;

            if (root.TryGetProperty("published_at", out var publishedEl) &&
                publishedEl.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(publishedEl.GetString(), out var dto))
            {
                publishedAt = dto;
            }

            if (!root.TryGetProperty("assets", out var assetsEl) ||
                assetsEl.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    "GitHub вернул релиз без списка assets.");
            }

            var allNames   = new List<string>();
            string? targetName = null;
            string? targetUrl  = null;
            long    targetSize = 0;

            foreach (var asset in assetsEl.EnumerateArray())
            {
                string assetName = asset.GetStringOrEmpty("name");
                allNames.Add(assetName);

                if (targetName is null)
                {
                    if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        targetName = assetName;
                        targetUrl  = asset.GetStringOrEmpty("browser_download_url");
                        targetSize = asset.TryGetProperty("size", out var sizeEl)
                                  ? sizeEl.GetInt64()
                                  : 0L;
                    }
                }
            }

            if (targetName is null)
            {
                throw new InvalidOperationException(
                    "В релизе GitHub не найден требуемый файл. " +
                    $"Найденные assets: [{string.Join(", ", allNames)}]");
            }

            return new GitHubReleaseInfo
            {
                TagName             = tagName,
                Name                = name,
                PublishedAt         = publishedAt,
                ZipAssetName        = targetName,
                ZipAssetDownloadUrl = targetUrl ?? "",
                ZipAssetSize        = targetSize,
                ZipAssetDigest      = null,   // GitHub does not provide digests in asset metadata
                AllAssetNames       = allNames.AsReadOnly()
            };
        }
    }

    // -------------------------------------------------------------------------
    // Factory — creates a pre-configured HttpClient for this service.
    // Usage from ZapretInstallerService (next stage):
    //
    //   var http    = GitHubReleaseService.CreateHttpClient();
    //   var service = new GitHubReleaseService(http);
    //   GitHubReleaseInfo info = await service.GetLatestReleaseAsync();
    //   // info.ZipAssetDownloadUrl  → pass to downloader
    //   // info.ZipAssetSize         → show progress bar max
    //   // info.TagName              → save as InstalledZapretVersion in AppSettings
    // -------------------------------------------------------------------------
    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ZapretKmestu", "0.1"));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.Timeout = TimeSpan.FromSeconds(30);
        return http;
    }
}

// -------------------------------------------------------------------------
// Internal JSON helpers — avoids null reference noise for missing string props.
// -------------------------------------------------------------------------
file static class JsonElementExtensions
{
    public static string GetStringOrEmpty(this JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) &&
               prop.ValueKind == JsonValueKind.String
            ? prop.GetString() ?? ""
            : "";
    }
}
