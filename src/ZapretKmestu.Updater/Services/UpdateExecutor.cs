using System.Diagnostics;
using ZapretKmestu.Updater.Models;

namespace ZapretKmestu.Updater.Services;

/// <summary>
/// Orchestrates the real update sequence for Zapret Kmestu.
///
/// Pipeline:
///   1. Wait for main process exit (with timeout)
///   2. Wait for file lock release
///   3. Create backup (.exe → .exe.backup)
///   4. Replace executable (copy new → current)
///   5. Verify replacement (file exists, size > 0)
///   6. Start updated application
///   7. Cleanup temporary files and old backup
///
/// In --test mode every step logs what it would do but performs no real I/O.
/// </summary>
public sealed class UpdateExecutor
{
    // -----------------------------------------------------------------------
    // Constants
    // -----------------------------------------------------------------------

    /// <summary>Maximum time to wait for the main process to exit.</summary>
    private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Maximum time to wait for the target file to become unlocked.</summary>
    private static readonly TimeSpan FileLockTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Polling interval when waiting for the file lock to release.</summary>
    private static readonly TimeSpan FileLockPollInterval = TimeSpan.FromMilliseconds(500);

    // -----------------------------------------------------------------------
    // Constructor
    // -----------------------------------------------------------------------

    private readonly UpdaterLogger _logger;

    public UpdateExecutor(UpdaterLogger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    // -----------------------------------------------------------------------
    // Public entry point
    // -----------------------------------------------------------------------

    /// <summary>
    /// Runs the complete update pipeline in order.
    /// Throws <see cref="UpdaterException"/> on any unrecoverable failure.
    /// </summary>
    public async Task RunAsync(UpdateArguments arguments)
    {
        bool testMode = arguments.IsTestMode;

        _logger.LogSection("Update pipeline started");
        _logger.Log(testMode
            ? "[UpdateExecutor] Running in TEST MODE – no real files will be modified."
            : "[UpdateExecutor] Running in LIVE MODE.");

        // Derive backup path if not specified.
        string backupPath = string.IsNullOrWhiteSpace(arguments.BackupPath)
            ? arguments.CurrentAppPath + ".backup"
            : arguments.BackupPath;

        _logger.Log($"[Config] CurrentAppPath : {arguments.CurrentAppPath}");
        _logger.Log($"[Config] NewAppPath     : {arguments.NewAppPath}");
        _logger.Log($"[Config] BackupPath     : {backupPath}");

        await WaitForMainProcessExitAsync(arguments.MainProcessId, testMode);
        await WaitForFileLockReleaseAsync(arguments.CurrentAppPath, testMode);
        await CreateBackupAsync(arguments.CurrentAppPath, backupPath, testMode);

        bool replaced = await ReplaceExecutableAsync(
            arguments.CurrentAppPath,
            arguments.NewAppPath,
            backupPath,
            testMode);

        if (!replaced)
        {
            // ReplaceExecutableAsync already attempted rollback and logged details.
            throw new UpdaterException("File replacement failed. Update aborted.");
        }

        await StartUpdatedApplicationAsync(arguments.CurrentAppPath, testMode);
        await CleanupAsync(arguments.NewAppPath, backupPath, testMode);

        _logger.LogSection("Update pipeline completed successfully");
    }

    // -----------------------------------------------------------------------
    // Step 1 – Wait for main process exit
    // -----------------------------------------------------------------------

    private async Task WaitForMainProcessExitAsync(int mainProcessId, bool testMode)
    {
        _logger.LogSection("Step 1: Wait for main process exit");
        _logger.Log($"[Step 1] Target PID: {mainProcessId}");

        if (testMode)
        {
            _logger.Log("[Step 1] TEST MODE – skipping real process wait.");
            return;
        }

        Process? process;
        try
        {
            process = Process.GetProcessById(mainProcessId);
        }
        catch (ArgumentException)
        {
            // Process has already exited – perfectly fine.
            _logger.Log("[Step 1] Process has already exited. Continuing.");
            return;
        }

        _logger.Log($"[Step 1] Found process '{process.ProcessName}'. Waiting up to {ProcessExitTimeout.TotalSeconds}s for it to exit…");

        using var cts = new CancellationTokenSource(ProcessExitTimeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
            _logger.Log("[Step 1] Main process exited.");
        }
        catch (OperationCanceledException)
        {
            throw new UpdaterException(
                $"[Step 1] Timeout: main process (PID {mainProcessId}) did not exit within {ProcessExitTimeout.TotalSeconds}s.");
        }
        finally
        {
            process.Dispose();
        }
    }

    // -----------------------------------------------------------------------
    // Step 2 – Wait for file lock release
    // -----------------------------------------------------------------------

    private async Task WaitForFileLockReleaseAsync(string filePath, bool testMode)
    {
        _logger.LogSection("Step 2: Wait for file lock release");
        _logger.Log($"[Step 2] Checking lock on: {filePath}");

        if (testMode)
        {
            _logger.Log("[Step 2] TEST MODE – skipping real lock check.");
            return;
        }

        var deadline = DateTime.UtcNow + FileLockTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsFileLocked(filePath))
            {
                _logger.Log("[Step 2] File is accessible (not locked).");
                return;
            }

            _logger.Log($"[Step 2] File is still locked. Retrying in {FileLockPollInterval.TotalMilliseconds}ms…");
            await Task.Delay(FileLockPollInterval);
        }

