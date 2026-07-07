namespace ZapretKmestu.Models;

public class ZapretProfileCommandInfo
{
    public bool Success { get; set; }
    public string ProfileFilePath { get; set; } = string.Empty;
    public string WinwsPath { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string FullCommandPreview { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
}
