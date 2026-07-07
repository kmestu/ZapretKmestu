using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Service for managing the "zapret" Windows service lifecycle.
/// Requires administrative privileges for most operations.
/// </summary>
public class ZapretServiceManager
{
    private const string ServiceName = "zapret";
    private readonly AdminService _adminService;
    private readonly ZapretServiceStatusService _statusService;
    private readonly ZapretProfileCommandParser _commandParser;
    private readonly AppSettings _settings;

    public ZapretServiceManager(
        AdminService adminService,
        ZapretServiceStatusService statusService,
        ZapretProfileCommandParser commandParser,
        AppSettings settings)
    {
        _adminService = adminService;
        _statusService = statusService;
        _commandParser = commandParser;
        _settings = settings;
    }

    /// <summary>
    /// Reinstalls the "zapret" service using the currently selected profile.
    /// Stops and deletes the service if it already exists.
    /// Does NOT start the service after creation.
    /// </summary>
    public async Task<ZapretServiceActionResult> ReinstallAsync()
    {
        AppLogger.Info("Начата переустановка службы zapret.");

        // 1. Check admin rights
        if (!_adminService.IsRunningAsAdministrator())
        {
            AppLogger.Warning("Переустановка службы отклонена: приложение запущено без прав администратора.");
            return ZapretServiceActionResult.Error("Требуются права администратора.");
        }

        // 2. Validate installation and profile
        if (!_settings.IsZapretInstalled)
        {
            return ZapretServiceActionResult.Error("zapret не установлен. Сначала установите файлы.");
        }

        if (string.IsNullOrEmpty(_settings.SelectedProfile))
        {
            return ZapretServiceActionResult.Error("Профиль не выбран в настройках.");
        }

        string profilePath = Path.Combine(AppPaths.ZapretDirectory, _settings.SelectedProfile);
        var commandInfo = _commandParser.ParseProfile(profilePath);

        if (!commandInfo.Success)
        {
            AppLogger.Warning($"Ошибка разбора профиля: {commandInfo.ErrorMessage}");
            return ZapretServiceActionResult.Error($"Не удалось разобрать команду профиля: {commandInfo.ErrorMessage}");
        }

        // Validate winws.exe exists (redundant but safe)
        if (!File.Exists(commandInfo.WinwsPath))
        {
            return ZapretServiceActionResult.Error("Исполняемый файл winws.exe не найден.");
        }

        try
        {
            // 3. Handle existing service
            var status = _statusService.GetStatus();
            if (status.Exists)
            {
                if (status.IsRunning)
                {
                    AppLogger.Info("Служба zapret запущена. Останавливаем...");
                    var stopRes = await RunScAsync("stop", ServiceName);
                    if (!stopRes.Success && !stopRes.Output.Contains("1062")) // 1062 = service not started
                    {
                        AppLogger.Warning($"Предупреждение при остановке службы: {stopRes.Output}");
                    }
                }

                AppLogger.Info("Удаление существующей службы zapret...");
                var delRes = await RunScAsync("delete", ServiceName);
                if (!delRes.Success)
                {
                    AppLogger.Warning($"Предупреждение при удалении службы: {delRes.Output}");
                }

                // Wait for service to be removed (SCM latency)
                AppLogger.Info("Ожидание удаления службы из системы...");
                int attempts = 0;
                while (attempts < 10)
                {
                    await Task.Delay(500);
                    if (!_statusService.GetStatus().Exists) break;
                    attempts++;
                }
                
                if (_statusService.GetStatus().Exists)
                {
                    return ZapretServiceActionResult.Error("Не удалось удалить старую службу. Возможно, она заблокирована SCM.");
                }
            }

            // 3.5 Prepare Flowseal-like environment (cleanup and network config)
            await PrepareFlowsealLikeEnvironmentAsync("перед переустановкой");

            // 4. Create service
            // The value for binPath= must be quoted if it contains spaces.
            // We use the @args file syntax to avoid command line length limits.
            
            AppLogger.Info("Подготовка конфигурации zapret...");
            try
            {
                if (string.IsNullOrWhiteSpace(commandInfo.Arguments))
                {
                    return ZapretServiceActionResult.Error("Список аргументов пуст.");
                }

                if (!Directory.Exists(AppPaths.ZapretDirectory))
                {
                    return ZapretServiceActionResult.Error("Директория zapret не найдена.");
                }

                // Split command line into tokens (one per line) for the reference file
                var argumentTokens = SplitCommandLineArguments(commandInfo.Arguments);

                if (argumentTokens.Count == 0)
                {
                    return ZapretServiceActionResult.Error("Не удалось получить список аргументов.");
                }

                // 4a. Prepare list files (create missing optional user lists)
                var prepError = PrepareReferencedListFiles(argumentTokens);
                if (prepError != null)
                {
                    return ZapretServiceActionResult.Error($"Не удалось подготовить файлы списков zapret: {prepError}");
                }

                int maxLineLength = 0;
                foreach (var line in argumentTokens)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        return ZapretServiceActionResult.Error("Обнаружен пустой или пустой аргумент.");
                    }

                    if (line.Contains("%BIN%") || line.Contains("%LISTS%") || 
                        line.Contains("%GameFilterTCP%") || line.Contains("%GameFilterUDP%"))
                    {
                        return ZapretServiceActionResult.Error($"Аргумент содержит неразрешенную переменную: {line}");
                    }

                    if (line.Length > maxLineLength) maxLineLength = line.Length;
                }

                // Ensure parent directory for reference args file exists
                string? argsDir = Path.GetDirectoryName(AppPaths.ZapretArgsFile);
                if (argsDir != null && !Directory.Exists(argsDir))
                {
                    Directory.CreateDirectory(argsDir);
                }

                // Write arguments to reference file (UTF-8 without BOM)
                File.WriteAllLines(AppPaths.ZapretArgsFile, argumentTokens, new UTF8Encoding(false));

                AppLogger.Info($"Файл аргументов (для справки) обновлен: {AppPaths.ZapretArgsFile}");
                AppLogger.Info($"Оригинальная длина аргументов: {commandInfo.Arguments.Length} симв.");
                AppLogger.Info($"Записано строк аргументов: {argumentTokens.Count}");
                AppLogger.Info($"Максимальная длина строки: {maxLineLength} симв.");
            }
            catch (Exception ex)
            {
                AppLogger.Error($"Ошибка при подготовке файла параметров: {ex.Message}");
                return ZapretServiceActionResult.Error($"Не удалось подготовить файл параметров zapret: {ex.Message}");
            }

