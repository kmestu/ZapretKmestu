namespace ZapretKmestu.Updater.Models;

/// <summary>
/// Holds the validated arguments passed to the updater process.
///
/// Command-line format:
///   ZapretKmestu.Updater.exe --pid &lt;int&gt; --current &lt;path&gt; --new &lt;path&gt; [--backup &lt;path&gt;] [--test]
/// </summary>
public sealed class UpdateArguments
{
    // -----------------------------------------------------------------------
    // Properties
    // -----------------------------------------------------------------------

    /// <summary>OS process ID of the main Zapret Kmestu application that must exit before updating.</summary>
    public int MainProcessId { get; private set; }

    /// <summary>Full path to the currently installed application executable.</summary>
    public string CurrentAppPath { get; private set; } = string.Empty;

    /// <summary>Full path to the downloaded new application executable.</summary>
    public string NewAppPath { get; private set; } = string.Empty;

    /// <summary>
    /// Optional path where the backup file will be written.
    /// If null the updater derives a default: &lt;currentDir&gt;\ZapretKmestu.exe.backup
    /// </summary>
    public string? BackupPath { get; private set; }

    /// <summary>
    /// When true, the updater performs every validation and lifecycle log step but
    /// does NOT touch any real files or processes.
    /// Activated by the --test flag.
    /// </summary>
    public bool IsTestMode { get; private set; }

    // -----------------------------------------------------------------------
    // Parsing
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parses a flat array of command-line tokens into an <see cref="UpdateArguments"/> instance.
    /// Unknown flags are silently ignored to allow forward compatibility.
    /// </summary>
    public static UpdateArguments Parse(string[] args)
    {
        var result = new UpdateArguments();

        for (int i = 0; i < args.Length; i++)
        {
            string flag = args[i].ToLowerInvariant();

            switch (flag)
            {
                case "--pid":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int pid))
                        result.MainProcessId = pid;
                    i++;
                    break;

                case "--current":
                    if (i + 1 < args.Length)
                        result.CurrentAppPath = args[i + 1];
                    i++;
                    break;

                case "--new":
                    if (i + 1 < args.Length)
                        result.NewAppPath = args[i + 1];
                    i++;
                    break;

                case "--backup":
                    if (i + 1 < args.Length)
                        result.BackupPath = args[i + 1];
                    i++;
                    break;

                case "--test":
                    result.IsTestMode = true;
                    break;
            }
        }

        return result;
    }

    // -----------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns <c>true</c> when all required fields carry usable, safe values.
    /// In test mode, file-system checks are skipped.
    /// Sets <paramref name="error"/> to a human-readable description when invalid.
    /// </summary>
    public bool IsValid(out string error)
    {
        // --- Required field checks (always) ---------------------------------

        if (MainProcessId <= 0)
        {
            error = "--pid must be a positive integer.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(CurrentAppPath))
        {
            error = "--current must not be empty.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(NewAppPath))
        {
            error = "--new must not be empty.";
            return false;
        }

        // --- Filename safety checks (always) --------------------------------

        string currentName = Path.GetFileName(CurrentAppPath);
        if (!currentName.Equals("ZapretKmestu.exe", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Safety check failed: --current must point to ZapretKmestu.exe, got '{currentName}'.";
            return false;
        }

        string newName = Path.GetFileName(NewAppPath);
        if (!newName.Equals("ZapretKmestu.exe", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Safety check failed: --new must point to ZapretKmestu.exe, got '{newName}'.";
            return false;
        }

        // --- File-system existence checks (skip in test mode) ---------------

        if (!IsTestMode)
        {
            if (!File.Exists(CurrentAppPath))
            {
                error = $"Safety check failed: current executable not found at '{CurrentAppPath}'.";
                return false;
            }

            if (!File.Exists(NewAppPath))
            {
                error = $"Safety check failed: new executable not found at '{NewAppPath}'.";
                return false;
            }

            // Sanity: new file must not be the same physical path as current.
            if (string.Equals(
                    Path.GetFullPath(CurrentAppPath),
                    Path.GetFullPath(NewAppPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Safety check failed: --current and --new must not point to the same file.";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }
}
