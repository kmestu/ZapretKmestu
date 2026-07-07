using ZapretKmestu.Models;
using System.IO;

namespace ZapretKmestu.Services;

/// <summary>
/// Orchestrates the installation of zapret by coordinating release metadata fetching,
/// downloading, and extraction.
/// </summary>
public sealed class ZapretInstallerService
{
    private readonly GitHubReleaseService _releaseService;
    private readonly ZapretDownloadService _downloadService;
    private readonly ZapretExtractionService _extractionService;
    private readonly AppSettings _settings;

    public ZapretInstallerService(
        GitHubReleaseService releaseService,
        ZapretDownloadService downloadService,
        ZapretExtractionService extractionService,
        AppSettings settings)
    {
        _releaseService = releaseService;
        _downloadService = downloadService;
        _extractionService = extractionService;
        _settings = settings;
    }

    /// <summary>
    /// Performs the full installation flow: check release -> download -> extract -> update settings.
    /// </summary>
    public async Task<ZapretInstallResult> InstallAsync(
        IProgress<InstallProgressInfo>? progress, 
        CancellationToken cancellationToken)
    {
        try
        {
            AppLogger.Info("Начало процесса установки zapret.");

            // 1. report "Проверяем последнюю версию zapret..."
            progress?.Report(new InstallProgressInfo { Step = "Проверяем последнюю версию zapret..." });

            // 2. call GitHubReleaseService
            var release = await _releaseService.GetLatestReleaseAsync(cancellationToken).ConfigureAwait(false);
            AppLogger.Info($"Последний доступный релиз: {release.TagName}");

            // 3. report "Скачиваем zapret..."
            progress?.Report(new InstallProgressInfo { Step = "Скачиваем zapret..." });

            // 4. call ZapretDownloadService
            string zipPath = await _downloadService.DownloadZipAsync(release, progress, cancellationToken).ConfigureAwait(false);
            AppLogger.Info($"Архив успешно скачан: {zipPath}");

            // 5. report "Распаковываем и проверяем файлы..."
            progress?.Report(new InstallProgressInfo { Step = "Распаковываем и проверяем файлы..." });

            // 6. call ZapretExtractionService
            var extractionResult = await _extractionService.ExtractAndInstallAsync(zipPath, progress, cancellationToken).ConfigureAwait(false);

            if (!extractionResult.IsValid)
            {
                AppLogger.Error("Распакованные файлы не прошли валидацию.");
                return ZapretInstallResult.Failed("Файлы после распаковки не прошли проверку.");
            }

            // 7. update AppSettings:
            //    IsZapretInstalled = true
            //    InstalledZapretVersion = latest release tag
            //    ZapretPath = AppPaths.ZapretDirectory
            _settings.IsZapretInstalled = true;
            _settings.InstalledZapretVersion = release.TagName;
            _settings.ZapretPath = AppPaths.ZapretDirectory;

            // 8. save settings
            SettingsService.Save(_settings, AppPaths.SettingsFilePath);
            AppLogger.Info("Настройки приложения обновлены.");

            // 9. log useful events
            AppLogger.Info($"Установка zapret версии {release.TagName} успешно завершена в {AppPaths.ZapretDirectory}");

            // 10. return ZapretInstallResult
            return ZapretInstallResult.Successful(release.TagName, AppPaths.ZapretDirectory);
        }
        catch (OperationCanceledException)
        {
            AppLogger.Warning("Установка zapret отменена пользователем.");
            return ZapretInstallResult.Failed("Операция отменена.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при установке zapret: {ex.Message}");
            return ZapretInstallResult.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Validates the local installation by checking for the presence of the main directory 
    /// and core files.
    /// </summary>
    public bool ValidateLocalInstall()
    {
        string zapretDir = AppPaths.ZapretDirectory;
        if (!Directory.Exists(zapretDir))
        {
            AppLogger.Warning($"Валидация провалена: директория {zapretDir} не найдена.");
            return false;
        }

        // Essential files for Flowseal release structure
        string[] requiredFiles = 
        {
            Path.Combine(zapretDir, "service.bat"),
            Path.Combine(zapretDir, "bin", "winws.exe")
        };

        foreach (var file in requiredFiles)
        {
            if (!File.Exists(file))
            {
                AppLogger.Warning($"Валидация провалена: отсутствует файл {file}");
                return false;
            }
        }

        // Essential directories
        string listsDir = Path.Combine(zapretDir, "lists");
        if (!Directory.Exists(listsDir))
        {
            AppLogger.Warning($"Валидация провалена: отсутствует директория {listsDir}");
            return false;
        }

        return true;
    }
}