            // Use inline arguments for service binPath (Flowseal-compatible mode)
            AppLogger.Info("Используется режим встроенных аргументов службы, совместимый с Flowseal.");
            
            // Expected binPathValue shape: "\"C:\...\winws.exe\" <args>"
            // Note: commandInfo.Arguments contains space-separated tokens with preserved quotes.
            string binPathValue = $"\"{commandInfo.WinwsPath}\" {commandInfo.Arguments}";

            AppLogger.Info("Создание службы zapret...");
            AppLogger.Info($"Итоговая длина binPath (inline): {binPathValue.Length} симв.");
            if (binPathValue.Length > 2048)
            {
                AppLogger.Warning("ПРЕДУПРЕЖДЕНИЕ: Командная строка службы очень длинная. Это может вызвать проблемы совместимости.");
            }
            
            // We use sc.exe create. 
            // Note: each parameter name (e.g., binPath=) and its value are passed 
            // as separate items in the ArgumentList for more robust parsing by sc.exe.
            var createRes = await RunScAsync(
                "create", 
                ServiceName,
                "binPath=", binPathValue,
                "start=", "demand",
                "DisplayName=", ServiceName
            );

            if (!createRes.Success)
            {
                AppLogger.Error($"Ошибка sc create: {createRes.Output}");
                return ZapretServiceActionResult.Error($"Не удалось создать службу: {createRes.Output}");
            }

