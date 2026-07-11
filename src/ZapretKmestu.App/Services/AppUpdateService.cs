using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Downloads the ZapretKmestu.exe update asset from GitHub Releases to a local
/// temp folder and provides helpers for launching the dedicated updater process.
///
/// This service only handles download + path resolution — it does NOT touch any
/// installed files or processes.
/// </summary>
public sealed class AppUpdateService
{
    // %LocalAppData%\Zapret Kmestu\Temp\AppUpdate\
    private static readonly string TempDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zapret Kmestu",
        "Temp",
        "AppUpdate");

    private const int BufferSize = 1 * 1024 * 1024; // 1 MB

    private readonly HttpClient _http;

    public AppUpdateService(HttpClient http)
    {
        _http = http;
    }

    // -------------------------------------------------------------------------
    // Download
    // -------------------------------------------------------------------------

    /// <summary>
    /// Downloads the asset at <paramref name="downloadUrl"/> (ZIP or EXE) to a
    /// version-tagged temp folder.  Returns the full path to the saved file.
    /// </summary>
    /// <param name="downloadUrl">browser_download_url from GitHub assets.</param>
    /// <param name="tagName">Release tag used to name the temp sub-folder, e.g. "v0.2.0".</param>
    /// <param name="fileName">Name to save the downloaded file as.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the downloaded file.</returns>
    public async Task<string> DownloadUpdateAsync(
        string downloadUrl,
        string tagName,
        string fileName = "ZapretKmestu.zip",
        IProgress<InstallProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
            throw new InvalidOperationException("URL для загрузки обновления отсутствует.");

        string safeTag  = SanitizePathSegment(tagName);
        string destDir  = string.IsNullOrEmpty(safeTag)
            ? TempDirectory
            : Path.Combine(TempDirectory, safeTag);
        string destPath = Path.Combine(destDir, SanitizePathSegment(fileName));

        try { Directory.CreateDirectory(destDir); }
        catch (Exception ex)
        {
            throw new IOException(
                $"Не удалось создать временную папку для загрузки: {destDir}\nПричина: {ex.Message}", ex);
        }

        int maxAttempts = 3;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    AppLogger.Info($"[AppUpdate] Повторная попытка загрузки ZIP: attempt {attempt}/{maxAttempts}");
                    progress?.Report(new InstallProgressInfo
                    {
                        Step    = $"Повторная попытка загрузки ({attempt}/{maxAttempts})",
                        Details = $"Подключение к {downloadUrl}"
                    });
                }
                else
                {
                    progress?.Report(new InstallProgressInfo
                    {
                        Step    = "Загрузка обновления Kmestu",
                        Details = $"Подключение к {downloadUrl}"
                    });
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl)
                {
                    Version = System.Net.HttpVersion.Version11,
                    VersionPolicy = HttpVersionPolicy.RequestVersionExact
                };

                using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;

                await using Stream remote = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                await using FileStream file = new(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 65536, useAsync: true);

                byte[] buffer       = new byte[BufferSize];
                long   bytesReceived = 0L;
                int    bytesRead;

                while ((bytesRead = await remote.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                    bytesReceived += bytesRead;

                    int? percent = totalBytes is > 0
                        ? (int)(bytesReceived * 100L / totalBytes.Value)
                        : null;

                    progress?.Report(new InstallProgressInfo
                    {
                        Step          = "Загрузка обновления Kmestu",
                        Percent       = percent,
                        BytesReceived = bytesReceived,
                        TotalBytes    = totalBytes,
                        Details       = FormatBytesDetail(bytesReceived, totalBytes)
                    });
                }

                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDeleteFile(destPath);
                throw new OperationCanceledException("Загрузка обновления отменена.", ct);
            }
            catch (Exception ex) when (ex is HttpRequestException || ex is IOException || ex is System.Net.Sockets.SocketException)
            {
                TryDeleteFile(destPath);

                if (attempt == maxAttempts)
                {
                    AppLogger.Error($"Ошибка загрузки обновления Kmestu: {ex.Message}; Inner: {ex.InnerException?.Message}\nПолные данные: {ex}");
                    throw new HttpRequestException(
                        $"Ошибка сети при загрузке обновления Kmestu: {ex.Message}", ex);
                }

                int delayMs = attempt == 1 ? 2000 : 4000;
                await Task.Delay(delayMs, ct).ConfigureAwait(false);
            }
        }

        progress?.Report(new InstallProgressInfo
        {
            Step    = "Загрузка завершена",
            Percent = 100,
            Details = destPath
        });

        return destPath;
    }

    // -------------------------------------------------------------------------
    // ZIP download + extraction
    // -------------------------------------------------------------------------

    /// <summary>
    /// Downloads a ZIP release asset from <paramref name="downloadUrl"/>, extracts it
    /// safely into a dedicated temp directory, locates <c>ZapretKmestu.exe</c> inside
    /// the extracted tree, and returns its absolute path.
    ///
    /// Path-traversal entries (those whose canonical path escapes the extract root)
    /// are silently skipped.
    /// </summary>
    /// <param name="downloadUrl">browser_download_url for the .zip asset.</param>
    /// <param name="tagName">Release tag — used to name the temp sub-folder.</param>
    /// <param name="zipFileName">File name used when saving the downloaded ZIP.</param>
    /// <param name="progress">Optional progress sink.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Absolute path to the extracted ZapretKmestu.exe.</returns>
    /// <exception cref="FileNotFoundException">ZapretKmestu.exe was not found in the extracted ZIP.</exception>
    public async Task<string> DownloadExtractAndLocateExeAsync(
        string downloadUrl,
        string tagName,
        string zipFileName = "ZapretKmestu.zip",
        IProgress<InstallProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        AppLogger.Info($"[AppUpdate] Скачивание ZIP-пакета обновления: {downloadUrl}");

        // ── Step 1: Download ZIP ─────────────────────────────────────────────
        string zipPath = await DownloadUpdateAsync(
            downloadUrl, tagName, zipFileName, progress, ct).ConfigureAwait(false);

        AppLogger.Info($"[AppUpdate] ZIP загружен: {zipPath}");

        // ── Step 2: Prepare extraction directory ─────────────────────────────
        string safeTag    = SanitizePathSegment(tagName);
        string extractDir = Path.Combine(
            Path.GetDirectoryName(zipPath)!,
            "extracted");

        if (Directory.Exists(extractDir))
        {
            AppLogger.Info($"[AppUpdate] Очистка старой папки распаковки: {extractDir}");
            Directory.Delete(extractDir, recursive: true);
        }

        Directory.CreateDirectory(extractDir);
        AppLogger.Info($"[AppUpdate] Распаковка в: {extractDir}");

        // ── Step 3: Extract safely (path-traversal guard) ────────────────────
        progress?.Report(new InstallProgressInfo
        {
            Step    = "Распаковка обновления",
            Details = extractDir
        });

        string fullExtractRoot = Path.GetFullPath(extractDir);
        if (!fullExtractRoot.EndsWith(Path.DirectorySeparatorChar))
            fullExtractRoot += Path.DirectorySeparatorChar;

        using (var archive = ZipFile.OpenRead(zipPath))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                // Skip directory entries
                if (string.IsNullOrEmpty(entry.Name))
                    continue;

                string destinationPath = Path.GetFullPath(
                    Path.Combine(extractDir, entry.FullName));

                // Path-traversal guard: destination must stay inside extractDir
                if (!destinationPath.StartsWith(fullExtractRoot, StringComparison.OrdinalIgnoreCase))
                {
                    AppLogger.Warning($"[AppUpdate] Пропущена подозрительная запись ZIP (path traversal): {entry.FullName}");
                    continue;
                }

                // Ensure the target sub-directory exists
                string? entryDir = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrEmpty(entryDir))
                    Directory.CreateDirectory(entryDir);

                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }

        AppLogger.Info($"[AppUpdate] Распаковка завершена. Поиск ZapretKmestu.exe в {extractDir}");

        // ── Step 4: Locate ZapretKmestu.exe in the extracted tree ────────────
        string? exePath = Directory
            .EnumerateFiles(extractDir, "ZapretKmestu.exe", SearchOption.AllDirectories)
            .FirstOrDefault();

        if (exePath is null || !File.Exists(exePath))
        {
            AppLogger.Error($"[AppUpdate] ZapretKmestu.exe не найден в распакованном архиве: {extractDir}");
            throw new FileNotFoundException(
                "ZapretKmestu.exe не найден в распакованном архиве обновления.", "ZapretKmestu.exe");
        }

        AppLogger.Info($"[AppUpdate] Найден ZapretKmestu.exe: {exePath}");
        return exePath;
    }

    // -------------------------------------------------------------------------
    // Updater process resolution
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns the full path to ZapretKmestu.Updater.exe by looking in the same
    /// directory as the currently running executable.
    /// Returns null if the file is not found.
    /// </summary>
    public static string? FindUpdaterExecutable()
    {
        string appDir = AppContext.BaseDirectory;
        string updaterPath = Path.Combine(appDir, "ZapretKmestu.Updater.exe");
        return File.Exists(updaterPath) ? updaterPath : null;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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
        return total.HasValue ? $"{Fmt(received)} / {Fmt(total.Value)}" : Fmt(received);
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { /* best-effort */ }
    }

    // -------------------------------------------------------------------------
    // Factory
    // -------------------------------------------------------------------------

    public static HttpClient CreateHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true
        };
        var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("ZapretKmestu", "0.1"));
        http.Timeout = TimeSpan.FromMinutes(10);
        return http;
    }
}
