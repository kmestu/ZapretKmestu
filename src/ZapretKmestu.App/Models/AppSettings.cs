namespace ZapretKmestu.Models;

/// <summary>
/// Persisted application settings. All properties have safe defaults.
/// This model is serialized to/from %AppData%\Zapret Kmestu\settings.json.
/// </summary>
public class AppSettings
{
    public string Theme                  { get; set; } = "system";
    public string LastPage               { get; set; } = "Главная";
    public bool   AutoStartBypass        { get; set; } = false;
    public bool   AutoStartGui           { get; set; } = false;
    public bool   IsZapretInstalled      { get; set; } = false;
    public string InstalledZapretVersion { get; set; } = "";
    public string SelectedProfile        { get; set; } = "";
    public string LastRecommendedProfile { get; set; } = "";
    public string ZapretPath             { get; set; } = "";
    public bool   ShowVpnWarning            { get; set; } = true;
    public bool   AutoCheckUpdatesOnStartup { get; set; } = true;
    public bool   AutoCheckKmestuOnStartup  { get; set; } = true;
    public bool   AutoUpdateZapret          { get; set; } = true;
    public bool   OpenLastPageOnStartup     { get; set; } = false;
    public bool   MinimizeToTrayOnClose     { get; set; } = true;
    public bool   StopBypassOnAppExit       { get; set; } = false;
    public string ProfileCheckMode          { get; set; } = "Fast";
    public bool   ShowWorkModesSection      { get; set; } = false;
    public bool   UseDiagnostics            { get; set; } = false;
    public string SelectedWorkMode          { get; set; } = "Standard";
    public string SelectedGameFilter        { get; set; } = "UDP";
    public string SelectedGameScope         { get; set; } = "Только нужные адреса";
    public string AppliedWorkMode           { get; set; } = "Standard";
    public string AppliedGameFilter         { get; set; } = "UDP";
    public string AppliedGameScope          { get; set; } = "Только нужные адреса";
}
