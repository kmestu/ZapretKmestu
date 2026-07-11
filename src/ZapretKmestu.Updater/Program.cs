using ZapretKmestu.Updater.Models;
using ZapretKmestu.Updater.Services;

namespace ZapretKmestu.Updater;

/// <summary>
/// Entry point for the standalone updater process.
///
/// Invocation:
///   ZapretKmestu.Updater.exe --pid &lt;mainPid&gt; --current &lt;appPath&gt; --new &lt;newPath&gt; [--backup &lt;backupPath&gt;] [--test]
///
/// Exit codes:
///   0  – update completed successfully
///   1  – validation failed or known update error (details in log)
///   2  – unhandled exception (details in log)
/// </summary>
internal static class Program
{
    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        // Initialise logger first so all subsequent errors are captured.
        using var logger = new UpdaterLogger();

        logger.Log("=== Zapret Kmestu Updater started ===");
        logger.Log($"Version : {typeof(Program).Assembly.GetName().Version}");
        logger.Log($"Arguments: {string.Join(" ", args)}");

        try
        {
            var arguments = UpdateArguments.Parse(args);

            if (arguments.IsTestMode)
                logger.Log("[Main] --test flag detected. Running in test mode – no files will be modified.");

            logger.Log("Parsed arguments:");
            logger.Log($"  MainProcessId : {arguments.MainProcessId}");
            logger.Log($"  CurrentAppPath: {arguments.CurrentAppPath}");
            logger.Log($"  NewAppPath    : {arguments.NewAppPath}");
            logger.Log($"  BackupPath    : {arguments.BackupPath ?? "(auto)"}");
            logger.Log($"  IsTestMode    : {arguments.IsTestMode}");

            if (!arguments.IsValid(out string validationError))
            {
                logger.Log($"[ERROR] Argument validation failed: {validationError}");
                return 1;
            }

            logger.Log("Argument validation passed.");

            var executor = new UpdateExecutor(logger);
            await executor.RunAsync(arguments);

            logger.Log("=== Updater finished successfully ===");
            return 0;
        }
        catch (UpdaterException ex)
        {
            // Known, expected failure – exit code 1.
            logger.Log($"[ERROR] Update failed: {ex.Message}");
            logger.Log("=== Updater finished with error (exit code 1) ===");
            return 1;
        }
        catch (Exception ex)
        {
            // Unhandled bug – exit code 2.
            logger.Log($"[FATAL] Unhandled exception: {ex}");
            logger.Log("=== Updater crashed (exit code 2) ===");
            return 2;
        }
    }
}
