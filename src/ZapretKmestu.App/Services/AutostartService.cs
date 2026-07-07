using System.IO;
using System.Diagnostics;
using System.Linq;
using ZapretKmestu.Models;
using System.Collections.Generic;

namespace ZapretKmestu.Services;

/// <summary>
/// Manages application autostart via Windows Task Scheduler for reliable elevated execution.
/// </summary>
public class AutostartService
{
    private readonly AppSettings _settings;

    public AutostartService(AppSettings settings)
    {
        _settings = settings;
    }

    /// <summary>
    /// Checks if the scheduled task actually exists.
    /// Named 'IsShortcutPresent' for backward compatibility with settings UI binding.
    /// </summary>
    public bool IsShortcutPresent()
    {
        string taskName = GetTaskName();
        var (exitCode, _) = RunSchtasks("/Query", "/TN", taskName);
        return exitCode == 0;
    }

    /// <summary>
    /// Synchronizes the startup state with the provided boolean.
    /// </summary>
    public bool SetAutostart(bool enable)
    {
        try
        {
            // Always clean up the old legacy startup shortcut if it exists
            DeleteLegacyShortcut();

            if (enable)
            {
                bool success = CreateScheduledTask();
                if (success) AppLogger.Info("Автозапуск через Планировщик задач включён.");
                return success;
            }
            else
            {
                bool success = DeleteScheduledTask();
                if (success) AppLogger.Info("Автозапуск через Планировщик задач выключен.");
                return success;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Не удалось изменить состояние автозапуска: {ex.Message}");
            return false;
        }
    }

    private bool CreateScheduledTask()
    {
        string taskName = GetTaskName();
        string? exePath = GetExecutablePath();

        if (string.IsNullOrEmpty(exePath))
        {
            AppLogger.Error("Автозапуск: не удалось определить путь к исполняемому файлу.");
            return false;
        }

        // We use ArgumentList to ensure robust parameter passing to schtasks.exe.
        // The task action should be exactly: "C:\path\to\exe" --tray
        string taskRunCommand = $"\"{exePath}\" --tray";
        
        var (exitCode, output) = RunSchtasks("/Create", "/TN", taskName, "/TR", taskRunCommand, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F");
        
        if (exitCode != 0)
        {
            AppLogger.Error($"Ошибка при создании задачи планировщика (Код {exitCode}): {output}");
            return false;
        }

        return true;
    }

    private bool DeleteScheduledTask()
    {
        string taskName = GetTaskName();
        var (exitCode, output) = RunSchtasks("/Delete", "/TN", taskName, "/F");
        
        // Exit code 1 with "ERROR: The system cannot find the file specified" is considered success when deleting.
        // Check for common error strings in both English and Russian.
        if (exitCode != 0 && 
            !output.Contains("0x80070002") && 
            !output.Contains("не найдена", StringComparison.OrdinalIgnoreCase) && 
            !output.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Error($"Ошибка при удалении задачи планировщика (Код {exitCode}): {output}");
            return false;
        }

        return true;
    }

    private string GetTaskName()
    {
        string userName = Environment.UserName;
        // Filter out characters that are not safe for a task name
        char[] invalidChars = { '\\', '/', ':', '*', '?', '"', '<', '>', '|', ' ' };
        string safeUserName = new string(userName.Where(c => !invalidChars.Contains(c)).ToArray());
        return $"ZapretKmestu_Autostart_{safeUserName}";
    }

    private string? GetExecutablePath()
    {
        string? exePath = Environment.ProcessPath;
        
        // If running under dotnet or null, fallback to entry assembly
        if (string.IsNullOrEmpty(exePath) || Path.GetFileNameWithoutExtension(exePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            string assemblyName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "ZapretKmestu.App";
            exePath = Path.Combine(AppContext.BaseDirectory, assemblyName + ".exe");
        }

        if (File.Exists(exePath))
        {
            return exePath;
        }

        return null;
    }

    private (int ExitCode, string Output) RunSchtasks(params string[] args)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            // Use ArgumentList for robust argument handling, avoiding shell escaping issues.
            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }
            
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            
            if (!process.WaitForExit(10000))
            {
                process.Kill();
                return (-2, "schtasks.exe timed out.");
            }
            
            return (process.ExitCode, output + error);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private void DeleteLegacyShortcut()
    {
        try
        {
            if (File.Exists(AppPaths.StartupShortcutPath))
            {
                File.Delete(AppPaths.StartupShortcutPath);
                AppLogger.Info("Старый ярлык автозагрузки удалён.");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Не удалось удалить старый ярлык автозагрузки: {ex.Message}");
        }
    }
}
