using System;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace ZapretKmestu.Services;

/// <summary>
/// Service for checking application execution privileges.
/// </summary>
public class AdminService
{
    /// <summary>
    /// Checks if the application is running with administrative privileges.
    /// </summary>
    public bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Ошибка при проверке прав администратора: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Attempts to restart the current application with administrative privileges.
    /// Returns true if the process was successfully started, false otherwise (e.g., UAC cancelled).
    /// </summary>
    public bool TryRestartAsAdministrator()
    {
        try
        {
            // Preserve existing command-line arguments (like --tray) during elevation.
            var args = Environment.GetCommandLineArgs().Skip(1);
            var argumentsString = string.Join(" ", args.Select(a => $"\"{a}\""));

            var processInfo = new ProcessStartInfo
            {
                FileName = Environment.ProcessPath,
                Arguments = argumentsString,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(processInfo);
            return true;
        }
        catch (Exception ex)
        {
            // This catch block handles cases where the user clicks "No" on the UAC prompt
            // or if there is another error starting the elevated process.
            AppLogger.Warning($"Перезапуск от имени администратора отменён или не удался: {ex.Message}");
            return false;
        }
    }
}
