using System;
using System.Collections.Generic;

namespace ZapretKmestu.Models;

/// <summary>
/// Container for persisting the results of the last successful auto-pick run.
/// </summary>
public class LastAutoPickData
{
    public List<ProfileCheckResult> Results { get; set; } = new();
    public DateTime CompletedAt { get; set; }
}
