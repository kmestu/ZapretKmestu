using System.IO;
using System.Text.Json;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Loads and saves <see cref="AppSettings"/> to/from a JSON file.
/// All I/O errors are handled safely — the app must never crash due to settings.
/// </summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented        = true,
        PropertyNameCaseInsensitive = true,
    };

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads settings from disk. If the file is missing, returns defaults.
    /// If the file is corrupted, backs it up and returns defaults.
    /// Never throws.
    /// </summary>
    public static AppSettings Load(string settingsFilePath)
    {
        TryEnsureDirectory(settingsFilePath);

        if (!File.Exists(settingsFilePath))
        {
            AppLogger.Info("Файл настроек не найден — создаются настройки по умолчанию.");
            var defaults = new AppSettings();
            TrySave(defaults, settingsFilePath);
            return defaults;
        }

        try
        {
            var json     = File.ReadAllText(settingsFilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, _jsonOptions);
            if (settings == null) throw new JsonException("Deserialized to null.");
            AppLogger.Info("Настройки загружены успешно.");
            return settings;
        }
        catch (Exception ex)
        {
            // Corrupt or invalid JSON — back it up, start fresh
            BackupCorruptFile(settingsFilePath);
            AppLogger.Warning($"Файл настроек повреждён — создан резервный файл, используются настройки по умолчанию. ({ex.Message})");
            var defaults = new AppSettings();
            TrySave(defaults, settingsFilePath);
            return defaults;
        }
    }

    /// <summary>
    /// Saves the current settings to disk. Never throws.
    /// </summary>
    public static void Save(AppSettings settings, string settingsFilePath)
    {
        TrySave(settings, settingsFilePath);
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private static void TrySave(AppSettings settings, string settingsFilePath)
    {
        try
        {
            TryEnsureDirectory(settingsFilePath);
            var json = JsonSerializer.Serialize(settings, _jsonOptions);
            File.WriteAllText(settingsFilePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Не удалось сохранить настройки: {ex.Message}");
        }
    }

    private static void TryEnsureDirectory(string filePath)
    {
        try
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
        }
        catch
        {
            // Silently swallow
        }
    }

    private static void BackupCorruptFile(string filePath)
    {
        try
        {
            var ts     = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var backup = Path.Combine(
                Path.GetDirectoryName(filePath) ?? "",
                $"settings.corrupt-{ts}.json");
            File.Copy(filePath, backup, overwrite: true);
        }
        catch
        {
            // Best-effort
        }
    }
}
