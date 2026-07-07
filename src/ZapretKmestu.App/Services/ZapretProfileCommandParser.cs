using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Service for parsing zapret profile .bat files to extract winws.exe command and arguments.
/// Safe read-only operation.
/// </summary>
public class ZapretProfileCommandParser
{
    private readonly string _zapretDir;

    public ZapretProfileCommandParser(string zapretDir)
    {
        _zapretDir = zapretDir;
    }

    /// <summary>
    /// Parses the specified profile .bat file and extracts the command line.
    /// </summary>
    public ZapretProfileCommandInfo ParseProfile(string profileFilePath)
    {
        var result = new ZapretProfileCommandInfo
        {
            ProfileFilePath = profileFilePath,
            WinwsPath = Path.Combine(_zapretDir, "bin", "winws.exe"),
            Success = false
        };

        try
        {
            if (string.IsNullOrWhiteSpace(profileFilePath))
            {
                result.ErrorMessage = "Путь к профилю не указан.";
                return result;
            }

            if (!File.Exists(profileFilePath))
            {
                result.ErrorMessage = "Файл профиля не найден.";
                return result;
            }

            // Security check: ensure it's within the zapret directory
            var fullPath = Path.GetFullPath(profileFilePath);
            var zapretFullPath = Path.GetFullPath(_zapretDir);
            if (!fullPath.StartsWith(zapretFullPath, StringComparison.OrdinalIgnoreCase))
            {
                result.ErrorMessage = "Файл профиля находится вне разрешенной директории.";
                return result;
            }

            if (!File.Exists(result.WinwsPath))
            {
                result.ErrorMessage = "Исполняемый файл winws.exe не найден.";
                return result;
            }

            var lines = File.ReadAllLines(profileFilePath);
            var logicalLines = JoinMultilineBatchCommands(lines);
            
            // Find the first line containing winws.exe that is not a comment
            string? winwsLine = logicalLines
                .FirstOrDefault(l => 
                    !string.IsNullOrWhiteSpace(l) && 
                    !IsComment(l) &&
                    l.Contains("winws.exe", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrEmpty(winwsLine))
            {
                result.ErrorMessage = "В файле профиля не найдена команда winws.exe.";
                return result;
            }

            // Extract arguments
            string rawArgs = ExtractArguments(winwsLine);

            if (string.IsNullOrWhiteSpace(rawArgs))
            {
                result.ErrorMessage = "Не удалось извлечь аргументы из команды.";
                return result;
            }

            // Load variables from service.bat or defaults
            var gameFilters = LoadGameFilters();

            // Replace variables
            string binPath = Path.Combine(_zapretDir, "bin") + Path.DirectorySeparatorChar;
            string listsPath = Path.Combine(_zapretDir, "lists") + Path.DirectorySeparatorChar;

            // Perform replacements
            string processedArgs = rawArgs
                .Replace("%BIN%", binPath, StringComparison.OrdinalIgnoreCase)
                .Replace("%LISTS%", listsPath, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilter%", gameFilters.General, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilterTCP%", gameFilters.Tcp, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilterUDP%", gameFilters.Udp, StringComparison.OrdinalIgnoreCase);

            result.Arguments = processedArgs.Trim();
            result.FullCommandPreview = $"\"{result.WinwsPath}\" {result.Arguments}";
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = $"Ошибка при чтении профиля: {ex.Message}";
        }

        return result;
    }

    private bool IsComment(string line)
    {
        string trimmed = line.TrimStart();
        return trimmed.StartsWith("::") || trimmed.StartsWith("rem ", StringComparison.OrdinalIgnoreCase);
    }

    private List<string> JoinMultilineBatchCommands(string[] lines)
    {
        var joined = new List<string>();
        string currentLine = "";

        foreach (var line in lines)
        {
            string trimmedEnd = line.TrimEnd();
            bool hasContinuation = trimmedEnd.EndsWith("^");

            if (hasContinuation)
            {
                // Remove the ^ and keep everything before it
                currentLine += trimmedEnd.Substring(0, trimmedEnd.Length - 1);
            }
            else
            {
                currentLine += line;
                joined.Add(currentLine);
                currentLine = "";
            }
        }

        if (!string.IsNullOrEmpty(currentLine))
        {
            joined.Add(currentLine);
        }

        return joined;
    }

    private string ExtractArguments(string line)
    {
        int winwsIndex = line.IndexOf("winws.exe", StringComparison.OrdinalIgnoreCase);
        if (winwsIndex == -1) return "";

        int endOfExec = -1;

        // Check for quotes: "...winws.exe"
        int lastQuoteBefore = line.LastIndexOf('"', winwsIndex);
        if (lastQuoteBefore != -1)
        {
            int firstQuoteAfter = line.IndexOf('"', winwsIndex);
            if (firstQuoteAfter != -1)
            {
                endOfExec = firstQuoteAfter;
            }
        }

        if (endOfExec == -1)
        {
            // Not quoted, find the first space after winws.exe
            int spaceAfter = line.IndexOf(' ', winwsIndex);
            if (spaceAfter != -1)
            {
                endOfExec = spaceAfter - 1;
            }
            else
            {
                // No space, command ends at the end of line
                endOfExec = line.Length - 1;
            }
        }

        if (endOfExec >= line.Length - 1) return "";

        return line.Substring(endOfExec + 1).Trim();
    }

    private class GameFilters
    {
        public string Tcp { get; }
        public string Udp { get; }
        public string General { get; }
        
        public GameFilters(string tcp, string udp, string general)
        {
            Tcp = tcp;
            Udp = udp;
            General = general;
        }
    }

    private GameFilters LoadGameFilters()
    {
        string gameFlagFile = Path.Combine(_zapretDir, "utils", "game_filter.enabled");
        if (!File.Exists(gameFlagFile))
        {
            return new GameFilters("12", "12", "12");
        }

        try
        {
            string? mode = File.ReadLines(gameFlagFile).FirstOrDefault()?.Trim().ToLowerInvariant();
            
            if (mode == "all")
                return new GameFilters("1024-65535", "1024-65535", "1024-65535");
            if (mode == "tcp")
                return new GameFilters("1024-65535", "12", "1024-65535");
            if (mode == "udp")
                return new GameFilters("12", "1024-65535", "1024-65535");
        }
        catch
        {
            // Safe fallback
        }

        return new GameFilters("12", "12", "12");
    }
}

