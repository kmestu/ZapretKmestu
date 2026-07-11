namespace ZapretKmestu.Updater;

/// <summary>
/// Represents a known, recoverable failure in the update pipeline.
/// Thrown instead of generic exceptions so the entry point can distinguish
/// expected failures (return code 1) from unhandled bugs (return code 2).
/// </summary>
public sealed class UpdaterException : Exception
{
    public UpdaterException(string message) : base(message) { }

    public UpdaterException(string message, Exception innerException)
        : base(message, innerException) { }
}
