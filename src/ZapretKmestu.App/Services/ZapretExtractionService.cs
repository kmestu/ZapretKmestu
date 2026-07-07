using System.IO;
using System.IO.Compression;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Extracts a downloaded Flowseal/zapret ZIP archive to a temporary staging directory,
/// validates the expected file structure, then safely copies the validated files into
/// the permanent ProgramData target folder.
///
/// Safety guarantees:
/// <list type="bullet">
///   <item>Never executes any extracted file (no .bat, .cmd, .ps1, .exe invocation).</item>
///   <item>Only writes inside %LocalAppData%\Zapret Kmestu\Temp\ and C:\ProgramData\Zapret Kmestu\zapret\.</item>
///   <item>Overwrites existing files at the target path but does NOT delete files that
///         exist only at the target — user files are preserved.</item>
///   <item>Does not touch the registry, Windows services, or network settings.</item>
///   <item>Does not require administrator privileges for the staging step;
///         a friendly error is thrown if ProgramData cannot be written.</item>
/// </list>
/// </summary>
public sealed class ZapretExtractionService
{
    // -------------------------------------------------------------------------
    // Paths
    // -------------------------------------------------------------------------

    /// <summary>%LocalAppData%\Zapret Kmestu\Temp\</summary>
    private static readonly string TempRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Zapret Kmestu",
        "Temp");

    /// <summary>C:\ProgramData\Zapret Kmestu\zapret\</summary>
    private static readonly string ProgramDataTarget = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "Zapret Kmestu",
        "zapret");

    // -------------------------------------------------------------------------
    // Required structure (relative paths inside the archive root)
    // -------------------------------------------------------------------------

    private static readonly string[] RequiredFiles =
    [
        "service.bat",
        Path.Combine("bin", "winws.exe"),
    ];

    private static readonly string[] RequiredDirectories =
    [
        "lists",
    ];

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Full pipeline: extract → validate → copy to ProgramData.
    /// </summary>
    /// <param name="zipPath">Absolute path to the downloaded .zip file.</param>
    /// <param name="progress">
    /// Optional progress sink for step notifications (e.g. bound to a UI label).
    /// Percent may be null during extraction and copy.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="ZapretExtractionResult"/> describing the outcome.</returns>
    /// <exception cref="InvalidOperationException">
    /// Thrown (in Russian) when the ZIP is missing required files.
    /// </exception>
    /// <exception cref="IOException">
    /// Thrown (in Russian) when a disk or permissions error occurs.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Thrown when cancellation is requested mid-operation.
    /// </exception>
    public async Task<ZapretExtractionResult> ExtractAndInstallAsync(
        string zipPath,
        IProgress<InstallProgressInfo>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath))
            throw new ArgumentException(
                "Путь к ZIP-архиву не указан.", nameof(zipPath));

        if (!File.Exists(zipPath))
            throw new FileNotFoundException(
                $"ZIP-архив не найден: {zipPath}", zipPath);

        string? stagingDir = null;
        try
        {
            // Step 1 — Extract to staging
            progress?.Report(new InstallProgressInfo
            {
                Step    = "Распаковываем архив...",
                Percent = null,
                Details = zipPath
            });

            stagingDir = await ExtractToStagingAsync(zipPath, ct)
                .ConfigureAwait(false);

            // Step 2 — Validate structure
            progress?.Report(new InstallProgressInfo
            {
                Step    = "Проверяем файлы...",
                Percent = null,
                Details = stagingDir
            });

            ct.ThrowIfCancellationRequested();
            string actualRoot = ResolveExtractedRoot(stagingDir);
            var (importantFiles, version) = ValidateStructure(actualRoot, zipPath);

            // Step 3 — Copy to ProgramData
            progress?.Report(new InstallProgressInfo
            {
                Step    = "Копируем файлы...",
                Percent = null,
                Details = ProgramDataTarget
            });

            EnsureTargetDirectory();
            await CopyDirectoryAsync(actualRoot, ProgramDataTarget, ct)
                .ConfigureAwait(false);

            // Done
            progress?.Report(new InstallProgressInfo
            {
                Step    = "zapret установлен локально.",
                Percent = 100,
                Details = ProgramDataTarget
            });

            return new ZapretExtractionResult
            {
                SourceZipPath     = zipPath,
                StagingDirectory  = stagingDir,
                TargetDirectory   = ProgramDataTarget,
                IsValid           = true,
                Version           = version,
                ImportantFilesFound = importantFiles
            };
        }
        finally
        {
            if (!string.IsNullOrEmpty(stagingDir))
            {
                CleanupStagingDirectory(stagingDir);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Step 1 — Extract ZIP to a timestamped staging subfolder
    // -------------------------------------------------------------------------

    private static Task<string> ExtractToStagingAsync(
        string zipPath,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            string timestamp  = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string stagingDir = Path.Combine(TempRoot, $"zapret-extract-{timestamp}");

            try
            {
                Directory.CreateDirectory(stagingDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Не удалось создать временную папку для распаковки: {stagingDir}\n" +
                    $"Причина: {ex.Message}", ex);
            }

            try
            {
                ZipFile.ExtractToDirectory(zipPath, stagingDir, overwriteFiles: true);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException(
                    $"Архив повреждён или имеет неверный формат: {zipPath}\n" +
                    $"Причина: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException(
                    $"Ошибка при распаковке архива в папку: {stagingDir}\n" +
                    $"Причина: {ex.Message}", ex);
            }

            return stagingDir;
        }, ct);
    }

    // -------------------------------------------------------------------------
    // Step 2 — Validate extracted structure
    // -------------------------------------------------------------------------

    private static string ResolveExtractedRoot(string stagingDir)
    {
        if (File.Exists(Path.Combine(stagingDir, "service.bat")))
        {
            return stagingDir;
        }

        string[] dirs = Directory.GetDirectories(stagingDir);
        if (dirs.Length == 1 && File.Exists(Path.Combine(dirs[0], "service.bat")))
        {
            AppLogger.Info($"Обнаружена вложенная папка архива: {Path.GetFileName(dirs[0])}");
            return dirs[0];
        }

        return stagingDir;
    }

    /// <summary>
    /// Checks that the expected files and directories exist under <paramref name="stagingDir"/>.
    /// </summary>
    /// <returns>
    /// A tuple of (list of important relative paths found, version string or null).
    /// </returns>
    /// <exception cref="InvalidOperationException">When a required item is missing.</exception>
    private static (IReadOnlyList<string> importantFiles, string? version)
        ValidateStructure(string stagingDir, string zipPath)
    {
        var found = new List<string>();

        // Check required files
        foreach (string relFile in RequiredFiles)
        {
            string fullPath = Path.Combine(stagingDir, relFile);
            if (!File.Exists(fullPath))
                throw new InvalidOperationException(
                    $"Обязательный файл не найден в архиве: {relFile}\n" +
                    $"Убедитесь, что загружен правильный архив Flowseal zapret.");

            found.Add(relFile);
        }

        // Check required directories
        foreach (string relDir in RequiredDirectories)
        {
            string fullPath = Path.Combine(stagingDir, relDir);
            if (!Directory.Exists(fullPath))
                throw new InvalidOperationException(
                    $"Обязательная папка не найдена в архиве: {relDir}\n" +
                    $"Убедитесь, что загружен правильный архив Flowseal zapret.");

            found.Add(relDir);
        }

        // Detect strategy .bat files (general*.bat) — informational, not required
        foreach (string bat in Directory.EnumerateFiles(stagingDir, "general*.bat",
                     SearchOption.TopDirectoryOnly))
        {
            found.Add(Path.GetFileName(bat));
        }

        // Try to extract version from zip file name, e.g. "v2025.04.10_zapret-v2025.04.10.zip"
        string? version = TryParseVersion(Path.GetFileNameWithoutExtension(zipPath));

        return (found.AsReadOnly(), version);
    }

    // -------------------------------------------------------------------------
    // Step 3 — Copy staging → ProgramData target (safe, non-destructive)
    // -------------------------------------------------------------------------

    private static void EnsureTargetDirectory()
    {
        try
        {
            Directory.CreateDirectory(ProgramDataTarget);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"Нет прав на создание папки назначения: {ProgramDataTarget}\n" +
                "Запустите программу от имени администратора или выберите другую папку установки.", ex);
        }
        catch (Exception ex) when (ex is IOException)
        {
            throw new IOException(
                $"Не удалось создать папку назначения: {ProgramDataTarget}\n" +
                $"Причина: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Recursively copies all files from <paramref name="sourceDir"/> into
    /// <paramref name="targetDir"/>, overwriting existing files with the same
    /// relative path. Files that already exist ONLY in the target are left untouched.
    /// </summary>
    private static async Task CopyDirectoryAsync(
        string sourceDir,
        string targetDir,
        CancellationToken ct)
    {
        // Enumerate all files in source tree
        foreach (string srcFile in Directory.EnumerateFiles(
                     sourceDir, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();

            string relativePath = Path.GetRelativePath(sourceDir, srcFile);
            string destFile     = Path.Combine(targetDir, relativePath);
            string? destFolder  = Path.GetDirectoryName(destFile);

            if (!string.IsNullOrEmpty(destFolder))
            {
                try { Directory.CreateDirectory(destFolder); }
                catch (UnauthorizedAccessException ex)
                {
                    throw new UnauthorizedAccessException(
                        $"Нет прав на создание папки: {destFolder}\n" +
                        "Запустите программу от имени администратора.", ex);
                }
            }

            try
            {
                // Use async copy via FileStream for responsiveness on large files
                await using var src  = new FileStream(srcFile,  FileMode.Open,   FileAccess.Read,  FileShare.Read,  65536, useAsync: true);
                await using var dest = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None,  65536, useAsync: true);
                await src.CopyToAsync(dest, ct).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException(
                    $"Нет прав на запись файла: {destFile}\n" +
                    "Запустите программу от имени администратора.", ex);
            }
            catch (IOException ex)
            {
                if (ex.Message.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) || 
                    ex.HResult == -2147024864) // 0x80070020
                {
                    string fileName = Path.GetFileName(destFile);
                    throw new IOException(
                        $"Не удалось заменить {fileName}: файл всё ещё занят другим процессом.\n\n" +
                        "Перезагрузите компьютер и повторите установку.", ex);
                }
                
                throw new IOException(
                    $"Ошибка при копировании файла: {relativePath}\n" +
                    $"Причина: {ex.Message}", ex);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static void CleanupStagingDirectory(string stagingDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(stagingDir)) return;
            if (!Directory.Exists(stagingDir)) return;

            // Defensive check: only delete if it's inside our TempRoot
            string fullStagingPath = Path.GetFullPath(stagingDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullTempRoot = Path.GetFullPath(TempRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!fullStagingPath.StartsWith(fullTempRoot, StringComparison.OrdinalIgnoreCase) || fullStagingPath.Equals(fullTempRoot, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warning($"Очистка пропущена: путь {stagingDir} не является подпапкой временного хранилища.");
                return;
            }

            // Safety: never delete the target ProgramData path
            string fullTarget = Path.GetFullPath(ProgramDataTarget).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullStagingPath.Equals(fullTarget, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Warning("Очистка пропущена: попытка удаления целевой папки ProgramData.");
                return;
            }

            Directory.Delete(fullStagingPath, true);
            AppLogger.Info($"Временная папка распаковки успешно удалена: {fullStagingPath}");
        }
        catch (Exception ex)
        {
            // Cleanup failure should not break the install/update result
            AppLogger.Warning($"Не удалось удалить временную папку {stagingDir}: {ex.Message}");
        }
    }

    /// <summary>
    /// Tries to parse a version tag like "v2025.04.10" from a file name segment such as
    /// "v2025.04.10_zapret-v2025.04.10" or "zapret-v2025.04.10".
    /// Returns <c>null</c> when no recognisable version pattern is found.
    /// </summary>
    private static string? TryParseVersion(string fileNameWithoutExt)
    {
        if (string.IsNullOrEmpty(fileNameWithoutExt))
            return null;

        // Look for a segment matching "v\d{4}\.\d{2}.\d{2}" style
        var parts = fileNameWithoutExt.Split('_', '-');
        foreach (var part in parts)
        {
            if (part.Length > 1 && part[0] == 'v' &&
                part[1..].Replace(".", "").All(char.IsDigit) &&
                part.Count(c => c == '.') >= 1)
            {
                return part;
            }
        }

        return null;
    }
}
