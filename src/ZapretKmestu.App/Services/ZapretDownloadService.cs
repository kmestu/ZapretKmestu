using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Downloads the Flowseal zapret ZIP archive to a local temp folder.
/// This service only transfers bytes — it does not extract, execute,
/// or install anything.
/// </summary>
/// <remarks>
/// Intended usage from ZapretInstallerService (next stage):
///
///   var info    = await _releaseService.GetLatestReleaseAsync(ct);
///   string zip  = await _downloadService.DownloadZipAsync(info, progress, ct);
///   // zip → pass to extraction service
/// </remarks>
public sealed class ZapretDownloadService
{
    // %LocalAppData%\Zapret Kmestu\Temp\
    private static readonly string TempDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zapret Kmestu",
        "Temp");

    private const string UserAgent = "ZapretKmestu/0.1";

    // 4 MB read buffer gives good throughput without excess memory.
    private const int BufferSize = 4 * 1024 * 1024;

    private readonly HttpClient _http;

    public ZapretDownloadService(HttpClient http)
    {
        _http = http;
    }

    /// <summary>
    /// Downloads <see cref="GitHubReleaseInfo.ZipAssetDownloadUrl"/> to the local temp folder
    /// and returns the full path of the saved file.
    /// </summary>
    /// <param name="release">Release metadata produced by <see cref="GitHubReleaseService"/>.</param>
    /// <param name="progress">Optional progress sink; receives <see cref="InstallProgressInfo"/> updates.</param>
    /// <param name="ct">Token that cancels the download mid-stream.</param>
    /// <returns>Absolute path to the downloaded .zip file.</returns>
    /// <exception cref="InvalidOperationException">URL is empty or asset name is missing.</exception>
    /// <exception cref="HttpRequestException">Network failure or non-success HTTP status.</exception>
    /// <exception cref="OperationCanceledException">Download was cancelled by the caller.</exception>
    /// <exception cref="IOException">Disk write failure.</exception>
    public async Task<string> DownloadZipAsync(
        GitHubReleaseInfo release,
        IProgress<InstallProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        // --- Validate input -----------------------------------------------
        if (string.IsNullOrWhiteSpace(release.ZipAssetDownloadUrl))
            throw new InvalidOperationException(
                "URL для загрузки архива отсутствует. Повторите получение информации о релизе.");

        if (string.IsNullOrWhiteSpace(release.ZipAssetName))
            throw new InvalidOperationException(
                "Имя ZIP-файла не определено. Повторите получение информации о релизе.");

        // --- Build safe destination path ----------------------------------
        // Example: Temp\v2025.04.10_zapret-v2025.04.10.zip
        string safeTag      = SanitizePathSegment(release.TagName);
        string safeAsset    = SanitizePathSegment(release.ZipAssetName);
        string fileName     = string.IsNullOrEmpty(safeTag)
                              ? safeAsset
                              : $"{safeTag}_{safeAsset}";
        string destPath     = Path.Combine(TempDirectory, fileName);

        EnsureTempDirectory();

        // --- Stream download ---------------------------------------------
        progress?.Report(new InstallProgressInfo
        {
            Step    = "Загрузка архива",
            Details = $"Подключение к {release.ZipAssetDownloadUrl}"
        });

        HttpResponseMessage response;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, release.ZipAssetDownloadUrl);
            response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "Загрузка архива отменена пользователем.", ct);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new HttpRequestException(
                "Соединение прервано: превышено время ожидания при загрузке архива.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new HttpRequestException(
                "Ошибка сети при загрузке архива zapret. " +
                "Проверьте интернет-соединение и повторите попытку.", ex);
        }

        long? totalBytes = response.Content.Headers.ContentLength;

        try
        {
            await using Stream remoteStream = await response.Content
                .ReadAsStreamAsync(ct)
                .ConfigureAwait(false);

            await using FileStream fileStream = new(
                destPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 65536,
                useAsync: true);

            byte[] buffer        = new byte[BufferSize];
            long   bytesReceived = 0L;
            int    bytesRead;

            while ((bytesRead = await remoteStream
                       .ReadAsync(buffer, ct)
                       .ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct)
                    .ConfigureAwait(false);

                bytesReceived += bytesRead;

                int? percent = totalBytes is > 0
                    ? (int)(bytesReceived * 100L / totalBytes.Value)
                    : null;

                progress?.Report(new InstallProgressInfo
                {
                    Step          = "Загрузка архива",
                    Percent       = percent,
                    BytesReceived = bytesReceived,
                    TotalBytes    = totalBytes,
                    Details       = FormatBytesDetail(bytesReceived, totalBytes)
                });
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Clean up the partial file so a retry starts fresh.
            TryDeleteFile(destPath);
            throw new OperationCanceledException(
                "Загрузка архива отменена пользователем.", ct);
        }
        catch (IOException ex)
        {
            TryDeleteFile(destPath);
            throw new IOException(
                $"Ошибка записи на диск при сохранении архива: {destPath}\n" +
                $"Причина: {ex.Message}", ex);
        }

        progress?.Report(new InstallProgressInfo
        {
            Step          = "Загрузка завершена",
            Percent       = 100,
            BytesReceived = null,
            TotalBytes    = totalBytes,
            Details       = destPath
        });

        return destPath;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void EnsureTempDirectory()
    {
        try
        {
            Directory.CreateDirectory(TempDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException(
                $"Не удалось создать временную папку для загрузки: {TempDirectory}\n" +
                $"Причина: {ex.Message}", ex);
        }
    }

    /// <summary>Removes characters that are unsafe in file names.</summary>
    private static string SanitizePathSegment(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Concat(input.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c));
    }

    private static string FormatBytesDetail(long received, long? total)
    {
        static string Fmt(long b) => b switch
        {
            >= 1024 * 1024 => $"{b / (1024.0 * 1024):F1} МБ",
            >= 1024        => $"{b / 1024.0:F1} КБ",
            _              => $"{b} Б"
        };

        return total.HasValue
            ? $"{Fmt(received)} / {Fmt(total.Value)}"
            : Fmt(received);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort cleanup */ }
    }

    // -------------------------------------------------------------------------
    // Factory — creates a pre-configured HttpClient for this service.
    // -------------------------------------------------------------------------
    public static HttpClient CreateHttpClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ZapretKmestu", "0.1"));
        http.Timeout = TimeSpan.FromMinutes(10); // allow large zip on slow connections
        return http;
    }
}
