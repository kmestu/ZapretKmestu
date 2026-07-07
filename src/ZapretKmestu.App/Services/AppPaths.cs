using System.IO;

namespace ZapretKmestu.Services;

/// <summary>
/// Centralized, read-only paths used throughout the application.
/// No elevation is required for any of these paths.
/// ProgramData path creation is attempted but failures are swallowed gracefully.
/// </summary>
public static class AppPaths
{
    private const string AppFolderName = "Zapret Kmestu";

    // ── User-scoped paths (no elevation needed) ───────────────────────────────

    /// <summary>%AppData%\Zapret Kmestu</summary>
    public static readonly string RoamingAppDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName);

    /// <summary>%LocalAppData%\Zapret Kmestu</summary>
    public static readonly string LocalAppDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

    /// <summary>%LocalAppData%\Zapret Kmestu\Logs</summary>
    public static readonly string LogsDirectory =
        Path.Combine(LocalAppDataDirectory, "Logs");

    /// <summary>%AppData%\Zapret Kmestu\settings.json</summary>
    public static readonly string SettingsFilePath =
        Path.Combine(RoamingAppDataDirectory, "settings.json");

    /// <summary>%AppData%\Zapret Kmestu\last_autopick.json</summary>
    public static readonly string LastAutoPickResultsFilePath =
        Path.Combine(RoamingAppDataDirectory, "last_autopick.json");

    /// <summary>%LocalAppData%\Zapret Kmestu\Logs\app.log</summary>
    public static readonly string LogFilePath =
        Path.Combine(LogsDirectory, "app.log");

    /// <summary>%AppData%\Microsoft\Windows\Start Menu\Programs\Startup\Zapret Kmestu.lnk</summary>
    public static readonly string StartupShortcutPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), $"{AppFolderName}.lnk");

    // ── Machine-scoped paths (elevation might be needed on some systems) ──────

    /// <summary>C:\ProgramData\Zapret Kmestu</summary>
    public static readonly string ProgramDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppFolderName);

    /// <summary>C:\ProgramData\Zapret Kmestu\zapret</summary>
    public static readonly string ZapretDirectory =
        Path.Combine(ProgramDataDirectory, "zapret");

    /// <summary>C:\ProgramData\ZapretKmestu</summary>
    public static readonly string RuntimeProgramDataDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ZapretKmestu");

    /// <summary>C:\ProgramData\ZapretKmestu\zapret.args</summary>
    public static readonly string ZapretArgsFile =
        Path.Combine(RuntimeProgramDataDirectory, "zapret.args");
}
