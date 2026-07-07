using System.IO;
using System.Windows;
using Microsoft.Win32;
using System.Threading;
using ZapretKmestu.Models;
using ZapretKmestu.Services;

namespace ZapretKmestu;

/// <summary>
/// Entry point for the ZapretKmestu application.
/// Initializes paths, logger, and settings before the first window opens.
/// </summary>
public partial class App : System.Windows.Application
{
    /// <summary>Globally accessible settings instance.</summary>
    public static AppSettings Settings { get; private set; } = new();

    private static Mutex? _singleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 0. Single Instance Guard - Acquire immediately. 
        // Use 'Local\' to ensure it works within the user session and avoid elevation-specific global issues.
        _singleInstanceMutex = new Mutex(true, @"Local\ZapretKmestu.SingleInstance.Mutex", out bool createdNew);

        if (!createdNew)
        {
            // Another instance is already running.
            // Try to signal it to restore from tray.
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\ZapretKmestu.ActivationEvent", out var waitHandle))
                {
                    waitHandle.Set();
                    waitHandle.Dispose();
                }
            }
            catch
            {
                // Ignore signaling errors
            }

            // Dispose the unused mutex reference and shut down before any window or tray icon creation.
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
            
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 1. Initialize logger first so subsequent calls can log
        AppLogger.Initialize(AppPaths.LogFilePath);
        AppLogger.Info("=== Zapret Kmestu запущен ===");
        
        var adminService = new AdminService();
        bool isAdmin = adminService.IsRunningAsAdministrator();
        AppLogger.Info($"Версия: v0.1 beta  |  .NET 8 WPF  |  Admin: {(isAdmin ? "YES" : "NO")}");

        // 2. Load settings
        Settings = SettingsService.Load(AppPaths.SettingsFilePath);
        AppLogger.Info($"Настройки загружены из: {AppPaths.SettingsFilePath}");

        // 3. Apply theme early
        ApplyTheme(Settings.Theme);

        // 4. Ensure user-scoped directories exist
        TryEnsureUserFolders();

        // 5. Attempt ProgramData path creation (may fail without elevation — that's OK)
        TryEnsureProgramDataFolder();

        AppLogger.Info($"Журнал: {AppPaths.LogFilePath}");
        AppLogger.Info("Инициализация завершена. Запуск интерфейса...");

        // 6. Create and manage MainWindow
        var mainWindow = new MainWindow();
        this.MainWindow = mainWindow;

        var args = Environment.GetCommandLineArgs();
        bool startInTray = args.Contains("--tray", StringComparer.OrdinalIgnoreCase);

        if (startInTray)
        {
            AppLogger.Info("Запуск в скрытом режиме (--tray). Окно не отображается.");
            // We set ShowInTaskbar to false here to ensure it's hidden even if WPF tries to show it
            mainWindow.ShowInTaskbar = false;
        }
        else
        {
            mainWindow.Show();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 1. Reliable tray cleanup: ensure icon is disposed even if window was never shown
        if (this.MainWindow is MainWindow mw)
        {
            mw.CleanupTrayIcon();
        }

        AppLogger.Info("=== Zapret Kmestu завершён ===");

        // 2. Release Single Instance Mutex
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }

        base.OnExit(e);
    }

    // ── Folder helpers ────────────────────────────────────────────────────────

    private static void TryEnsureUserFolders()
    {
        TryCreate(AppPaths.RoamingAppDataDirectory, "Roaming AppData");
        TryCreate(AppPaths.LocalAppDataDirectory,   "Local AppData");
        TryCreate(AppPaths.LogsDirectory,           "Logs");
    }

    private static void TryEnsureProgramDataFolder()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.ZapretDirectory);
            AppLogger.Info($"ProgramData: {AppPaths.ZapretDirectory}");
        }
        catch (Exception ex)
        {
            // Not an error — may require elevation. Logged as a warning, never shown as a crash.
            AppLogger.Warning($"Не удалось создать папку ProgramData (может потребоваться запуск от администратора): {ex.Message}");
        }
    }

    private static void TryCreate(string path, string label)
    {
        try
        {
            Directory.CreateDirectory(path);
            AppLogger.Info($"Папка проверена [{label}]: {path}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Не удалось создать папку [{label}]: {ex.Message}");
        }
    }

    // ── Theme management ──────────────────────────────────────────────────────

    public static void ApplyTheme(string theme)
    {
        string themeName = theme.ToLower() switch
        {
            "light"  => "LightTheme",
            "dark"   => "DarkTheme",
            "system" => DetectWindowsTheme(),
            _        => "LightTheme"
        };

        try
        {
            var uri = new Uri($"Themes/{themeName}.xaml", UriKind.Relative);
            var newDict = (ResourceDictionary)LoadComponent(uri);

            var dictionaries = Current.Resources.MergedDictionaries;
            
            // Remove existing theme dictionaries while keeping Icons.xaml (at index 0)
            for (int i = dictionaries.Count - 1; i >= 0; i--)
            {
                if (dictionaries[i].Source != null && dictionaries[i].Source.OriginalString.Contains("Theme.xaml"))
                {
                    dictionaries.RemoveAt(i);
                }
            }

            dictionaries.Add(newDict);
            
            AppLogger.Info($"Тема применена: {theme} ({themeName})");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при смене темы: {ex.Message}");
        }
    }

    public static string DetectWindowsTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key != null)
            {
                object? val = key.GetValue("AppsUseLightTheme");
                if (val is int i && i == 0)
                {
                    return "DarkTheme";
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Не удалось определить системную тему: {ex.Message}");
        }
        
        return "LightTheme";
    }

    public static string GetCurrentResolvedThemeName()
    {
        string theme = Settings.Theme.ToLower();
        if (theme == "system")
        {
            return DetectWindowsTheme() == "DarkTheme" ? "dark" : "light";
        }
        return theme;
    }
}