        throw new UpdaterException(
            $"[Step 2] Timeout: file '{filePath}' remained locked for {FileLockTimeout.TotalSeconds}s. Update aborted.");
    }

    /// <summary>Returns true if the file cannot be opened exclusively (i.e. is still locked).</summary>
    private static bool IsFileLocked(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    // -----------------------------------------------------------------------
    // Step 3 – Create backup
    // -----------------------------------------------------------------------

    private async Task CreateBackupAsync(string currentPath, string backupPath, bool testMode)
    {
        _logger.LogSection("Step 3: Create backup");
        _logger.Log($"[Step 3] Source : {currentPath}");
        _logger.Log($"[Step 3] Backup : {backupPath}");

        if (testMode)
        {
            _logger.Log("[Step 3] TEST MODE – backup creation simulated.");
            return;
        }

        try
        {
            // Delete stale backup from a previous failed run.
            if (File.Exists(backupPath))
            {
                _logger.Log("[Step 3] Removing stale backup file…");
                File.Delete(backupPath);
            }

            File.Copy(currentPath, backupPath, overwrite: false);

            long backupSize = new FileInfo(backupPath).Length;
            _logger.Log($"[Step 3] Backup created successfully. Size: {backupSize:N0} bytes.");
        }
        catch (Exception ex)
        {
            throw new UpdaterException($"[Step 3] Failed to create backup: {ex.Message}", ex);
        }

        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Step 4 – Replace executable  (+ Step 5 verify + rollback on failure)
    // -----------------------------------------------------------------------

    /// <summary>
    /// Copies the new executable over the current one.
    /// Verifies the result. On failure, attempts rollback from backup.
    /// Returns true if the replacement is verified; false if rollback was needed.
    /// </summary>
    private async Task<bool> ReplaceExecutableAsync(
        string currentPath,
        string newPath,
        string backupPath,
        bool testMode)
    {
        _logger.LogSection("Step 4: Replace executable");
        _logger.Log($"[Step 4] Source      : {newPath}");
        _logger.Log($"[Step 4] Destination : {currentPath}");

        if (testMode)
        {
            _logger.Log("[Step 4] TEST MODE – file replacement simulated.");
            _logger.Log("[Step 5] TEST MODE – verification simulated (pass).");
            return true;
        }

        try
        {
            // Use File.Replace when possible for atomic-ish swap on the same volume.
            // Fall back to Copy+Delete if Replace fails (e.g. cross-volume).
            bool sameVolume = string.Equals(
                Path.GetPathRoot(currentPath),
                Path.GetPathRoot(newPath),
                StringComparison.OrdinalIgnoreCase);

            if (sameVolume)
            {
                _logger.Log("[Step 4] Using File.Replace (atomic swap)…");
                // File.Replace(source, destination, destinationBackup)
                // We already have our own backup so pass null for the built-in backup.
                File.Replace(newPath, currentPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                _logger.Log("[Step 4] Cross-volume detected – using File.Copy + File.Delete…");
                File.Copy(newPath, currentPath, overwrite: true);
                // Leave newPath for cleanup step.
            }

            _logger.Log("[Step 4] Replacement operation completed.");
        }
        catch (Exception ex)
        {
            _logger.Log($"[Step 4] [ERROR] Replacement failed: {ex.Message}");
            await AttemptRollbackAsync(currentPath, backupPath, testMode);
            return false;
        }

        // ------------------------------------------------------------------
        // Step 5 – Verify result
        // ------------------------------------------------------------------
        _logger.LogSection("Step 5: Verify replacement");

        if (!File.Exists(currentPath))
        {
            _logger.Log("[Step 5] [ERROR] Destination file does not exist after copy.");
            await AttemptRollbackAsync(currentPath, backupPath, testMode);
            return false;
        }

        long finalSize = new FileInfo(currentPath).Length;
        if (finalSize == 0)
        {
            _logger.Log("[Step 5] [ERROR] Destination file size is zero.");
            await AttemptRollbackAsync(currentPath, backupPath, testMode);
            return false;
        }

        _logger.Log($"[Step 5] Verification passed. File size: {finalSize:N0} bytes.");
        return true;
    }

    // -----------------------------------------------------------------------
    // Rollback helper
    // -----------------------------------------------------------------------

    private async Task AttemptRollbackAsync(string currentPath, string backupPath, bool testMode)
    {
        _logger.LogSection("ROLLBACK: Restoring from backup");

        if (testMode)
        {
            _logger.Log("[ROLLBACK] TEST MODE – rollback simulated.");
            return;
        }

        if (!File.Exists(backupPath))
        {
            _logger.Log("[ROLLBACK] [ERROR] No backup file found. Cannot restore.");
            return;
        }

        try
        {
            File.Copy(backupPath, currentPath, overwrite: true);
            _logger.Log("[ROLLBACK] Restored from backup successfully.");
        }
        catch (Exception ex)
        {
            _logger.Log($"[ROLLBACK] [ERROR] Rollback also failed: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Step 6 – Start updated application
    // -----------------------------------------------------------------------

    private async Task StartUpdatedApplicationAsync(string currentPath, bool testMode)
    {
        _logger.LogSection("Step 6: Start updated application");
        _logger.Log($"[Step 6] Executable: {currentPath}");

        if (testMode)
        {
            _logger.Log("[Step 6] TEST MODE – application launch simulated.");
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = currentPath,
                UseShellExecute = true,   // Required to launch a GUI app from a background process.
                WorkingDirectory = Path.GetDirectoryName(currentPath) ?? string.Empty
            };

            Process.Start(startInfo);
            _logger.Log("[Step 6] Updated application launched successfully.");
        }
        catch (Exception ex)
        {
            // Non-fatal: the update itself succeeded; log and continue.
            _logger.Log($"[Step 6] [WARNING] Failed to start application: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    // -----------------------------------------------------------------------
    // Step 7 – Cleanup
    // -----------------------------------------------------------------------

    private async Task CleanupAsync(string newPath, string backupPath, bool testMode)
    {
        _logger.LogSection("Step 7: Cleanup");

        if (testMode)
        {
            _logger.Log("[Step 7] TEST MODE – cleanup simulated.");
            return;
        }

        // Remove the temporary new file (the source of the copy).
        TryDeleteFile(newPath, "temporary update file");

        // Remove the backup now that the update was successful.
        TryDeleteFile(backupPath, "backup file");

        await Task.CompletedTask;
    }

    /// <summary>Attempts to delete a file, logging errors without throwing.</summary>
    private void TryDeleteFile(string path, string description)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _logger.Log($"[Step 7] Skipping {description} (not found): {path}");
            return;
        }

        try
        {
            File.Delete(path);
            _logger.Log($"[Step 7] Deleted {description}: {path}");
        }
        catch (Exception ex)
        {
            // Cleanup failures are non-fatal.
            _logger.Log($"[Step 7] [WARNING] Could not delete {description} '{path}': {ex.Message}");
        }
    }
}
