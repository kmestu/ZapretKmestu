using System;

namespace ZapretKmestu.Models;

public enum ProfileCheckMode
{
    Fast,
    Accurate
}

public class ProfileCheckResult
{
    public string ProfileName { get; set; } = string.Empty;
    public bool YouTubeAvailable { get; set; }
    public bool DiscordAvailable { get; set; }
    public int YouTubeScore { get; set; }
    public int DiscordScore { get; set; }
    public int TotalScore => YouTubeScore + DiscordScore;
    public int SuccessCount { get; set; }
    public int TotalProbes { get; set; }
    public string StabilityText => TotalProbes > 0 ? $"{SuccessCount}/{TotalProbes}" : "—";
    public string Errors { get; set; } = string.Empty;
    public TimeSpan CheckDuration { get; set; }
    public bool IsPerfect => YouTubeAvailable && DiscordAvailable;
    public bool IsPartial => (YouTubeAvailable || DiscordAvailable) && !IsPerfect;

    // Helper properties for UI comparison
    public bool IsWinner { get; set; }
    public string CheckDurationText => CheckDuration.TotalSeconds < 1 ? "< 1с" : $"{(int)CheckDuration.TotalSeconds}с";
    public string DisplayStatus => (YouTubeAvailable && DiscordAvailable) ? "Works" : (YouTubeAvailable || DiscordAvailable) ? "Partial" : "Failed";

    public string DisplayStatusRu =>
        (YouTubeAvailable && DiscordAvailable) ? "Работает" :
        (YouTubeAvailable || DiscordAvailable) ? "Частично" :
        "Не работает";

    public string DisplayProfileName =>
        ProfileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
            ? ProfileName[..^4]
            : ProfileName;
}

