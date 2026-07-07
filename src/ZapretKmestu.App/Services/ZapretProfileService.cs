using System.Collections.Generic;
using System.IO;
using System.Linq;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Service for discovering available zapret profiles (batch files) on the local disk.
/// Does not execute any files.
/// </summary>
public class ZapretProfileService
{
    private readonly string _zapretDir;

    public ZapretProfileService(string zapretDir)
    {
        _zapretDir = zapretDir;
    }

    /// <summary>
    /// Returns a list of available profiles found in the zapret directory.
    /// Matches general*.bat files.
    /// </summary>
    public List<ZapretProfileInfo> GetAvailableProfiles()
    {
        if (!Directory.Exists(_zapretDir))
        {
            return new List<ZapretProfileInfo>();
        }

        try
        {
            var files = Directory.GetFiles(_zapretDir, "general*.bat", SearchOption.TopDirectoryOnly);
            
            return files.Select(path =>
            {
                var fileName = Path.GetFileName(path);
                var displayName = Path.GetFileNameWithoutExtension(path);
                
                return new ZapretProfileInfo
                {
                    FileName = fileName,
                    FullPath = path,
                    DisplayName = displayName,
                    Category = DetermineCategory(fileName)
                };
            })
            .OrderBy(p => GetNaturalSortKey(p.DisplayName))
            .ToList();
        }
        catch (IOException)
        {
            return new List<ZapretProfileInfo>();
        }
    }

    /// <summary>
    /// Generates a key for natural sorting by padding numbers with leading zeros.
    /// Example: "general (ALT2)" -> "general (ALT0000000002)"
    /// </summary>
    private static string GetNaturalSortKey(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        
        return System.Text.RegularExpressions.Regex.Replace(input, @"\d+", m => m.Value.PadLeft(10, '0'));
    }

    private string DetermineCategory(string fileName)
    {
        var normalized = fileName.ToUpperInvariant().Replace("-", " ").Replace("_", " ");
        
        // Priority 1: FAKE TLS AUTO
        if (normalized.Contains("FAKE TLS AUTO"))
            return "FAKE TLS AUTO";
            
        // Priority 2: SIMPLE FAKE
        if (normalized.Contains("SIMPLE FAKE"))
            return "SIMPLE FAKE";

        // Priority 3: ALT
        if (normalized.Contains(" ALT") || normalized.Contains("(ALT"))
            return "ALT";

        // Priority 4: Основной
        if (normalized.StartsWith("GENERAL"))
            return "Основной";
            
        return "Остальные";
    }
}