            AppLogger.Info("Служба zapret успешно переустановлена.");
            return ZapretServiceActionResult.Ok("Служба zapret переустановлена. Теперь можно включить обход.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Критическая ошибка при переустановке службы: {ex.Message}");
            return ZapretServiceActionResult.Error($"Произошла ошибка: {ex.Message}");
        }
    }

    /// <summary>
    /// Safely stops and deletes the "zapret" Windows service.
    /// </summary>
    public async Task<ZapretServiceActionResult> UninstallServiceAsync()
    {
        AppLogger.Info("Запрошено удаление службы zapret.");

        if (!_adminService.IsRunningAsAdministrator())
        {
            return ZapretServiceActionResult.Error("Требуются права администратора.");
        }

        try
        {
            var status = _statusService.GetStatus();
            if (!status.Exists)
            {
                return ZapretServiceActionResult.Ok("Служба zapret не установлена.");
            }

            if (status.IsRunning)
            {
                AppLogger.Info("Служба zapret запущена. Останавливаем...");
                await RunScAsync("stop", ServiceName);
            }

            AppLogger.Info("Удаление службы zapret...");
            var delRes = await RunScAsync("delete", ServiceName);
            
            if (!delRes.Success && !delRes.Output.Contains("1060")) // 1060 = service does not exist
            {
                return ZapretServiceActionResult.Error($"Не удалось удалить службу: {delRes.Output}");
            }

            // Wait for service to be removed
            int attempts = 0;
            while (attempts < 10)
            {
                await Task.Delay(500);
                if (!_statusService.GetStatus().Exists) break;
                attempts++;
            }

            AppLogger.Info("Служба zapret успешно удалена.");
            return ZapretServiceActionResult.Ok("Служба zapret удалена.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при удалении службы: {ex.Message}");
            return ZapretServiceActionResult.Error($"Ошибка: {ex.Message}");
        }
    }


    /// <summary>
    /// Starts the "zapret" service using sc.exe.
    /// </summary>
    public async Task<ZapretServiceActionResult> StartAsync()
    {
        AppLogger.Info("Запрошено включение обхода (запуск службы zapret).");

        if (!_adminService.IsRunningAsAdministrator())
        {
            return ZapretServiceActionResult.Error("Требуются права администратора.");
        }

        var status = _statusService.GetStatus();
        if (!status.Exists)
        {
            return ZapretServiceActionResult.Error("Служба zapret не установлена. Сначала переустановите службу в разделе 'Эксперт'.");
        }

        if (status.IsRunning)
        {
            return ZapretServiceActionResult.Ok("Обход уже включён.");
        }

        try
        {
            // 0. Prepare Flowseal-like environment (cleanup and network config)
            await PrepareFlowsealLikeEnvironmentAsync("перед запуском");

            AppLogger.Info("Запуск службы zapret...");
            var res = await RunScAsync("start", ServiceName);

            if (!res.Success)
            {
                AppLogger.Error($"Ошибка sc start: {res.Output}");
                await RunPostFailureDiagnosticsAsync();
                return ZapretServiceActionResult.Error($"Не удалось включить обход: {res.Output}");
            }

            // Wait for service to become running
            int attempts = 0;
            ZapretServiceStatusInfo finalStatus = status;
            while (attempts < 10)
            {
                await Task.Delay(500);
                finalStatus = _statusService.GetStatus();
                if (finalStatus.IsRunning)
                {
                    AppLogger.Info("Служба zapret успешно запущена.");
                    return ZapretServiceActionResult.Ok("Обход включён.");
                }
                attempts++;
            }

            AppLogger.Warning($"Служба не перешла в состояние Running вовремя. Финальный статус: {finalStatus.StatusText}");
            
            // --- Диагностика после неудачного ожидания ---
            await RunPostFailureDiagnosticsAsync();

            // Diagnostic check: is winws.exe actually running?
            if (IsWinwsProcessRunning())
            {
                AppLogger.Info("Диагностика: процесс winws.exe запущен по верному пути, несмотря на статус службы.");
                return ZapretServiceActionResult.Ok("Обход включён. Процесс zapret запущен, хотя служба не успела обновить статус.");
            }

            return ZapretServiceActionResult.Error("Служба запущена, но не перешла в состояние 'Выполняется' вовремя.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при включении обхода: {ex.Message}");
            return ZapretServiceActionResult.Error($"Ошибка: {ex.Message}");
        }
    }

    /// <summary>
    /// Stops the "zapret" service using sc.exe.
    /// </summary>
    public async Task<ZapretServiceActionResult> StopAsync()
    {
        AppLogger.Info("Запрошено выключение обхода (остановка службы zapret).");

        if (!_adminService.IsRunningAsAdministrator())
        {
            return ZapretServiceActionResult.Error("Требуются права администратора.");
        }

        var status = _statusService.GetStatus();
        if (!status.Exists)
        {
            return ZapretServiceActionResult.Ok("Служба zapret не установлена.");
        }

        if (!status.IsRunning)
        {
            return ZapretServiceActionResult.Ok("Обход уже выключен.");
        }

        try
        {
            AppLogger.Info("Остановка службы zapret через sc stop...");
            var res = await RunScAsync("stop", ServiceName);

            if (!res.Success)
            {
                AppLogger.Error($"Ошибка при остановке службы: {res.Output}");
                return ZapretServiceActionResult.Error($"Не удалось выключить обход: {res.Output}");
            }

            // Wait for service to become stopped
            int attempts = 0;
            while (attempts < 10)
            {
                await Task.Delay(500);
                if (!_statusService.GetStatus().IsRunning)
                {
                    AppLogger.Info("Служба zapret успешно остановлена.");
                    return ZapretServiceActionResult.Ok("Обход выключен.");
                }
                attempts++;
            }

            return ZapretServiceActionResult.Error("Служба остановлена, но не перешла в состояние 'Остановлена' вовремя.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при выключении обхода: {ex.Message}");
            return ZapretServiceActionResult.Error($"Ошибка: {ex.Message}");
        }
    }

    /// <summary>
    /// Diagnostic method to check if winws.exe is actually running from the expected path.
    /// Does NOT start or kill any processes.
    /// </summary>
    public bool IsWinwsProcessRunning()
    {
        string expectedPath = Path.Combine(AppPaths.ZapretDirectory, "bin", "winws.exe");
        
        bool anyWinwsFound = false;
        bool matchingWinwsFound = false;
        bool pathCheckUnavailable = false;

        try
        {
            var processes = Process.GetProcessesByName("winws");
            if (processes.Length > 0)
            {
                anyWinwsFound = true;
                foreach (var proc in processes)
                {
                    try
                    {
                        // MainModule might throw Win32Exception (Access Denied) or InvalidOperationException
                        string? filePath = proc.MainModule?.FileName;
                        if (string.Equals(filePath, expectedPath, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingWinwsFound = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        pathCheckUnavailable = true;
                        AppLogger.Info($"Не удалось проверить путь процесса winws (PID: {proc.Id}): {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при поиске процессов winws: {ex.Message}");
        }

        AppLogger.Info($"Результат проверки процессов winws: " +
                       $"любой winws: {anyWinwsFound}, " +
                       $"совпадающий путь: {matchingWinwsFound}, " +
                       $"проверка пути недоступна: {pathCheckUnavailable}");
        
        return matchingWinwsFound;
    }



    /// <summary>
    /// Prepares referenced list files: creates missing optional user lists and validates mandatory ones.
    /// </summary>
    private string? PrepareReferencedListFiles(IReadOnlyList<string> argumentTokens)
    {
        AppLogger.Info("Начата подготовка файлов списков zapret...");
        int checkedFilesCount = 0;
        int createdFilesCount = 0;

        string listsDirectory = Path.Combine(AppPaths.ZapretDirectory, "lists");
        string zapretDir = AppPaths.ZapretDirectory;
        if (!zapretDir.EndsWith(Path.DirectorySeparatorChar.ToString()))
            zapretDir += Path.DirectorySeparatorChar;

        foreach (var token in argumentTokens)
        {
            string? pathValue = null;
            if (token.StartsWith("--hostlist=", StringComparison.OrdinalIgnoreCase))
                pathValue = token.Substring("--hostlist=".Length);
            else if (token.StartsWith("--hostlist-exclude=", StringComparison.OrdinalIgnoreCase))
                pathValue = token.Substring("--hostlist-exclude=".Length);
            else if (token.StartsWith("--ipset=", StringComparison.OrdinalIgnoreCase))
                pathValue = token.Substring("--ipset=".Length);
            else if (token.StartsWith("--ipset-exclude=", StringComparison.OrdinalIgnoreCase))
                pathValue = token.Substring("--ipset-exclude=".Length);

            if (pathValue == null) continue;

            // Strip surrounding quotes from the value only for filesystem checks
            string rawPath = pathValue.Trim('\"');
            if (string.IsNullOrWhiteSpace(rawPath)) continue;

            string fullPath;
            try
            {
                // Resolve to full path
                fullPath = Path.GetFullPath(rawPath);
            }
            catch (Exception ex)
            {
                return $"Некорректный путь в аргументах: {rawPath}. {ex.Message}";
            }

            // Security check: Must be inside zapret directory
            if (!fullPath.StartsWith(zapretDir, StringComparison.OrdinalIgnoreCase) && 
                !fullPath.Equals(AppPaths.ZapretDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return $"Путь к списку находится вне директории zapret: {fullPath}";
            }

            checkedFilesCount++;

            if (!File.Exists(fullPath))
            {
                // If it's a user list, create it
                if (fullPath.EndsWith("-user.txt", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string? parentDir = Path.GetDirectoryName(fullPath);
                        // Ensure parent directory exists if it's inside zapret/lists
                        if (parentDir != null && parentDir.StartsWith(listsDirectory, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!Directory.Exists(parentDir))
                            {
                                Directory.CreateDirectory(parentDir);
                            }
                        }

                        // Create empty file with UTF-8 without BOM
                        File.WriteAllText(fullPath, string.Empty, new UTF8Encoding(false));
                        createdFilesCount++;
                        AppLogger.Info($"Создан отсутствующий необязательный файл списка: {fullPath}");
                    }
                    catch (Exception ex)
                    {
                        return $"Не удалось создать необязательный файл списка {fullPath}: {ex.Message}";
                    }
                }
                else
                {
                    // Mandatory list missing
                    return $"Не найден обязательный файл списка zapret: {fullPath}";
                }
            }
        }

        AppLogger.Info($"Проверка файлов списков завершена. Проверено ссылок: {checkedFilesCount}, создано файлов: {createdFilesCount}");
        return null;
    }


    /// <summary>
    /// Splits command line arguments into tokens, respecting quotes.
    /// Double quotes are preserved in the output tokens.
    /// </summary>
    private static IReadOnlyList<string> SplitCommandLineArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            throw new ArgumentException("Arguments cannot be null or whitespace.", nameof(arguments));
        }

        var tokens = new List<string>();
        var currentToken = new StringBuilder();
        bool inQuotes = false;

        foreach (char c in arguments)
        {
            if (c == '\"')
            {
                inQuotes = !inQuotes;
                currentToken.Append(c);
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (currentToken.Length > 0)
                {
                    tokens.Add(currentToken.ToString());
                    currentToken.Clear();
                }
            }
            else
            {
                currentToken.Append(c);
            }
        }

        if (inQuotes)
        {
            throw new InvalidOperationException("Unbalanced quotes in arguments.");
        }

        if (currentToken.Length > 0)
        {
            tokens.Add(currentToken.ToString());
        }

        if (tokens.Count == 0)
        {
            throw new InvalidOperationException("No arguments found after splitting.");
        }

        return tokens;
    }


    /// <summary>
    /// Prepares a clean environment mimicking Flowseal's service.bat behavior.
    /// Kills winws.exe processes, removes orphaned WinDivert services, and enables TCP timestamps.
    /// </summary>
    public async Task PrepareFlowsealLikeEnvironmentAsync(string reason)
    {
        AppLogger.Info($"Подготовка окружения Flowseal ({reason})...");

        // 1. Kill all winws.exe processes
        try
        {
            var processes = Process.GetProcessesByName("winws");
            if (processes.Length > 0)
            {
                AppLogger.Info($"Обнаружено {processes.Length} процессов winws.exe. Завершение...");
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.Kill(true);
                        AppLogger.Info($"Процесс winws (PID: {proc.Id}) завершен.");
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning($"Не удалось завершить процесс winws (PID: {proc.Id}): {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Ошибка при поиске процессов winws: {ex.Message}");
        }

        // 2. Stop and delete WinDivert services (mimicking Flowseal cleanup)
        string[] divertServices = { "WinDivert", "WinDivert14" };
        foreach (var svc in divertServices)
        {
            try
            {
                // We use sc stop/delete. If service doesn't exist, sc returns error which we ignore.
                var stopRes = await RunScAsync("stop", svc);
                if (stopRes.Success) AppLogger.Info($"Служба {svc} остановлена.");
                
                var delRes = await RunScAsync("delete", svc);
                if (delRes.Success) AppLogger.Info($"Служба {svc} удалена из системы.");
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"Ошибка при очистке службы {svc}: {ex.Message}");
            }
        }

        // 3. Enable TCP Timestamps (required for some bypass techniques)
        try
        {
            AppLogger.Info("Настройка сетевого стека: включение TCP Timestamps...");
            var netshRes = await RunCommandAsync("netsh.exe", "interface", "tcp", "set", "global", "timestamps=enabled");
            if (netshRes.Success)
            {
                AppLogger.Info("TCP Timestamps успешно включены.");
            }
            else
            {
                AppLogger.Warning($"Предупреждение при настройке TCP Timestamps: {netshRes.Output}");
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Ошибка при вызове netsh: {ex.Message}");
        }

        AppLogger.Info("Подготовка окружения завершена.");
    }

    private async Task<ScResult> RunScAsync(params string[] args)
    {
        return await RunCommandAsync("sc.exe", args);
    }

    private async Task<ScResult> RunCommandAsync(string fileName, params string[] args)
    {
        string argumentsString = string.Join(" ", args);
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(psi);
            if (process == null) return new ScResult(false, $"Не удалось запустить {fileName}", fileName, argumentsString, -1);

            string output = await process.StandardOutput.ReadToEndAsync();
            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            int exitCode = process.ExitCode;
            bool success = exitCode == 0;
            string combinedOutput = (success ? output : (error + output)).Trim();
            
            return new ScResult(success, combinedOutput, fileName, argumentsString, exitCode, output, error);
        }
        catch (Exception ex)
        {
            return new ScResult(false, ex.Message, fileName, argumentsString, -1, "", ex.Message);
        }
    }

    private void LogDetailedCommandResult(ScResult res)
    {
        AppLogger.Info($"[Детали команды: {res.FileName} {res.Arguments}]");
        AppLogger.Info($"  Код завершения: {res.ExitCode}");
        if (!string.IsNullOrWhiteSpace(res.StdOut))
            AppLogger.Info($"  StdOut:\n{res.StdOut.TrimEnd()}");
        if (!string.IsNullOrWhiteSpace(res.StdErr))
            AppLogger.Info($"  StdErr:\n{res.StdErr.TrimEnd()}");
    }

    private async Task RunPostFailureDiagnosticsAsync()
    {
        AppLogger.Info("Выполнение пост-аварийной диагностики...");
        
        AppLogger.Info("sc queryex zapret:");
        var qex = await RunScAsync("queryex", ServiceName);
        LogDetailedCommandResult(qex);

        AppLogger.Info("sc qc zapret:");
        var qc = await RunScAsync("qc", ServiceName);
        LogDetailedCommandResult(qc);

        await LogScmEventsAsync();
    }

    private async Task LogScmEventsAsync()
    {
        AppLogger.Info("Сбор логов Windows Service Control Manager (последние 20 событий)...");
        var res = await RunCommandAsync("wevtutil.exe", "qe", "System", "/q:*[System[Provider[@Name='Service Control Manager']]]", "/c:20", "/rd:true", "/f:text");
        
        if (!res.Success)
        {
            AppLogger.Warning($"Не удалось получить логи SCM: {res.Output}");
            return;
        }

        string[] keywords = { "zapret", "winws", "WinDivert", "service did not respond", "service terminated", "error 1053", "error 1067" };
        
        // Split by "Event["
        var rawEvents = res.Output.Split(new[] { "Event[" }, StringSplitOptions.RemoveEmptyEntries);
        int matchedCount = 0;

        foreach (var rawEvt in rawEvents)
        {
            string evtText = "Event[" + rawEvt;
            bool match = false;
            foreach (var kw in keywords)
            {
                if (evtText.Contains(kw, StringComparison.OrdinalIgnoreCase))
                {
                    match = true;
                    break;
                }
            }

            if (match)
            {
                AppLogger.Info($"[SCM Event Match]\n{evtText.Trim()}");
                matchedCount++;
            }
        }

        if (matchedCount == 0)
        {
            AppLogger.Info("Релевантных событий SCM не найдено.");
        }
    }

    private record ScResult(
        bool Success, 
        string Output, 
        string FileName = "", 
        string Arguments = "", 
        int ExitCode = 0, 
        string StdOut = "", 
        string StdErr = ""
    );
}
