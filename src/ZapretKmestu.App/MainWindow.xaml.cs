using System;
using System.IO;
using System.Text.Json;
using System.Linq;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using System.Drawing;
using WinForms = System.Windows.Forms;
using ZapretKmestu.Models;
using ZapretKmestu.Services;

namespace ZapretKmestu;

public enum FooterMessageKind
{
    Info,
    Success,
    Warning,
    Error
}

public enum HeroIconKind
{
    Running,
    Stopped,
    Warning,
    NotInstalled,
    Installing,
    Wizard
}

/// <summary>
/// Code-behind for the main application window.
/// All button handlers are safe placeholders — no external processes are invoked.
/// Navigation between pages is handled here via panel visibility toggling.
/// Settings are loaded from AppSettings on init and saved on change.
/// </summary>
public partial class MainWindow : Window
{

    private AppSettings Settings => App.Settings;

    private readonly ZapretInstallerService _installer;
    private readonly ZapretExtractionService _extractionService;
    private const string BundledArchiveFileName = "zapret-flowseal-1.9.9d-bundled.zip";
    private const string BundledArchiveExpectedVersion = "1.9.9d";
    private readonly ZapretProfileService _profileService;
    private readonly AdminService _adminService;
    private readonly ZapretServiceStatusService _statusService;
    private readonly ZapretProfileCommandParser _commandParser;
    private readonly ZapretServiceManager _serviceManager;
    private readonly GitHubReleaseService _releaseService;
    private CancellationTokenSource? _installCts;
    private bool _isCheckingUpdates;
    private DateTime _lastManualUpdateCheckUtc = DateTime.MinValue;
    private GitHubReleaseInfo? _latestRelease;   // zapret (Flowseal)
    private string? _latestKmestuRelease;          // ZapretKmestu tag from GitHub
    private string? _latestKmestuZipUrl;           // direct download URL for ZapretKmestu ZIP asset
    private bool _isUpdateAvailable;               // zapret update available
    private bool _isKmestuUpdateAvailable;         // Zapret Kmestu update available
    private bool _isKmestuUpdating;                // true while downloading / preparing Kmestu update
    private readonly AppUpdateService _appUpdateService = new(AppUpdateService.CreateHttpClient());
#if DEBUG
    public enum UpdateCenterDebugState
    {
        None,
        OnlyKmestu,
        OnlyZapret,
        Both
    }
    private UpdateCenterDebugState _updateCenterDebugState = UpdateCenterDebugState.None;
#endif
    private bool _hasUpdateCheckError;
    private bool _hasInstallError;
    private bool _hasCheckedUpdates;
    private readonly DispatcherTimer _vpnRefreshTimer;
    private readonly DispatcherTimer _uptimeTimer;
    private const int AutoCheckIntervalSeconds = 15;
    private readonly DispatcherTimer _autoCheckTimer;
    private bool _isNetworkCheckRunning;
    private DateTimeOffset? _bypassStartedAt;
    private readonly AutostartService _autostartService;
    private HeroIconKind? _lastHeroIcon = null;
    private string _lastHelperText = "";
    private bool _isInitialized;
    private bool _isSyncingProfiles;
    private bool _isApplyingProfile;
    private string? _lastAppliedProfile;
    private bool _autoStartBypassAttempted;

    private bool _isWizardRunning = false;
    private bool _suppressWizardFooterNoise = false;
    private DateTime? _autopickStartTime = null;
    private CancellationTokenSource? _wizardCts;
    private TaskCompletionSource? _wizardCompletionTcs;
    private DateTime? _lastCheckTime;
    private string? _bestProfileCandidate;
    private int _bestProfileScore = -1;
    private string? _originalProfileBeforeWizard;
    private bool _wasRunningBeforeWizard;
    private ProfileCheckResult? _lastWizardResult;
    private List<ProfileCheckResult>? _lastWizardResults;
    private DateTime? _lastWizardCompletedAt;
    private ProfileCheckMode _currentWizardMode;

    private WinForms.NotifyIcon? _notifyIcon;
    private Window? _trayFlyout;
    private EventWaitHandle? _activationEvent;
    private Thread? _activationWatcher;
    private System.Drawing.Icon? _iconOn;
    private System.Drawing.Icon? _iconOff;
    private System.Drawing.Icon? _iconAutopick;
    private System.Drawing.Icon? _iconVpn;
    private bool _isReallyClosing;
    private bool _isTrayBypassToggleRunning;
    private bool _isTrayProfileApplyRunning;

    private Action? _overlayPrimaryAction;
    private Action? _overlaySecondaryAction;
    private Action? _overlayTertiaryAction;
    private bool _closeBehavesAsPrimary;

    private enum OverlayResult { Primary, Secondary, Tertiary }

    private enum DiagnosticProbeState
    {
        NotChecked,
        Checking,
        Available,
        Unavailable,
        Error
    }

    private DiagnosticProbeState _youtubeProbeState = DiagnosticProbeState.NotChecked;
    private DiagnosticProbeState _discordProbeState = DiagnosticProbeState.NotChecked;
    private int? _lastYouTubeDurationMs;
    private int? _lastDiscordDurationMs;
    private bool _lastVpnActive;
    private bool _initialNetworkDiagnosticQueued;

    private class DiagnosticSnapshot
    {
        public DateTime Timestamp { get; set; }
        public int SuccessfulEndpoints { get; set; }
        public int? YouTubeDurationMs { get; set; }
        public int? DiscordDurationMs { get; set; }
        public DiagnosticProbeState YouTubeState { get; set; }
        public DiagnosticProbeState DiscordState { get; set; }
    }
    private readonly List<DiagnosticSnapshot> _diagnosticHistory = new List<DiagnosticSnapshot>();
    private string? _pinnedServiceName;
    private DateTime? _pinnedTimestamp;

    // ─── UI State ─────────────────────────────────────────────────────────────
    private const string WorkModeStandardKey = "Standard";
    private const string WorkModeServicesKey = "Services";
    private const string WorkModeGameKey = "Game";

    private string _selectedWorkMode = WorkModeStandardKey;

    // Game settings sub-panel UI-only state (default selection values)
    private string _selectedGameFilter = "UDP";
    private string _selectedGameScope = "Только нужные адреса";
    private bool _hasPendingScenarioChanges = false;
    private bool _isGameFilterOperationRunning = false;
    private bool _suppressGameFilterToggleEvents = false;

    // ─── Startup ──────────────────────────────────────────────────────────────

    public MainWindow()
    {
        InitializeComponent();
        DiagAutoRefreshText.Text = $"Автоматическое обновление графиков — каждые {AutoCheckIntervalSeconds} секунд";

        // Initialize installer services
        var http = GitHubReleaseService.CreateHttpClient();
        _releaseService = new GitHubReleaseService(http);
        var downloadService = new ZapretDownloadService(http);
        _extractionService = new ZapretExtractionService();
        _installer = new ZapretInstallerService(_releaseService, downloadService, _extractionService, Settings);
        _profileService = new ZapretProfileService(AppPaths.ZapretDirectory);
        _adminService = new AdminService();
        _statusService = new ZapretServiceStatusService();
        _commandParser = new ZapretProfileCommandParser(AppPaths.ZapretDirectory);
        _serviceManager = new ZapretServiceManager(_adminService, _statusService, _commandParser, Settings);
        _autostartService = new AutostartService(Settings);

        // Bind settings to UI controls
        BindSettingsToUi();
        RestoreScenarioSelections();
        _hasPendingScenarioChanges = !IsSelectedScenarioSameAsApplied();
        UpdateWorkModeVisuals();
        UpdateGameSettingsVisuals();

        // Pre-warm Settings page visual templates so switches render correctly on first open.
        // Must be called after BindSettingsToUi() (values are set) and before RestoreLastPage().
        // _isInitialized is still false here, so no settings handlers fire.
        PreWarmSettingsPageVisuals();

        // Restore last page after binding to prevent layout flashes or premature page visibility before initialization
        RestoreLastPage();

        // Populate Journal with current log
        RefreshJournal();

        // Reflect expert page saved state
        RefreshExpertPage();

        // Update install card based on current status
        UpdateInstallCard();

        // Validate installation in background
        _ = ValidateInstallationOnStartup();

        // Check admin and service status in background
        _ = CheckStatusOnStartup();

        // Initialize VPN refresh timer
        _vpnRefreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _vpnRefreshTimer.Tick += VpnRefreshTimer_Tick;
        _vpnRefreshTimer.Start();

        // Initialize Uptime timer
        _uptimeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uptimeTimer.Tick += UptimeTimer_Tick;

        // Initialize AutoCheck timer
        _autoCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoCheckIntervalSeconds) };
        _autoCheckTimer.Tick += AutoCheckTimer_Tick;

        _isInitialized = true;
        UpdateSidebarThemeToggleUi();
        UpdateScenarioStatusUi();
        UpdateUpdateStatusUi();

        // Handle auto-update check on startup
        HandleStartupUpdates();

        // Apply native title bar theme
        UpdateWindowTitleBar();

        // Handle auto-start bypass
        _ = HandleAutoStartBypassAsync();

        // Load persisted last auto-pick results
        LoadLastAutoPickResults();

        // Initialize last wizard results button state
        UpdateLastWizardResultsButtonState();

        // Initialize system tray icon
        InitializeTrayIcon();

        // Initialize single instance activation listener
        InitializeActivationListener();

        // Close update center popup on window movement / deactivation / closing
        LocationChanged += (s, e) => CloseUpdateCenterPopup();
        Deactivated += (s, e) => CloseUpdateCenterPopup();
        Closing += (s, e) => CloseUpdateCenterPopup();
    }

    private async void HandleStartupUpdates()
    {
        try
        {
            bool checkZapret  = Settings.AutoCheckUpdatesOnStartup;
            bool checkKmestu  = Settings.AutoCheckKmestuOnStartup;

            if (checkZapret || checkKmestu)
            {
                // Delay to let UI initialize and show initial status
                await Task.Delay(2000);
                await CheckZapretUpdatesAsync(isAutomatic: true, checkZapret: checkZapret, checkKmestu: checkKmestu);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при проверке обновлений при запуске: {ex.Message}");
        }
    }

    private void VpnRefreshTimer_Tick(object? sender, EventArgs e)
    {
        // Only refresh VPN status to be lightweight
        _ = RefreshVpnStatusAsync();
    }

    private async Task RefreshVpnStatusAsync(bool animate = true)
    {
        bool isVpn = await Task.Run(() => IsPossibleVpnActive());
        var status = await Task.Run(() => _statusService.GetStatus());

        ApplyStatusToUi(status, isVpn, animate);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_isReallyClosing)
        {
            base.OnClosing(e);
            return;
        }

        if (Settings.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            this.Hide();
            return;
        }

        e.Cancel = true;
        _ = ExitApplicationAsync();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vpnRefreshTimer?.Stop();
        _uptimeTimer?.Stop();
        _autoCheckTimer?.Stop();

        try
        {
            var evt = _activationEvent;
            _activationEvent = null;
            evt?.Set(); // Wake up thread so it can exit safely
            evt?.Dispose();
        }
        catch { }

        CleanupTrayIcon();

        base.OnClosed(e);
    }

    private void InitializeActivationListener()
    {
        try
        {
            _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ZapretKmestu.ActivationEvent");
            _activationWatcher = new Thread(() =>
            {
                while (_activationEvent != null)
                {
                    try
                    {
                        if (_activationEvent.WaitOne())
                        {
                            Dispatcher.BeginInvoke(() => RestoreWindow());
                        }
                    }
                    catch { break; }
                }
            })
            {
                IsBackground = true,
                Name = "ActivationWatcherThread"
            };
            _activationWatcher.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка инициализации листенера активации: {ex.Message}");
        }
    }

    internal void CleanupTrayIcon()
    {
        if (_notifyIcon != null)
        {
            try
            {
                _notifyIcon.Visible = false;

                if (_trayFlyout != null)
                {
                    var flyout = _trayFlyout;
                    _trayFlyout = null;
                    flyout.Close();
                }

                _notifyIcon.Dispose();
            }
            catch (Exception ex)
            {
                // Use Debug or silent catch for cleanup to avoid noise on exit
                System.Diagnostics.Debug.WriteLine($"Tray cleanup error: {ex.Message}");
            }
            finally
            {
                _notifyIcon = null;

                // Dispose cached icons
                _iconOn?.Dispose();
                _iconOff?.Dispose();
                _iconAutopick?.Dispose();
                _iconVpn?.Dispose();

                _iconOn = null;
                _iconOff = null;
                _iconAutopick = null;
                _iconVpn = null;
            }
        }
    }

    private async Task ExitApplicationAsync()
    {
        if (_isReallyClosing) return;
        _isReallyClosing = true;

        if (_isWizardRunning)
        {
            AppLogger.Info("Запрошен выход во время автоподбора. Ожидание отмены и восстановления...");
            SetFooterMessage("Завершение работы...", FooterMessageKind.Info, highlight: true);
        }

        _wizardCts?.Cancel();
        _installCts?.Cancel();

        var tcs = _wizardCompletionTcs;
        if (tcs != null)
        {
            try { await tcs.Task; }
            catch (Exception ex)
            {
                AppLogger.Error($"Ошибка ожидания завершения автоподбора при выходе: {ex.Message}");
            }
        }

        // Optional bypass stop on exit
        if (Settings.StopBypassOnAppExit && Settings.IsZapretInstalled)
        {
            var status = _statusService.GetStatus();
            if (status.IsRunning)
            {
                AppLogger.Info("Запрошена остановка обхода перед выходом из приложения...");

                // We don't block the UI here, but we await the stop.
                // If it fails, we still allow exit to ensure app doesn't hang.
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    await _serviceManager.StopAsync().WaitAsync(cts.Token);
                    AppLogger.Info("Обход остановлен. Завершение работы...");
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Ошибка при остановке обхода перед выходом: {ex.Message}");
                }
            }
        }

        // Ensure tray cleanup before final shutdown
        CleanupTrayIcon();

        System.Windows.Application.Current.Shutdown();
    }

    private bool SafeSaveSettings()
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(AppPaths.SettingsFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            var json = System.Text.Json.JsonSerializer.Serialize(Settings, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNameCaseInsensitive = true });
            System.IO.File.WriteAllText(AppPaths.SettingsFilePath, json);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Не удалось сохранить настройки: {ex.Message}");
            SetFooterMessage("Ошибка: не удалось сохранить настройки", FooterMessageKind.Error, highlight: true);
            return false;
        }
    }

    private async Task CheckStatusOnStartup()
    {
        ReconcileZapretState();
        // 1. Check admin status
        bool isAdmin = _adminService.IsRunningAsAdministrator();
        if (isAdmin)
            AppLogger.Info("Приложение запущено с правами администратора.");
        else
            AppLogger.Info("Запущено без прав администратора.");

        // 2. Check service status
        var status = await Task.Run(() => _statusService.GetStatus());

        if (status.Exists)
            AppLogger.Info($"Статус службы zapret: {(status.IsRunning ? "запущена" : "остановлена")}.");
        else if (status.ErrorMessage != null)
            AppLogger.Warning($"Ошибка при проверке службы zapret: {status.ErrorMessage}");
        else
            AppLogger.Info("Служба zapret не установлена в системе.");

        // 3. Update UI
        bool isVpn = IsPossibleVpnActive();
        Dispatcher.Invoke(() => ApplyStatusToUi(status, isVpn, animate: false));

        // Schedule delayed startup diagnostic if needed
        _ = QueueStartupNetworkDiagnosticIfNeededAsync(status);
    }

    private async Task QueueStartupNetworkDiagnosticIfNeededAsync(ZapretServiceStatusInfo status)
    {
        if (_initialNetworkDiagnosticQueued) return;
        if (!Settings.IsZapretInstalled) return;
        if (!status.IsRunning) return;
        if (_isWizardRunning || _installCts != null || _isNetworkCheckRunning) return;

        _initialNetworkDiagnosticQueued = true;

        // Perform delay asynchronously
        await Task.Delay(2000);

        // Verify conditions again on the UI thread
        if (!Settings.IsZapretInstalled) return;
        if (_isWizardRunning || _installCts != null || _isNetworkCheckRunning) return;

        // Re-check current service status in background
        var currentStatus = await Task.Run(() => _statusService.GetStatus());
        if (!currentStatus.IsRunning) return;

        // Call the network diagnostics quietly on the UI thread
        Dispatcher.Invoke(() => {
            _ = ExecuteNetworkDiagnosticAsync(isManualCheck: false);
        });
    }

    private void ApplyStatusToUi(ZapretServiceStatusInfo status, bool isVpn, bool animate)
    {
#if DEBUG
        if (_isDebugPreviewMode) return;
#endif
        _lastVpnActive = isVpn;
        bool isAdmin = _adminService.IsRunningAsAdministrator();

        if (isAdmin)
        {
            AdminStatusText.Text = "Администратор";
            AdminStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
            AdminIcon.Visibility = Visibility.Visible;
        }
        else
        {
            AdminStatusText.Text = "Нет прав администратора";
            AdminStatusText.Foreground = (System.Windows.Media.Brush)FindResource("WarningBrush");
            AdminIcon.Visibility = Visibility.Collapsed;
        }

        // --- Hardening: Update sensitive button availability ---
        bool isBusy = _isWizardRunning || _installCts != null || _isTrayBypassToggleRunning || _isTrayProfileApplyRunning || OperationProgressCard.Visibility == Visibility.Visible;
        string? adminTooltip = isAdmin ? null : "Требуются права администратора";
        string? busyTooltip = isBusy ? "Выполняется другая операция" : adminTooltip;

        // Hero button
        bool toggleEnabled = isAdmin || !Settings.IsZapretInstalled || isBusy;
        if (_isWizardRunning) toggleEnabled = _wizardCts == null || !_wizardCts.IsCancellationRequested;
        ToggleButton.IsEnabled = toggleEnabled;
        ToggleButton.ToolTip = isBusy && !_isWizardRunning ? "Выполняется другая операция" : adminTooltip;

        // Action buttons
        BestProfileButton.IsEnabled = isAdmin && !isBusy;
        BestProfileButton.ToolTip = busyTooltip;
        FixAllButton.IsEnabled = isAdmin && !isBusy;
        FixAllButton.ToolTip = busyTooltip;
        CheckConnectionButton.IsEnabled = !isBusy;

        // Profile combo boxes
        if (ProfileComboBox != null) ProfileComboBox.IsEnabled = Settings.IsZapretInstalled && !isBusy;
        if (HomeProfileComboBox != null) HomeProfileComboBox.IsEnabled = Settings.IsZapretInstalled && !isBusy;

        // Expert page buttons
        ReinstallServiceButton.IsEnabled = isAdmin && !isBusy;
        ReinstallServiceButton.ToolTip = busyTooltip;
        UninstallAppButton.IsEnabled = isAdmin && !isBusy;
        UninstallAppButton.ToolTip = busyTooltip;

        if (_isWizardRunning)
        {
            ToggleButtonText.Text = (_wizardCts != null && _wizardCts.IsCancellationRequested) ? "ОТМЕНА..." : "ОТМЕНИТЬ";
        }
        else if (_installCts != null)
        {
            ToggleButtonText.Text = "ОТМЕНИТЬ";
        }
        else if (!Settings.IsZapretInstalled)
        {
            ToggleButtonText.Text = "УСТАНОВИТЬ ZAPRET";
        }
        else
        {
            ToggleButtonText.Text = status.IsRunning ? "ВЫКЛЮЧИТЬ" : "ВКЛЮЧИТЬ";
        }

        // Update Hero controls
        string newTitle;
        string newHelperText;
        HeroIconKind newIconKind;
        string circleBrushKey;
        string glowColorKey;
        string titleBrushKey;
        string badgeBgKey;
        string badgeBorderKey;
        string badgeTextKey;

        if (_isWizardRunning || _installCts != null || OperationProgressCard.Visibility == Visibility.Visible)
        {
            newTitle = _isWizardRunning ? "Подбор профиля" : (_installCts != null ? "Установка" : "Операция...");
            titleBrushKey = "PrimaryBrush";
            if (_isWizardRunning)
            {
                newHelperText = "Ищем лучший профиль";
            }
            else
            {
                newHelperText = _installCts != null ? "Скачиваем и настраиваем zapret" : "Выполняем действия...";
            }
            newIconKind = _isWizardRunning ? HeroIconKind.Wizard : HeroIconKind.Installing;
            circleBrushKey = "PrimaryBrush";
            glowColorKey = "PrimaryColor";
            badgeBgKey = "HeroVersionBadgeActiveBackgroundBrush";
            badgeBorderKey = "HeroVersionBadgeActiveBorderBrush";
            badgeTextKey = "HeroVersionBadgeActiveTextBrush";
        }
        else if (!Settings.IsZapretInstalled)
        {
            bool localValid = false;
            try { localValid = _installer.ValidateLocalInstall(); } catch { }

            if (status.Exists && !localValid)
            {
                newTitle = "Ошибка установки";
                titleBrushKey = "DangerBrush";
                newHelperText = "Требуется восстановление файлов";
                newIconKind = HeroIconKind.Warning;
                circleBrushKey = "DangerBrush";
                glowColorKey = "DangerColor";
            }
            else if (!status.Exists && localValid)
            {
                newTitle = "Не установлен";
                titleBrushKey = "IndigoBrush";
                newHelperText = "Локальные файлы найдены · требуется установка";
                newIconKind = HeroIconKind.NotInstalled;
                circleBrushKey = "IndigoBrush";
                glowColorKey = "IndigoColor";
            }
            else
            {
                newTitle = "Не установлен";
                titleBrushKey = "IndigoBrush";
                newHelperText = "Сначала установите zapret";
                newIconKind = HeroIconKind.NotInstalled;
                circleBrushKey = "IndigoBrush";
                glowColorKey = "IndigoColor";
            }

            badgeBgKey = "HeroVersionBadgeActiveBackgroundBrush";
            badgeBorderKey = "HeroVersionBadgeActiveBorderBrush";
            badgeTextKey = "HeroVersionBadgeActiveTextBrush";
        }
        else if (Settings.ShowVpnWarning && isVpn && status.IsRunning)
        {
            if (_selectedWorkMode == WorkModeServicesKey)
            {
                newTitle = "VPN активен";
                titleBrushKey = "WarningBrush";
                newHelperText = "Маршрут неизвестен — проверьте YouTube и Discord";
                newIconKind = HeroIconKind.Warning;
                circleBrushKey = "WarningBrush";
                glowColorKey = "WarningColor";
            }
            else
            {
                newTitle = "Обход включён";
                titleBrushKey = "WarningBrush";
                newHelperText = "VPN может менять маршрут";
                newIconKind = HeroIconKind.Warning;
                circleBrushKey = "WarningBrush";
                glowColorKey = "WarningColor";
            }
            badgeBgKey = "HeroVersionBadgeWarningBackgroundBrush";
            badgeBorderKey = "HeroVersionBadgeWarningBorderBrush";
            badgeTextKey = "HeroVersionBadgeWarningTextBrush";
        }
        else if (Settings.ShowVpnWarning && isVpn && !status.IsRunning)
        {
            newTitle = "Обход выключен";
            titleBrushKey = "StatusOffBrush";
            if (_selectedWorkMode == WorkModeServicesKey)
            {
                newHelperText = "VPN активен, проверьте маршрут после запуска";
            }
            else
            {
                newHelperText = "VPN включён";
            }
            newIconKind = HeroIconKind.Stopped;
            circleBrushKey = "StatusOffBrush";
            glowColorKey = "StatusOffColor";
            badgeBgKey = "HeroVersionBadgeWarningBackgroundBrush";
            badgeBorderKey = "HeroVersionBadgeWarningBorderBrush";
            badgeTextKey = "HeroVersionBadgeWarningTextBrush";
        }
        else if (status.IsRunning)
        {
            newTitle = "Обход включён";
            titleBrushKey = "StatusOnBrush";
            newHelperText = "Профиль применён";
            newIconKind = HeroIconKind.Running;
            circleBrushKey = "StatusOnBrush";
            glowColorKey = "StatusOnGlowColor";
            badgeBgKey = "HeroVersionBadgeOnBackgroundBrush";
            badgeBorderKey = "HeroVersionBadgeOnBorderBrush";
            badgeTextKey = "HeroVersionBadgeOnTextBrush";
        }
        else
        {
            newTitle = "Обход выключен";
            titleBrushKey = "StatusOffBrush";
            newHelperText = "Готов к запуску";
            newIconKind = HeroIconKind.Stopped;
            circleBrushKey = "StatusOffBrush";
            glowColorKey = "StatusOffColor";
            badgeBgKey = "HeroVersionBadgeOffBackgroundBrush";
            badgeBorderKey = "HeroVersionBadgeOffBorderBrush";
            badgeTextKey = "HeroVersionBadgeOffTextBrush";
        }

        // Trigger animation if content changed
        if (animate && (newIconKind != _lastHeroIcon || newHelperText != _lastHelperText || MainStatusText.Text != newTitle))
        {
            var sb = (Storyboard)HeroIconCircle.FindResource("StatusChangeAnimation");
            sb?.Begin();
        }

        MainStatusText.Text = newTitle;
        MainStatusText.SetResourceReference(TextBlock.ForegroundProperty, titleBrushKey);
        HeroHelperText.Text = newHelperText;

        UpdateHeroIcon(newIconKind, circleBrushKey, glowColorKey);

        // Override version badge color to warning/yellow if a newer update is known and zapret is installed
        if (_isUpdateAvailable && _latestRelease != null && Settings.IsZapretInstalled)
        {
            badgeBgKey = "HeroVersionBadgeWarningBackgroundBrush";
            badgeBorderKey = "HeroVersionBadgeWarningBorderBrush";
            badgeTextKey = "HeroVersionBadgeWarningTextBrush";
        }

        SetHeroVersionBadgeStyle(badgeBgKey, badgeBorderKey, badgeTextKey);
        UpdateTrayIcon(newIconKind);

        _lastHeroIcon = newIconKind;
        _lastHelperText = newHelperText;

        if (!Settings.IsZapretInstalled)
        {
            ProfileVersionBadge.Visibility = Visibility.Collapsed;
            ProfileText.Visibility = Visibility.Visible;
            ProfileVersionBadge.ToolTip = "zapret не установлен";
        }
        else
        {
            string version = Settings.InstalledZapretVersion ?? "???";
            ProfileText.Text = $"zapret {version}";
            ProfileVersionBadge.Visibility = Visibility.Visible;

            // Set specific tooltip message depending on update availability / error states
            if (_isUpdateAvailable && _latestRelease != null)
            {
                ProfileVersionBadge.ToolTip = $"Доступна новая версия zapret: {_latestRelease.TagName}";
            }
            else if (_hasUpdateCheckError)
            {
                ProfileVersionBadge.ToolTip = "Версия zapret установлена. Проверка обновлений не выполнена.";
            }
            else
            {
                ProfileVersionBadge.ToolTip = "Установленная версия zapret";
            }
        }

        // Update footer text dynamically
        if (!_suppressWizardFooterNoise)
        {
            if (!isAdmin)
            {
                SetFooterMessage("Нужны права администратора", FooterMessageKind.Warning, suppressPulse: !animate);
                ShowAdminAlert();
            }
        }

        // --- HARD RULE: Sync Game Filter UI with actual service state ---
        if (!_isGameFilterOperationRunning)
        {
            SyncGameFilterUiFromActualState(status: status);
        }

        if (_isGameFilterOperationRunning)
        {
            SetFooterMessage("Настраиваем Game Filter...", FooterMessageKind.Info, suppressPulse: !animate);
        }
        else if (!Settings.IsZapretInstalled)
        {
            string footerText = (_latestRelease != null) 
                ? $"zapret не установлен • Доступна {_latestRelease.TagName}" 
                : "zapret не установлен";
            SetFooterMessage(footerText, FooterMessageKind.Info, suppressPulse: !animate);
        }
        else if (status.IsRunning)
        {
            SetFooterMessage(GetBypassStatusMessage(true), FooterMessageKind.Success, suppressPulse: !animate);
        }
        else
        {
            SetFooterMessage(GetBypassStatusMessage(false), FooterMessageKind.Info, suppressPulse: !animate);
        }

        // --- Uptime Timer Management ---
        if (status.IsRunning && !_isWizardRunning && _installCts == null)
        {
            if (_bypassStartedAt == null)
            {
                _bypassStartedAt = DateTimeOffset.Now;
                UptimeText.Text = "работает сейчас";
                _uptimeTimer.Start();
            }
            UptimeText.Visibility = Visibility.Visible;
            UptimeSeparator.Visibility = Visibility.Visible;
        }
        else
        {
            _bypassStartedAt = null;
            _uptimeTimer.Stop();
            UptimeText.Visibility = Visibility.Collapsed;
            UptimeSeparator.Visibility = Visibility.Collapsed;
        }

        // --- AutoCheck Timer Management ---
        bool shouldAutoCheck = Settings.IsZapretInstalled && !_isWizardRunning && _installCts == null
                               && (status.IsRunning || (Settings.ShowVpnWarning && isVpn));

        if (shouldAutoCheck)
        {
            if (!_autoCheckTimer.IsEnabled) _autoCheckTimer.Start();
        }
        else
        {
            if (_autoCheckTimer.IsEnabled) _autoCheckTimer.Stop();
        }

        UpdateConnectionDiagnosticSummary();
    }

    private string GetBypassStatusMessage(bool isRunning)
    {
        if (isRunning)
        {
            if (Settings.ShowWorkModesSection)
            {
                if (Settings.AppliedWorkMode == WorkModeGameKey)
                {
                    string traffic = Settings.AppliedGameFilter switch
                    {
                        "UDP" => "UDP",
                        "TCP" => "TCP",
                        "TCP + UDP" => "TCP + UDP",
                        _ => "UDP"
                    };

                    string scope = Settings.AppliedGameScope switch
                    {
                        "Только нужные адреса" => "По спискам",
                        "Больше адресов" => "Расширенный",
                        "Максимальный охват" => "Весь трафик",
                        _ => "По спискам"
                    };

                    return $"Обход включён · Game Filter: {traffic} · {scope}";
                }
                else
                {
                    return "Обход включён · Game Filter выключен";
                }
            }
            return "Обход включён";
        }
        else
        {
            return "zapret готов к работе";
        }
    }

    private void SetHeroVersionBadgeStyle(string backgroundKey, string borderKey, string textKey)
    {
        ProfileVersionBadge.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        ProfileVersionBadge.SetResourceReference(Border.BorderBrushProperty, borderKey);
        ProfileText.SetResourceReference(TextBlock.ForegroundProperty, textKey);
    }

    private void UpdateHeroIcon(HeroIconKind kind, string backgroundResourceKey, string glowColorResourceKey)
    {
        string resourceKey = kind switch
        {
            HeroIconKind.Running => "IconLightning",
            HeroIconKind.Stopped => "IconShield",
            HeroIconKind.Warning => "IconGlobe",
            HeroIconKind.NotInstalled => "IconBoxPlus",
            HeroIconKind.Installing => "IconBoxDownload",
            HeroIconKind.Wizard => "IconProfileSearchHero",
            _ => "IconWarning"
        };

        HeroIconPath.Data = (System.Windows.Media.Geometry)FindResource(resourceKey);
        HeroIconCircle.SetResourceReference(Border.BackgroundProperty, backgroundResourceKey);

        string theme = App.GetCurrentResolvedThemeName();
        bool isDark = theme == "dark";
        var glowColor = (System.Windows.Media.Color)FindResource(glowColorResourceKey);

        HeroIconGlow.Color = glowColor;

        if (kind == HeroIconKind.Running)
        {
            HeroIconGlow.BlurRadius = 20;
            HeroIconGlow.Opacity = 0.45;
        }
        else
        {
            HeroIconGlow.BlurRadius = isDark ? 32 : 24;
            HeroIconGlow.Opacity = isDark ? 0.42 : 0.28;
        }

        // Reset any potential transition states
        HeroIconViewbox.BeginAnimation(UIElement.OpacityProperty, null);
        HeroIconViewbox.Opacity = 1.0;

        UpdateHeroMotionForCurrentState(kind);
    }

    private bool _isHeroRunningGlowActive = false;

    private void StartHeroRunningGlow()
    {
        if (_isHeroRunningGlowActive) return;
        _isHeroRunningGlowActive = true;

        // Premium breathing glow values: visible but tasteful
        double baseOpacity = 0.45;
        double targetOpacity = 0.85;
        double baseBlur = 20;
        double targetBlur = 34;

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseInOut };

        var opacityAnim = new DoubleAnimation
        {
            From = baseOpacity,
            To = targetOpacity,
            Duration = TimeSpan.FromSeconds(1.5), // Total cycle ~3s
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = easing
        };

        var blurAnim = new DoubleAnimation
        {
            From = baseBlur,
            To = targetBlur,
            Duration = TimeSpan.FromSeconds(1.5),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = easing
        };

        HeroIconGlow.BeginAnimation(DropShadowEffect.OpacityProperty, opacityAnim);
        HeroIconGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, blurAnim);
    }

    private void StopHeroRunningGlow()
    {
        if (!_isHeroRunningGlowActive) return;
        _isHeroRunningGlowActive = false;

        HeroIconGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
        HeroIconGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
    }

    private bool _isHeroPulseRunning = false;

    private void StartHeroProfilePulse()
    {
        if (_isHeroPulseRunning) return;
        _isHeroPulseRunning = true;

        var scaleAnimation = new DoubleAnimation
        {
            From = 1.0,
            To = 1.06,
            Duration = TimeSpan.FromSeconds(0.85),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };

        HeroIconCircleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleAnimation);
        HeroIconCircleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleAnimation);
    }

    private void StopHeroProfilePulse()
    {
        if (!_isHeroPulseRunning) return;
        _isHeroPulseRunning = false;

        HeroIconCircleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        HeroIconCircleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        HeroIconCircleScale.ScaleX = 1.0;
        HeroIconCircleScale.ScaleY = 1.0;
    }

    private const double GlintDurationSeconds = 3.0;
    private const double GlintRedPeakOpacity = 0.85;
    private const double GlintYellowPeakOpacity = 1.00;
    private const double GlintStartX = -0.85;
    private const double GlintEndX = 0.85;
    private const double GlintStartY = 0.85;
    private const double GlintEndY = -0.85;

    private bool _isHeroGlintActive = false;

    private void StartHeroGlint(bool isWarning)
    {
        if (_isHeroGlintActive) return;
        _isHeroGlintActive = true;

        var opacityAnim = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever,
            Duration = TimeSpan.FromSeconds(GlintDurationSeconds)
        };
        double peakOpacity = isWarning ? GlintYellowPeakOpacity : GlintRedPeakOpacity;
        opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(0.0)));
        opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(peakOpacity, KeyTime.FromPercent(0.15)));
        opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(peakOpacity, KeyTime.FromPercent(0.85)));
        opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.0, KeyTime.FromPercent(1.0)));

        var easing = new SineEase { EasingMode = EasingMode.EaseInOut };

        var transXAnim = new DoubleAnimation
        {
            From = GlintStartX,
            To = GlintEndX,
            Duration = TimeSpan.FromSeconds(GlintDurationSeconds),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = easing
        };

        var transYAnim = new DoubleAnimation
        {
            From = GlintStartY,
            To = GlintEndY,
            Duration = TimeSpan.FromSeconds(GlintDurationSeconds),
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = easing
        };

        HeroGlintHost.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        HeroGlintSoftBrushTransform.BeginAnimation(TranslateTransform.XProperty, transXAnim);
        HeroGlintSoftBrushTransform.BeginAnimation(TranslateTransform.YProperty, transYAnim);
        HeroGlintCoreBrushTransform.BeginAnimation(TranslateTransform.XProperty, transXAnim);
        HeroGlintCoreBrushTransform.BeginAnimation(TranslateTransform.YProperty, transYAnim);
    }

    private void StopHeroGlint()
    {
        if (!_isHeroGlintActive) return;
        _isHeroGlintActive = false;

        HeroGlintHost.BeginAnimation(UIElement.OpacityProperty, null);
        HeroGlintSoftBrushTransform.BeginAnimation(TranslateTransform.XProperty, null);
        HeroGlintSoftBrushTransform.BeginAnimation(TranslateTransform.YProperty, null);
        HeroGlintCoreBrushTransform.BeginAnimation(TranslateTransform.XProperty, null);
        HeroGlintCoreBrushTransform.BeginAnimation(TranslateTransform.YProperty, null);

        HeroGlintHost.Opacity = 0;
        HeroGlintSoftBrushTransform.X = GlintStartX;
        HeroGlintSoftBrushTransform.Y = GlintStartY;
        HeroGlintCoreBrushTransform.X = GlintStartX;
        HeroGlintCoreBrushTransform.Y = GlintStartY;
    }

    private void UptimeTimer_Tick(object? sender, EventArgs e)
    {
        if (_bypassStartedAt == null) return;
        var uptime = DateTimeOffset.Now - _bypassStartedAt.Value;
        UptimeText.Text = $"работает {FormatUptime(uptime)}";
    }

    private string FormatUptime(TimeSpan uptime)
    {
        int h = (int)uptime.TotalHours;
        int m = uptime.Minutes;
        int s = uptime.Seconds;

        if (h >= 1)
            return $"{h} ч {m:D2} мин";
        if (m >= 1)
            return $"{m} мин {s:D2} сек";
        return $"{s} сек";
    }



    private void UpdateHeroMotionForCurrentState(HeroIconKind kind)
    {
        if (kind == HeroIconKind.Wizard || kind == HeroIconKind.Installing)
        {
            StopHeroGlint();
            StopHeroRunningGlow();
            StartHeroProfilePulse();
        }
        else if (kind == HeroIconKind.Running)
        {
            StopHeroGlint();
            StopHeroProfilePulse();
            StartHeroRunningGlow();
        }
        else if (kind == HeroIconKind.Stopped)
        {
            StopHeroProfilePulse();
            StopHeroRunningGlow();
            StartHeroGlint(isWarning: false);
        }
        else if (kind == HeroIconKind.Warning)
        {
            StopHeroProfilePulse();
            StopHeroRunningGlow();
            StartHeroGlint(isWarning: true);
        }
        else
        {
            StopHeroGlint();
            StopHeroProfilePulse();
            StopHeroRunningGlow();
        }
    }

    private async Task ValidateInstallationOnStartup()
    {
        if (!Settings.IsZapretInstalled)
            return;

        bool isValid = await Task.Run(() => _installer.ValidateLocalInstall());

        if (isValid)
        {
            AppLogger.Info("Локальная установка zapret проверена.");
            Dispatcher.Invoke(RefreshExpertPage);
        }
        else
        {
            AppLogger.Warning("Файлы zapret не найдены. Сброс состояния установки.");

            Settings.IsZapretInstalled = false;
            SafeSaveSettings();

            // Sync with UI
            Dispatcher.Invoke(() =>
            {
                UpdateInstallCard(filesMissing: true);
                RefreshExpertPage();
            });
        }
    }

    // ─── Updates ──────────────────────────────────────────────────────────────

    private async void UpdateCheckButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Проверка обновлений пропущена: выполняется автоподбор профиля.");
            return;
        }

        if (_isCheckingUpdates) return;

        var now = DateTime.UtcNow;
        if ((now - _lastManualUpdateCheckUtc).TotalSeconds < 3)
        {
            return;
        }

        _lastManualUpdateCheckUtc = now;
        // Manual refresh always checks every source regardless of settings
        await CheckZapretUpdatesAsync(isAutomatic: false, checkZapret: true, checkKmestu: true);
    }

    private async Task CheckZapretUpdatesAsync(bool isAutomatic, bool checkZapret = true, bool checkKmestu = true)
    {
        if (_isWizardRunning)
        {
            if (!isAutomatic)
            {
                AppLogger.Info("Проверка обновлений пропущена: выполняется автоподбор профиля.");
            }

            return;
        }

        if (_isCheckingUpdates)
        {
            if (!isAutomatic)
                AppLogger.Info("Проверка обновлений уже выполняется.");
            return;
        }

        _isCheckingUpdates = true;
        _hasUpdateCheckError = false;
        try
        {
            if (isAutomatic)
                AppLogger.Info("Автопроверка обновлений при запуске включена.");

            AppLogger.Info($"Проверка обновлений [zapret={checkZapret}, Kmestu={checkKmestu}]");

            // UI state: checking
            Dispatcher.Invoke(() =>
            {
                UpdateUpdateStatusUi();

                var animation = new System.Windows.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 360,
                    Duration = new Duration(TimeSpan.FromSeconds(1)),
                    RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
                };
                if (UpdateCheckIconRotation != null)
                {
                    UpdateCheckIconRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, animation);
                }
            });

            // ── 1. Fetch zapret (Flowseal) release ────────────────────────────
            if (checkZapret)
            {
                try
                {
                    _latestRelease = await _releaseService.GetLatestReleaseAsync();

                    string installedRaw = Settings.InstalledZapretVersion;
                    string latestRaw = _latestRelease.TagName;

                    AppLogger.Info($"Установленная версия zapret: {(!string.IsNullOrWhiteSpace(installedRaw) ? installedRaw : "не установлена")}");
                    AppLogger.Info($"Последняя версия Flowseal: {latestRaw}");

                    _isUpdateAvailable = Settings.IsZapretInstalled && !IsSameVersion(installedRaw, latestRaw);
                }
                catch (Exception exZapret)
                {
                    // Non-fatal: if zapret release check fails, skip silently
                    AppLogger.Warning($"Не удалось проверить обновление zapret: {exZapret.Message}");
                    _latestRelease = null;
                    _isUpdateAvailable = false;
                }
            }

            // ── 2. Fetch Zapret Kmestu release ────────────────────────────────
            if (checkKmestu)
            {
                try
                {
                    using var http = GitHubReleaseService.CreateHttpClient();
                    string kmestuJson = await http.GetStringAsync(
                        "https://api.github.com/repos/kmestu/ZapretKmestu/releases/latest").ConfigureAwait(false);

                    using var doc = System.Text.Json.JsonDocument.Parse(kmestuJson);
                    var root = doc.RootElement;

                    _latestKmestuRelease = root
                        .TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == System.Text.Json.JsonValueKind.String
                        ? tagEl.GetString() ?? ""
                        : "";

                    // Find the ZapretKmestu ZIP asset download URL (.zip ending)
                    _latestKmestuZipUrl = null;
                    if (root.TryGetProperty("assets", out var assetsEl) &&
                        assetsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var asset in assetsEl.EnumerateArray())
                        {
                            if (asset.TryGetProperty("name", out var nameEl) &&
                                nameEl.ValueKind == System.Text.Json.JsonValueKind.String &&
                                nameEl.GetString()?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true)
                            {
                                if (asset.TryGetProperty("browser_download_url", out var urlEl) &&
                                    urlEl.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    _latestKmestuZipUrl = urlEl.GetString();
                                }
                                break;
                            }
                        }
                    }

                    // Compare with current assembly informational version
                    string currentKmestuVersion = System.Reflection.CustomAttributeExtensions
                        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(
                            System.Reflection.Assembly.GetExecutingAssembly())
                        ?.InformationalVersion ?? "";

                    _isKmestuUpdateAvailable = !string.IsNullOrWhiteSpace(_latestKmestuRelease)
                        && IsNewerVersion(currentKmestuVersion, _latestKmestuRelease)
                        && !string.IsNullOrWhiteSpace(_latestKmestuZipUrl);

                    AppLogger.Info($"Текущая версия Kmestu: {currentKmestuVersion}");
                    AppLogger.Info($"Последняя версия Kmestu: {_latestKmestuRelease}");
                    AppLogger.Info($"URL ZIP-обновления Kmestu: {_latestKmestuZipUrl ?? "(не найден)"}");
                    AppLogger.Info($"Обновление Kmestu доступно: {_isKmestuUpdateAvailable}");
                }
                catch (Exception exKmestu)
                {
                    // Non-fatal: if Kmestu release check fails, just skip it silently
                    AppLogger.Info($"Не удалось проверить обновление Kmestu: {exKmestu.Message}");
                    _latestKmestuRelease = null;
                    _latestKmestuZipUrl = null;
                    _isKmestuUpdateAvailable = false;
                }
            }

            _hasUpdateCheckError = false;
            _hasCheckedUpdates = true;

            Dispatcher.Invoke(() =>
            {
                UpdateUpdateStatusUi();

                if (!Settings.IsZapretInstalled)
                    AppLogger.Info("Результат zapret: не установлен");
                else if (!_isUpdateAvailable)
                    AppLogger.Info("Результат zapret: актуальная версия");
                else
                    AppLogger.Info($"Результат zapret: доступно обновление ({_latestRelease?.TagName})");

                // Refresh the install card to show the Update button if needed
                UpdateInstallCard();
            });

            if (Settings.AutoUpdateZapret && _isUpdateAvailable && _latestRelease != null && _installCts == null)
            {
                AppLogger.Info("Автообновление zapret включено. Запуск автоматического обновления...");
                _ = Dispatcher.InvokeAsync(async () => await RunZapretInstallOrUpdateAsync());
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при проверке обновлений: {ex.Message}");
            _hasUpdateCheckError = true;
            _isUpdateAvailable = false;
            _isKmestuUpdateAvailable = false;
            _latestKmestuZipUrl = null;
            _latestRelease = null;
            _latestKmestuRelease = null;

            Dispatcher.Invoke(() =>
            {
                UpdateUpdateStatusUi();

                if (!isAutomatic)
                {
                    SetFooterMessage("Не удалось проверить обновления.", FooterMessageKind.Error, highlight: true);
                }
            });
        }
        finally
        {
            _isCheckingUpdates = false;
            Dispatcher.Invoke(() => 
            {
                UpdateUpdateStatusUi();
                if (UpdateCheckIconRotation != null)
                {
                    UpdateCheckIconRotation.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, null);
                }
            });
        }
    }

    private bool IsSameVersion(string installed, string latest)
    {
        var v1 = NormalizeVersion(installed);
        var v2 = NormalizeVersion(latest);

        bool match = string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase);
        AppLogger.Info($"Сравнение версий: '{v1}' == '{v2}' -> {(match ? "совпадают" : "различаются")}");

        return match;
    }

    private bool IsNewerVersion(string installed, string latest)
    {
        var v1 = NormalizeVersion(installed);
        var v2 = NormalizeVersion(latest);

        if (string.Equals(v1, v2, StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Info($"Сравнение версий: '{v1}' == '{v2}' -> совпадают (обновление не требуется)");
            return false;
        }

        string[] parts1 = v1.Split(new[] { '-' }, 2);
        string[] parts2 = v2.Split(new[] { '-' }, 2);

        Version parsed1 = Version.TryParse(parts1[0], out var p1) ? p1 : new Version(0, 0);
        Version parsed2 = Version.TryParse(parts2[0], out var p2) ? p2 : new Version(0, 0);

        if (parsed2 > parsed1)
        {
            AppLogger.Info($"Сравнение версий: '{v2}' > '{v1}' -> доступно обновление");
            return true;
        }
        else if (parsed2 < parsed1)
        {
            AppLogger.Info($"Сравнение версий: '{v2}' < '{v1}' -> установлена более новая версия");
            return false;
        }

        // Numeric versions are equal, check pre-release tags
        bool hasTag1 = parts1.Length > 1;
        bool hasTag2 = parts2.Length > 1;

        if (!hasTag1 && hasTag2) return false; // 0.2 > 0.2-beta
        if (hasTag1 && !hasTag2) return true;  // 0.2-beta < 0.2

        if (hasTag1 && hasTag2)
        {
            int cmp = string.Compare(parts1[1], parts2[1], StringComparison.OrdinalIgnoreCase);
            if (cmp < 0)
            {
                AppLogger.Info($"Сравнение версий: '{v2}' > '{v1}' (по тегу) -> доступно обновление");
                return true;
            }
        }

        AppLogger.Info($"Сравнение версий: '{v2}' <= '{v1}' -> обновление не требуется");
        return false;
    }

    private string NormalizeVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return "";

        v = v.Trim();

        // 1. Remove build metadata (e.g., "+6042b9bb...")
        int plusIndex = v.IndexOf('+');
        if (plusIndex >= 0)
        {
            v = v.Substring(0, plusIndex);
        }

        // 2. Remove leading 'v'
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            v = v.Substring(1);
        }

        // Try zapret-specific normalization (e.g. 1.9.9d -> 1.9.9.4)
        var zapretMatch = System.Text.RegularExpressions.Regex.Match(v, @"^([0-9]+)\.([0-9]+)\.([0-9]+)([a-zA-Z])?$");
        if (zapretMatch.Success)
        {
            string major = zapretMatch.Groups[1].Value;
            string minor = zapretMatch.Groups[2].Value;
            string build = zapretMatch.Groups[3].Value;
            string letterGroup = zapretMatch.Groups[4].Value;

            int revision = 0;
            if (!string.IsNullOrEmpty(letterGroup))
            {
                char c = char.ToLower(letterGroup[0]);
                if (c >= 'a' && c <= 'z')
                {
                    revision = c - 'a' + 1; // a=1, b=2, c=3, d=4, etc.
                }
            }
            return $"{major}.{minor}.{build}.{revision}";
        }

        // 3. Normalize semver components (e.g., "0.2.0-beta" -> "0.2-beta")
        string[] parts = v.Split(new[] { '-' }, 2);
        string versionPart = parts[0];

        if (Version.TryParse(versionPart, out Version parsedVersion))
        {
            int major = parsedVersion.Major >= 0 ? parsedVersion.Major : 0;
            int minor = parsedVersion.Minor >= 0 ? parsedVersion.Minor : 0;
            int build = parsedVersion.Build >= 0 ? parsedVersion.Build : 0;

            // Omit build component if it is 0 (so "0.2.0" == "0.2")
            if (build == 0)
            {
                versionPart = $"{major}.{minor}";
            }
            else
            {
                versionPart = $"{major}.{minor}.{build}";
            }
        }

        if (parts.Length > 1)
        {
            return $"{versionPart}-{parts[1]}";
        }

        return versionPart;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Update Center UI helpers
    // ─────────────────────────────────────────────────────────────────────────

    public class AppUpdateItem
    {
        public string Name        { get; set; } = string.Empty;
        public string Version     { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    private void UpdateUpdateStatusUi()
    {
        if (UpdateCheckButton == null || HeaderInstallUpdateButton == null || UpdateStatusBorder == null || UpdateStatusText == null || UpdateRefreshIconPath == null || HeaderInstallUpdateIconPath == null) return;

        // Ensure both buttons are always visible
        UpdateCheckButton.Visibility = Visibility.Visible;
        HeaderInstallUpdateButton.Visibility = Visibility.Visible;

        // Reset badge outline (always borderless, soft pill only)
        UpdateStatusBorder.BorderThickness = new Thickness(0);
        UpdateStatusBorder.Padding = new Thickness(10, 0, 10, 0);

        // UpdateCheckButton enabled state
        bool updateInteractionAllowed = !_isWizardRunning;

        bool isCheckEnabled =
            updateInteractionAllowed &&
            !_isCheckingUpdates &&
            _installCts == null &&
            !_isKmestuUpdating;

        UpdateCheckButton.IsEnabled = isCheckEnabled;
        UpdateCheckButton.IsHitTestVisible = updateInteractionAllowed;
        UpdateCheckButton.Focusable = updateInteractionAllowed;
        UpdateCheckButton.IsTabStop = updateInteractionAllowed;
        UpdateCheckButton.Cursor = isCheckEnabled
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;
        UpdateCheckButton.ToolTip = _isWizardRunning ? "Недоступно во время автоподбора" : "Проверить обновления";

        // Determine if updates are available
        bool hasZapretUpdate = (!Settings.IsZapretInstalled && _latestRelease != null) || _isUpdateAvailable || _hasInstallError;
        bool hasKmestuUpdate = _isKmestuUpdateAvailable && !string.IsNullOrWhiteSpace(_latestKmestuRelease);

#if DEBUG
        if (_updateCenterDebugState != UpdateCenterDebugState.None && !_isCheckingUpdates && _installCts == null && !_hasInstallError && !_hasUpdateCheckError)
        {
            hasKmestuUpdate = _updateCenterDebugState == UpdateCenterDebugState.OnlyKmestu || _updateCenterDebugState == UpdateCenterDebugState.Both;
            hasZapretUpdate = _updateCenterDebugState == UpdateCenterDebugState.OnlyZapret || _updateCenterDebugState == UpdateCenterDebugState.Both;
        }
#endif

        // HeaderInstallUpdateButton enabled state
        bool isInstallUpdateEnabled = (hasZapretUpdate || hasKmestuUpdate) &&
                                     !_isWizardRunning &&
                                     !_isCheckingUpdates &&
                                     _installCts == null &&
                                     !_isKmestuUpdating;
        HeaderInstallUpdateButton.IsEnabled = isInstallUpdateEnabled;

        // Tooltip state for install/update button
        string installUpdateToolTip;
        if (_isKmestuUpdating)
            installUpdateToolTip = "Загрузка обновления Kmestu...";
        else if (_installCts != null)
            installUpdateToolTip = "Установка выполняется";
        else if (_isCheckingUpdates)
            installUpdateToolTip = "Проверка выполняется";
        else if (hasZapretUpdate && hasKmestuUpdate)
            installUpdateToolTip = "Выбрать обновление";
        else if (hasKmestuUpdate)
            installUpdateToolTip = "Открыть обновление программы";
        else if (hasZapretUpdate)
        {
            if (_hasInstallError)
                installUpdateToolTip = !Settings.IsZapretInstalled ? "Установить обход" : "Обновить обход";
            else if (!Settings.IsZapretInstalled)
                installUpdateToolTip = "Установить обход";
            else
                installUpdateToolTip = "Обновить обход";
        }
        else
            installUpdateToolTip = "Обновление не требуется";
        HeaderInstallUpdateButton.ToolTip = installUpdateToolTip;

        // Update badge using real data
        RefreshUpdateCenterUI();
    }

    /// <summary>
    /// Populates the update center badge and popup rows from real check results.
    /// Called from UpdateUpdateStatusUi() every time state changes.
    /// </summary>
    private void RefreshUpdateCenterUI()
    {
        var items = new System.Collections.Generic.List<AppUpdateItem>();

        // ── Determine badge visual state ──────────────────────────────────────

        if (_isCheckingUpdates)
        {
            // Checking in progress
            SetBadge("Проверяем...", "UpdateBadgeNeutralBackgroundBrush", "UpdateBadgeNeutralTextBrush", arrowVisible: false, clickable: false);
            UpdateItemsControl.ItemsSource = items;
            return;
        }

        if (_isKmestuUpdating)
        {
            SetBadge("Загрузка Kmestu...", "UpdateBadgeBusyBackgroundBrush", "UpdateBadgeBusyTextBrush", arrowVisible: false, clickable: false);
            UpdateItemsControl.ItemsSource = items;
            return;
        }

        if (_installCts != null)
        {
            string ver = _latestRelease?.TagName ?? "zapret";
            SetBadge($"Установка {ver}...", "UpdateBadgeBusyBackgroundBrush", "UpdateBadgeBusyTextBrush", arrowVisible: false, clickable: false);
            UpdateItemsControl.ItemsSource = items;
            return;
        }

        if (_hasInstallError)
        {
            SetBadge("Ошибка установки", "UpdateBadgeErrorBackgroundBrush", "UpdateBadgeErrorTextBrush", arrowVisible: false, clickable: false);
            UpdateItemsControl.ItemsSource = items;
            return;
        }

        if (_hasUpdateCheckError)
        {
            SetBadge("Не удалось проверить", "UpdateBadgeErrorBackgroundBrush", "UpdateBadgeErrorTextBrush", arrowVisible: false, clickable: false);
            UpdateItemsControl.ItemsSource = items;
            return;
        }

        if (!_hasCheckedUpdates)
        {
            SetBadge("Не проверено", "UpdateBadgeNeutralBackgroundBrush", "UpdateBadgeNeutralTextBrush", arrowVisible: false, clickable: false);
            UpdateItemsControl.ItemsSource = items;
            return;
        }

        // ── Build popup rows from real data ───────────────────────────────────

        bool showKmestu = _isKmestuUpdateAvailable && !string.IsNullOrWhiteSpace(_latestKmestuRelease);
        bool showZapret = _isUpdateAvailable && _latestRelease != null;
        bool showZapretInstall = !Settings.IsZapretInstalled && _latestRelease != null;

#if DEBUG
        if (_updateCenterDebugState != UpdateCenterDebugState.None && !_isCheckingUpdates && _installCts == null && !_hasInstallError && !_hasUpdateCheckError)
        {
            showKmestu = _updateCenterDebugState == UpdateCenterDebugState.OnlyKmestu || _updateCenterDebugState == UpdateCenterDebugState.Both;
            showZapret = _updateCenterDebugState == UpdateCenterDebugState.OnlyZapret || _updateCenterDebugState == UpdateCenterDebugState.Both;
            showZapretInstall = false;
        }
#endif

        if (showKmestu)
        {
            items.Add(new AppUpdateItem
            {
                Name        = "Kmestu",
                Version     = _latestKmestuRelease ?? "v0.2 beta",
                Description = "Обновление программы"
            });
        }

        if (showZapret)
        {
            items.Add(new AppUpdateItem
            {
                Name        = "zapret",
                Version     = _latestRelease?.TagName ?? "1.9.9e",
                Description = "Обновление обхода"
            });
        }
        else if (showZapretInstall)
        {
            items.Add(new AppUpdateItem
            {
                Name        = "zapret",
                Version     = _latestRelease?.TagName ?? "1.9.9e",
                Description = "Установка обхода"
            });
        }

        // ── Apply badge state based on item count ─────────────────────────────

        if (items.Count == 0)
        {
            SetBadge("Всё актуально", "UpdateBadgeNeutralBackgroundBrush", "UpdateBadgeNeutralTextBrush", arrowVisible: false, clickable: false);
        }
        else if (items.Count == 1)
        {
            SetBadge(items[0].Version, "UpdateBadgeAvailableBackgroundBrush", "UpdateBadgeAvailableTextBrush", arrowVisible: true, clickable: true);
        }
        else
        {
            SetBadge("Есть обновления", "UpdateBadgeAvailableBackgroundBrush", "UpdateBadgeAvailableTextBrush", arrowVisible: true, clickable: true);
        }

        UpdateItemsControl.ItemsSource = items;
    }

    private void SetBadge(string text, string backgroundKey, string foregroundKey, bool arrowVisible, bool clickable)
    {
        UpdateStatusText.Text = text;
        UpdateStatusBorder.Background = (System.Windows.Media.Brush)FindResource(backgroundKey);
        UpdateStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, foregroundKey);
        UpdateStatusBorder.Cursor = clickable
            ? System.Windows.Input.Cursors.Hand
            : System.Windows.Input.Cursors.Arrow;

        if (UpdateStatusArrow != null)
        {
            UpdateStatusArrow.Visibility = arrowVisible ? Visibility.Visible : Visibility.Collapsed;
            if (arrowVisible)
                UpdateStatusArrow.SetResourceReference(System.Windows.Shapes.Path.FillProperty, foregroundKey);
        }
    }

    private void UpdateStatusBorder_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isWizardRunning)
        {
            e.Handled = true;
            return;
        }

        // Only open popup if there are real update items to show
        var items = UpdateItemsControl.ItemsSource as System.Collections.Generic.List<AppUpdateItem>;
        if (items == null || items.Count == 0) return;

        UpdateCenterPopup.IsOpen = !UpdateCenterPopup.IsOpen;
        e.Handled = true;
    }

    private void Window_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!UpdateCenterPopup.IsOpen) return;

        // If the click originated on the badge itself, let the badge handler deal with it
        if (e.OriginalSource is DependencyObject src &&
            (UpdateStatusBorder.IsAncestorOf(src) || ReferenceEquals(src, UpdateStatusBorder)))
        {
            return;
        }

        UpdateCenterPopup.IsOpen = false;
    }

    private void CloseUpdateCenterPopup()
    {
        if (UpdateCenterPopup != null && UpdateCenterPopup.IsOpen)
        {
            UpdateCenterPopup.IsOpen = false;
        }
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        CloseUpdateCenterPopup();
    }

    private async void UpdateItem_Click(object sender, RoutedEventArgs e)
    {
        UpdateCenterPopup.IsOpen = false;

        if (sender is FrameworkElement fe && fe.DataContext is AppUpdateItem item)
        {
            if (item.Name == "Kmestu")
            {
                await RunKmestuUpdateAsync();
            }
            else if (item.Name == "zapret")
            {
                await RunZapretInstallOrUpdateAsync();
            }
        }
    }

    // ─── Navigation ───────────────────────────────────────────────────────────

    private void NavHome_Click(object sender, RoutedEventArgs e)
        => ShowPage("home");

    private void NavExpert_Click(object sender, RoutedEventArgs e)
        => ShowPage("expert");

    private void NavScenarios_Click(object sender, RoutedEventArgs e)
        => ShowPage("scenarios");

    private void NavDiag_Click(object sender, RoutedEventArgs e)
        => ShowPage("diagnostics");

    private void NavSettings_Click(object sender, RoutedEventArgs e)
        => ShowPage("settings");


    private void NavLog_Click(object sender, RoutedEventArgs e)
        => NavigateToLog();

    private void NavigateToLog()
    {
        RefreshJournal();
        ShowPage("log");
    }

    private void SwitchToDiagTab(string tabName)
    {
        if (TabOverviewButton == null || TabGraphsButton == null || TabLogButton == null ||
            DiagOverviewTabContent == null || DiagGraphsTabContent == null || DiagLogTabContent == null) return;

        // 1. Update button styles
        TabOverviewButton.Style = (Style)FindResource(tabName == "overview" ? "TabDiagButtonActiveStyle" : "TabDiagButtonStyle");
        TabGraphsButton.Style = (Style)FindResource(tabName == "graphs" ? "TabDiagButtonActiveStyle" : "TabDiagButtonStyle");
        TabLogButton.Style = (Style)FindResource(tabName == "log" ? "TabDiagButtonActiveStyle" : "TabDiagButtonStyle");

        // 2. Toggle visibility
        DiagOverviewTabContent.Visibility = tabName == "overview" ? Visibility.Visible : Visibility.Collapsed;
        DiagGraphsTabContent.Visibility = tabName == "graphs" ? Visibility.Visible : Visibility.Collapsed;
        DiagLogTabContent.Visibility = tabName == "log" ? Visibility.Visible : Visibility.Collapsed;

        // 3. Perform tab-specific updates
        if (tabName == "graphs")
        {
            UpdateStabilityGraph();
        }
        else if (tabName == "log")
        {
            RefreshJournal();
        }
    }

    private void TabOverview_Click(object sender, RoutedEventArgs e) => SwitchToDiagTab("overview");
    private void TabGraphs_Click(object sender, RoutedEventArgs e) => SwitchToDiagTab("graphs");
    private void TabLog_Click(object sender, RoutedEventArgs e) => SwitchToDiagTab("log");

    private void ShowPage(string page)
    {
        string requestedPage = page;

        if (!Settings.UseDiagnostics && (page == "diagnostics" || page == "log"))
        {
            page = "home";
            requestedPage = "home";
        }

        if (page == "help" || page == "Помощь")
        {
            page = "home";
            requestedPage = "home";
        }

        if (page == "log")
        {
            SwitchToDiagTab("log");
            page = "diagnostics";
        }
        else if (page == "diagnostics")
        {
            SwitchToDiagTab("graphs");
        }

        PageHome.Visibility      = page == "home"        ? Visibility.Visible : Visibility.Collapsed;
        PageExpert.Visibility    = page == "expert"      ? Visibility.Visible : Visibility.Collapsed;
        PageScenarios.Visibility = page == "scenarios"   ? Visibility.Visible : Visibility.Collapsed;
        PageDiag.Visibility      = page == "diagnostics" ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility  = page == "settings"    ? Visibility.Visible : Visibility.Collapsed;

        if (page == "scenarios")
        {
            UpdateScenarioStatusUi();
            SyncGameFilterUiFromActualState();
        }

        NavHomeButton.Style      = GetNavStyle(page == "home");
        NavExpertButton.Style    = GetNavStyle(page == "expert");
        if (NavScenariosButton != null) NavScenariosButton.Style = GetNavStyle(page == "scenarios");
        NavDiagButton.Style      = GetNavStyle(page == "diagnostics");
        NavSettingsButton.Style  = GetNavStyle(page == "settings");

        if (NavLogButton != null) NavLogButton.Style = GetNavStyle(page == "log");

        // Save last page — do NOT spam the log
        var pageName = requestedPage switch
        {
            "home"        => "Главная",
            "expert"      => "Профили",
            "scenarios"   => "Сценарии",
            "diagnostics" => "Диагностика",
            "settings"    => "Настройки",
            "help"        => "Помощь",
            "log"         => "Журнал",
            _             => requestedPage
        };
        Settings.LastPage = pageName;
        SafeSaveSettings();
    }

    private void RestoreLastPage()
    {
        if (!Settings.OpenLastPageOnStartup)
        {
            ShowPage("home");
            return;
        }

        var page = Settings.LastPage switch
        {
            "Профили"     => "home", // Fallback since hidden
            "Сценарии"    => Settings.ShowWorkModesSection ? "scenarios" : "home",
            "Диагностика" => Settings.UseDiagnostics ? "diagnostics" : "home",
            "Настройки"   => "settings",
            "Помощь"      => "home",
            "Журнал"      => Settings.UseDiagnostics ? "log" : "home",
            _             => "home"
        };
        ShowPage(page);
        AppLogger.Info($"Открыта страница: {Settings.LastPage}");
    }

    private Style GetNavStyle(bool active) =>
        (Style)System.Windows.Application.Current.Resources[active ? "NavButtonActiveStyle" : "NavButtonStyle"];

    private string _lastFooterMessage = "";
    private DateTime _lastHighlightTime = DateTime.MinValue;

    private void PulseFooterStatus()
    {
        if (FooterHighlightLayer == null) return;

        var anim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(275)),
            AutoReverse = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
        };

        FooterHighlightLayer.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void SetFooterMessage(string message, FooterMessageKind kind = FooterMessageKind.Info, bool highlight = false, bool suppressPulse = false)
    {
        if (string.IsNullOrEmpty(message)) return;

        if (_suppressWizardFooterNoise && kind != FooterMessageKind.Error)
        {
            if (!message.StartsWith("Подбор профиля") && !message.StartsWith("Идёт проверка профилей"))
            {
                return;
            }
        }

        Dispatcher.Invoke(() =>
        {
            bool messageChanged = message != _lastFooterMessage;
            _lastFooterMessage = message;
            FooterWarningText.Text = message;

            if (_isInitialized && messageChanged && !suppressPulse)
            {
                PulseFooterStatus();
            }
        });
    }

    // ─── Settings — Work Modes (UI Only) ───────────────────────────────────────

    private async void GameFilterToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isWizardRunning)
        {
            e.Handled = true;
            AppLogger.Info("Blocked GameFilterToggle_Changed while auto-pick is active.");
            SyncGameFilterUiFromActualState();
            return;
        }
        if (!_isInitialized || _suppressGameFilterToggleEvents) return;
        
        if (_isGameFilterOperationRunning)
        {
            // Rapid click protection: revert visual toggle to true state and return
            SyncGameFilterUiFromActualState();
            return;
        }

        bool isOn = GameFilterToggle.IsChecked == true;
        _selectedWorkMode = isOn ? WorkModeGameKey : WorkModeStandardKey;
        Settings.SelectedWorkMode = _selectedWorkMode;
        SafeSaveSettings();
        AppLogger.Info($"Режим работы изменен (Game Filter): {_selectedWorkMode}");
        
        // Disable old UpdateWorkModeVisuals logic for GameFilterToggle as it is now authoritative
        UpdateWorkModeVisuals();
        
        await ApplyScenarioSettingsAsync();
    }

    private void UpdateWorkModeVisuals()
    {
        // Normalize any old "Services" mode to OFF on this page
        if (_selectedWorkMode == WorkModeServicesKey)
        {
            _selectedWorkMode = WorkModeStandardKey;
            Settings.SelectedWorkMode = _selectedWorkMode;
        }
    }

    private void GameFilterButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked GameFilterButton_Click while auto-pick is active.");
            return;
        }
        bool isRunning = _statusService.GetStatus().IsRunning;
        bool isGameFilterActive = isRunning && Settings.AppliedWorkMode == WorkModeGameKey;
        if (isGameFilterActive)
        {
            SetFooterMessage("Выключите Game Filter, чтобы изменить параметры.", FooterMessageKind.Warning, highlight: true);
            return;
        }

        if (GameFilterUdpButton == null || GameFilterTcpButton == null || GameFilterTcpUdpButton == null)
            return;

        var activeBorder = sender as System.Windows.Controls.Border;
        if (activeBorder == null) return;

        // Get filter text to update local state
        var textBlock = (activeBorder.Child as System.Windows.Controls.Grid)?.Children[0] as System.Windows.Controls.TextBlock;
        if (textBlock != null)
        {
            _selectedGameFilter = textBlock.Text;
            Settings.SelectedGameFilter = _selectedGameFilter;
            SafeSaveSettings();
            AppLogger.Info($"Фильтр трафика изменен: {_selectedGameFilter}");
            
            SetScenarioPendingChanges();
        }

        GameFilterUdpButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameFilterUdpButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
        
        GameFilterTcpButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameFilterTcpButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
        
        GameFilterTcpUdpButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameFilterTcpUdpButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");

        string activeBorderBrushName;
        string activeBackgroundBrushName;
        if (activeBorder == GameFilterUdpButton) {
            activeBorderBrushName = "PrimaryBrush";
            activeBackgroundBrushName = "PrimaryTintBrush";
        } else {
            activeBorderBrushName = "IndigoBrush";
            activeBackgroundBrushName = "IndigoTintBrush";
        }

        activeBorder.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, activeBorderBrushName);
        activeBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, activeBackgroundBrushName);
    }

    private void GameScopeButton_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked GameScopeButton_Click while auto-pick is active.");
            return;
        }
        bool isRunning = _statusService.GetStatus().IsRunning;
        bool isGameFilterActive = isRunning && Settings.AppliedWorkMode == WorkModeGameKey;
        if (isGameFilterActive)
        {
            SetFooterMessage("Выключите Game Filter, чтобы изменить параметры.", FooterMessageKind.Warning, highlight: true);
            return;
        }

        if (GameScopeListsButton == null || GameScopeExtendedButton == null || GameScopeAllButton == null)
            return;

        var activeBorder = sender as System.Windows.Controls.Border;
        if (activeBorder == null) return;

        // Get scope text to update local state
        var textBlock = (activeBorder.Child as System.Windows.Controls.Grid)?.Children[0] as System.Windows.Controls.TextBlock;
        if (textBlock != null)
        {
            _selectedGameScope = textBlock.Text;
            Settings.SelectedGameScope = _selectedGameScope;
            SafeSaveSettings();
            AppLogger.Info($"Охват трафика изменен: {_selectedGameScope}");
            
            SetScenarioPendingChanges();
        }

        GameScopeListsButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameScopeListsButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
        
        GameScopeExtendedButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameScopeExtendedButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
        
        GameScopeAllButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameScopeAllButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");

        string activeBorderBrushName;
        string activeBackgroundBrushName;
        if (activeBorder == GameScopeListsButton) {
            activeBorderBrushName = "SuccessBrush";
            activeBackgroundBrushName = "SuccessTintBrush";
        } else {
            activeBorderBrushName = "WarningBrush";
            activeBackgroundBrushName = "WarningTintBrush";
        }

        activeBorder.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, activeBorderBrushName);
        activeBorder.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, activeBackgroundBrushName);
    }

    private void RecalculateScenarioPendingState()
    {
        _hasPendingScenarioChanges = !IsSelectedScenarioSameAsApplied();
        UpdateScenarioStatusUi();
    }

    private void SetScenarioPendingChanges()
    {
        RecalculateScenarioPendingState();
    }

    private void UpdateScenarioStatusUi()
    {
        if (ApplyGameFilterStatusText == null || ScenarioStatusBadge == null) return;
        SyncGameFilterUiFromActualState();
    }

    private void ShowSuccessStatus()
    {
        // Handled by RefreshVpnStatusAsync => ApplyStatusToUi
    }


    private void SyncGameFilterUiFromActualState(ZapretServiceStatusInfo? status = null, bool isBusy = false, string? busyStatus = null)
    {
        if (GameFilterToggle == null || ScenarioStatusBadge == null || ApplyGameFilterStatusText == null)
            return;

        if (_isWizardRunning)
        {
            ApplyGameFilterStatusText.Text = "Недоступно в момент подбора";
            ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BusyBadgeTextBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BusyBadgeBackgroundBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BusyBadgeBorderBrush");
            ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(1);

            _suppressGameFilterToggleEvents = true;
            try
            {
                GameFilterToggle.IsEnabled = false;
                GameFilterToggle.IsHitTestVisible = false;
                GameFilterToggle.Focusable = false;
                GameFilterToggle.IsTabStop = false;

                if (ScenarioGameOptionsPanel != null)
                {
                    ScenarioGameOptionsPanel.IsEnabled = false;
                    ScenarioGameOptionsPanel.Opacity = 0.5;
                }

                if (GameFilterLockedHint != null)
                {
                    GameFilterLockedHint.Visibility = System.Windows.Visibility.Visible;
                }
            }
            finally
            {
                _suppressGameFilterToggleEvents = false;
            }
            return;
        }

        if (isBusy && !string.IsNullOrEmpty(busyStatus))
        {
            ApplyGameFilterStatusText.Text = busyStatus;
            ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "BusyBadgeTextBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BusyBadgeBackgroundBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BusyBadgeBorderBrush");
            ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(1);

            _suppressGameFilterToggleEvents = true;
            try
            {
                GameFilterToggle.IsEnabled = false;
                GameFilterToggle.IsHitTestVisible = false;
                GameFilterToggle.Focusable = false;
                GameFilterToggle.IsTabStop = false;

                if (ScenarioGameOptionsPanel != null)
                {
                    ScenarioGameOptionsPanel.IsEnabled = false;
                    ScenarioGameOptionsPanel.Opacity = 0.5;
                }

                if (GameFilterLockedHint != null)
                {
                    GameFilterLockedHint.Visibility = System.Windows.Visibility.Visible;
                }
            }
            finally
            {
                _suppressGameFilterToggleEvents = false;
            }
            return;
        }

        // Active state depends strictly on actual running service AND applied mode
        var currentStatus = status ?? _statusService.GetStatus();
        bool isRunning = currentStatus.IsRunning;
        bool serviceExists = currentStatus.Exists;
        bool localValid = false;
        try { localValid = _installer.ValidateLocalInstall(); } catch { }

        // State 4: Files absent/invalid, service absent
        if (!serviceExists && !localValid)
        {
            ApplyGameFilterStatusText.Text = "zapret не установлен";
            ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(0);
            ScenarioStatusBadge.BorderBrush = null;
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DangerTintBrush");
            ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");

            _suppressGameFilterToggleEvents = true;
            try
            {
                GameFilterToggle.IsEnabled = false;
                GameFilterToggle.IsChecked = false;
                if (ScenarioGameOptionsPanel != null) { ScenarioGameOptionsPanel.IsEnabled = false; ScenarioGameOptionsPanel.Opacity = 0.5; }
                if (GameFilterLockedHint != null) { GameFilterLockedHint.Visibility = System.Windows.Visibility.Collapsed; }
            }
            finally
            {
                _suppressGameFilterToggleEvents = false;
            }
            return;
        }

        // State 3: Files valid, service absent
        if (!serviceExists && localValid)
        {
            ApplyGameFilterStatusText.Text = "Требуется установка службы";
            ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(0);
            ScenarioStatusBadge.BorderBrush = null;
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DangerTintBrush");
            ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");

            _suppressGameFilterToggleEvents = true;
            try
            {
                GameFilterToggle.IsEnabled = false;
                GameFilterToggle.IsChecked = false;
                if (ScenarioGameOptionsPanel != null) { ScenarioGameOptionsPanel.IsEnabled = false; ScenarioGameOptionsPanel.Opacity = 0.5; }
                if (GameFilterLockedHint != null) { GameFilterLockedHint.Visibility = System.Windows.Visibility.Collapsed; }
            }
            finally
            {
                _suppressGameFilterToggleEvents = false;
            }
            return;
        }

        // State 5: Service exists but files invalid
        if (serviceExists && !localValid)
        {
            ApplyGameFilterStatusText.Text = "Требуется восстановление";
            ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(0);
            ScenarioStatusBadge.BorderBrush = null;
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DangerTintBrush");
            ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");

            _suppressGameFilterToggleEvents = true;
            try
            {
                GameFilterToggle.IsEnabled = false;
                GameFilterToggle.IsChecked = false;
                if (ScenarioGameOptionsPanel != null) { ScenarioGameOptionsPanel.IsEnabled = false; ScenarioGameOptionsPanel.Opacity = 0.5; }
                if (GameFilterLockedHint != null) { GameFilterLockedHint.Visibility = System.Windows.Visibility.Collapsed; }
            }
            finally
            {
                _suppressGameFilterToggleEvents = false;
            }
            return;
        }

        // States 1 & 2: Valid files and service exists
        bool isGameFilterActive = isRunning && Settings.AppliedWorkMode == WorkModeGameKey;

        bool isAdmin = _adminService.IsRunningAsAdministrator();
        bool hasConflictingOperation =
            isBusy ||
            _isGameFilterOperationRunning ||
            _isWizardRunning ||
            _installCts != null ||
            _isTrayBypassToggleRunning ||
            _isTrayProfileApplyRunning ||
            (OperationProgressCard != null && OperationProgressCard.Visibility == Visibility.Visible);

        bool canChangeGameFilter = Settings.IsZapretInstalled && isAdmin && !hasConflictingOperation;
        bool canEditOptions = canChangeGameFilter && !isGameFilterActive;

        _suppressGameFilterToggleEvents = true;
        try
        {
            GameFilterToggle.IsEnabled = canChangeGameFilter;
            GameFilterToggle.IsHitTestVisible = canChangeGameFilter;
            GameFilterToggle.Focusable = canChangeGameFilter;
            GameFilterToggle.IsTabStop = canChangeGameFilter;
            GameFilterToggle.IsChecked = isGameFilterActive;

            if (ScenarioGameOptionsPanel != null)
            {
                // Options are editable only when Game Filter is inactive and no operation is running
                ScenarioGameOptionsPanel.IsEnabled = canEditOptions;
                ScenarioGameOptionsPanel.Opacity = canEditOptions ? 1.0 : 0.5;
            }

            if (GameFilterLockedHint != null)
            {
                GameFilterLockedHint.Visibility = isGameFilterActive ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
            }

            ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(0);
            ScenarioStatusBadge.BorderBrush = null;
        }
        finally
        {
            _suppressGameFilterToggleEvents = false;
        }

        if (isGameFilterActive)
        {
            ApplyGameFilterStatusText.Text = "Активен";
            ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ActiveBadgeTextBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ActiveBadgeBackgroundBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "ActiveBadgeBorderBrush");
            ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(1);
        }
        else
        {
            // Only overwrite if it isn't currently displaying a failure state (handled directly in error path)
            bool isInstalled = Settings.IsZapretInstalled && !string.IsNullOrEmpty(AppPaths.ZapretDirectory) && System.IO.Directory.Exists(AppPaths.ZapretDirectory);
            if (ApplyGameFilterStatusText.Text != "Не удалось включить" &&
                ApplyGameFilterStatusText.Text != "Не удалось выключить" &&
                ApplyGameFilterStatusText.Text != "Не удалось применить" &&
                ApplyGameFilterStatusText.Text != "Список недоступен" &&
                (isInstalled || ApplyGameFilterStatusText.Text != "zapret не установлен"))
            {
                ApplyGameFilterStatusText.Text = "Неактивен";
                ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextSecondaryBrush");
                ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
            }
        }

        _suppressGameFilterToggleEvents = false;
    }

    private void ShowScenarioStatus(string message, bool isError)
    {
        if (ApplyGameFilterStatusText == null || ScenarioStatusBadge == null) return;
        ApplyGameFilterStatusText.Text = message;

        ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(0);
        ScenarioStatusBadge.BorderBrush = null;

        if (isError)
        {
            ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "DangerBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "DangerTintBrush");
        }
        else
        {
            ApplyGameFilterStatusText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "ActiveBadgeTextBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "ActiveBadgeBackgroundBrush");
            ScenarioStatusBadge.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "ActiveBadgeBorderBrush");
            ScenarioStatusBadge.BorderThickness = new System.Windows.Thickness(1);
        }
    }

    private string ExtractIpSetUrlFromServiceBat()
    {
        const string FallbackUrl = "https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/ipset-service.txt";
        try
        {
            string serviceBatPath = System.IO.Path.Combine(AppPaths.ZapretDirectory, "service.bat");
            if (System.IO.File.Exists(serviceBatPath))
            {
                var lines = System.IO.File.ReadLines(serviceBatPath);
                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("set \"url=", StringComparison.OrdinalIgnoreCase))
                    {
                        int startIndex = trimmed.IndexOf("url=", StringComparison.OrdinalIgnoreCase) + 4;
                        int endIndex = trimmed.LastIndexOf('"');
                        if (endIndex > startIndex)
                        {
                            string url = trimmed.Substring(startIndex, endIndex - startIndex).Trim();
                            if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                            {
                                AppLogger.Info($"ExtractIpSetUrlFromServiceBat: Успешно извлечен URL из service.bat: {url}");
                                return url;
                            }
                        }
                    }
                    else if (trimmed.StartsWith("set url=", StringComparison.OrdinalIgnoreCase))
                    {
                        string url = trimmed.Substring("set url=".Length).Trim().Trim('"');
                        if (Uri.IsWellFormedUriString(url, UriKind.Absolute))
                        {
                            AppLogger.Info($"ExtractIpSetUrlFromServiceBat: Успешно извлечен URL из service.bat (без кавычек): {url}");
                            return url;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"ExtractIpSetUrlFromServiceBat: Ошибка при парсинге service.bat: {ex.Message}");
        }
        return FallbackUrl;
    }

    private bool IsValidIpSetContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        string trimmed = content.Trim();
        if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || 
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<head", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("<body", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("http-equiv", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var lines = trimmed.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l) && !l.StartsWith("#"))
            .ToList();

        if (lines.Count < 2)
        {
            return false;
        }

        if (lines.All(l => l.Equals("203.0.113.113/32", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        bool hasIpLike = lines.Any(l => {
            return (l.Contains(".") || l.Contains(":")) && l.Any(char.IsDigit);
        });

        return hasIpLike;
    }

    private async Task<bool> EnsureIpSetBackupExistsAsync(string ipsetFile, string ipsetBackupFile)
    {
        string flowsealIpSetUrl = ExtractIpSetUrlFromServiceBat();

        // 1. If ipset-all.txt.backup exists and is valid
        if (System.IO.File.Exists(ipsetBackupFile) && new System.IO.FileInfo(ipsetBackupFile).Length > 0)
        {
            try
            {
                string backupContent = await System.IO.File.ReadAllTextAsync(ipsetBackupFile, System.Text.Encoding.UTF8);
                if (IsValidIpSetContent(backupContent))
                {
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"EnsureIpSetBackupExists: Ошибка чтения бэкапа {ipsetBackupFile}: {ex.Message}");
            }
        }

        // 2. If backup is missing/invalid, inspect current lists\ipset-all.txt
        if (System.IO.File.Exists(ipsetFile))
        {
            try
            {
                string currentContent = await System.IO.File.ReadAllTextAsync(ipsetFile, System.Text.Encoding.UTF8);
                if (IsValidIpSetContent(currentContent))
                {
                    System.IO.File.Copy(ipsetFile, ipsetBackupFile, overwrite: true);
                    AppLogger.Info($"EnsureIpSetBackupExists: Создана резервная копия {ipsetBackupFile} из текущего {ipsetFile}.");
                    return true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"EnsureIpSetBackupExists: Не удалось скопировать {ipsetFile} в резервную копию: {ex.Message}");
            }
        }

        // 3. If both backup and current are missing/invalid, download the official Flowseal list
        AppLogger.Info($"EnsureIpSetBackupExists: Бэкап отсутствует или невалиден, текущий ipset-all.txt также невалиден. Запускаем скачивание из {flowsealIpSetUrl}...");
        try
        {
            using (var http = GitHubReleaseService.CreateHttpClient())
            {
                http.Timeout = TimeSpan.FromSeconds(15);
                string downloadedContent = await http.GetStringAsync(flowsealIpSetUrl);
                if (IsValidIpSetContent(downloadedContent))
                {
                    await System.IO.File.WriteAllTextAsync(ipsetBackupFile, downloadedContent, System.Text.Encoding.UTF8);
                    try
                    {
                        await System.IO.File.WriteAllTextAsync(ipsetFile, downloadedContent, System.Text.Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Warning($"EnsureIpSetBackupExists: Не удалось записать также в {ipsetFile}: {ex.Message}");
                    }
                    AppLogger.Info($"EnsureIpSetBackupExists: Успешно скачан и сохранен бэкап {ipsetBackupFile} и {ipsetFile} (размер: {downloadedContent.Length} символов).");
                    return true;
                }
                else
                {
                    AppLogger.Error("EnsureIpSetBackupExists: Скачанное содержимое не прошло валидацию IP-списка.");
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"EnsureIpSetBackupExists: Исключение при скачивании списка IP с {flowsealIpSetUrl}: {ex.Message}");
        }

        return false;
    }

    private async Task ApplyScenarioSettingsAsync()
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked ApplyScenarioSettingsAsync while auto-pick is active.");
            return;
        }
        if (_isGameFilterOperationRunning) return;
        _isGameFilterOperationRunning = true;

        bool operationSuccessful = false;
        try
        {
            if (!Settings.IsZapretInstalled || string.IsNullOrEmpty(AppPaths.ZapretDirectory) || !System.IO.Directory.Exists(AppPaths.ZapretDirectory))
            {
                ShowScenarioStatus("zapret не установлен", isError: true);
                AppLogger.Warning("ApplyScenario: Отмена применения — zapret не установлен или папка отсутствует.");
                return;
            }

            string selectedProfile = Settings.SelectedProfile;
            if (string.IsNullOrEmpty(selectedProfile))
            {
                AppLogger.Error("ApplyScenario: Ошибка применения — выбранный профиль пуст или отсутствует в настройках.");
                ShowScenarioStatus("Не удалось применить", isError: true);
                return;
            }

            string profilePath = System.IO.Path.Combine(AppPaths.ZapretDirectory, selectedProfile);
            if (!System.IO.File.Exists(profilePath))
            {
                AppLogger.Error($"ApplyScenario: Ошибка применения — файл выбранного профиля отсутствует по пути: {profilePath}");
                ShowScenarioStatus("Не удалось применить", isError: true);
                return;
            }

            // Determine lists paths and prepare directory
            string listsPath = System.IO.Path.Combine(AppPaths.ZapretDirectory, "lists");
            string ipsetFile = System.IO.Path.Combine(listsPath, "ipset-all.txt");
            string ipsetBackupFile = System.IO.Path.Combine(listsPath, "ipset-all.txt.backup");

            try
            {
                if (!System.IO.Directory.Exists(listsPath))
                {
                    System.IO.Directory.CreateDirectory(listsPath);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"ApplyScenario: Не удалось создать папку lists: {ex.Message}");
                ShowScenarioStatus("Не удалось применить", isError: true);
                return;
            }

            // Run backup helper to capture current working ipset-all.txt or download it if needed
            bool backupExists = await EnsureIpSetBackupExistsAsync(ipsetFile, ipsetBackupFile);

            // If target scope is "Больше адресов" (Extended) in Game mode, we MUST have a valid backup file
            if (_selectedWorkMode == WorkModeGameKey && _selectedGameScope == "Больше адресов" && !backupExists)
            {
                AppLogger.Error($"ApplyScenario: Ошибка применения. Резервная копия ipset-all.txt.backup отсутствует по пути: {ipsetBackupFile} и не может быть восстановлена/скачана.");
                ShowScenarioStatus("Список недоступен", isError: true);
                return;
            }

            bool isTurningOn = _selectedWorkMode == WorkModeGameKey;
            string busyMessage = isTurningOn ? "Включается..." : "Выключается...";
            
            Dispatcher.Invoke(() => {
                SyncGameFilterUiFromActualState(isBusy: true, busyStatus: busyMessage);
            });
            
            // Brief delay for visual transition feedback
            await Task.Delay(500);

            string utilsPath = System.IO.Path.Combine(AppPaths.ZapretDirectory, "utils");
            string gameFlagFile = System.IO.Path.Combine(utilsPath, "game_filter.enabled");

            // Step 1: Write/Delete game_filter.enabled based on work mode
            try
            {
                if (!System.IO.Directory.Exists(utilsPath))
                {
                    System.IO.Directory.CreateDirectory(utilsPath);
                }

                if (_selectedWorkMode == WorkModeStandardKey || _selectedWorkMode == WorkModeServicesKey)
                {
                    if (System.IO.File.Exists(gameFlagFile))
                    {
                        System.IO.File.Delete(gameFlagFile);
                        AppLogger.Info("ApplyScenario: Файл game_filter.enabled успешно удален для стандартного/сервисного сценария.");
                    }
                }
                else // Game mode
                {
                    string filterValue;
                    if (_selectedGameFilter == "TCP + UDP") 
                        filterValue = "all";
                    else if (_selectedGameFilter == "TCP") 
                        filterValue = "tcp";
                    else 
                        filterValue = "udp"; // Default UDP

                    // Enforce UTF-8 without BOM to prevent cmd/batch parser corruption in service.bat
                    System.IO.File.WriteAllText(gameFlagFile, filterValue, new System.Text.UTF8Encoding(false));
                    AppLogger.Info($"ApplyScenario: Файл game_filter.enabled успешно обновлен (значение: {filterValue}) для игрового сценария.");
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"ApplyScenario: Исключение при работе с файлом {gameFlagFile}: {ex.Message}");
                ShowScenarioStatus("Не удалось применить", isError: true);
                return;
            }

            // Step 2: Write lists/ipset-all.txt based on scope / work mode
            try
            {
                if (_selectedWorkMode == WorkModeStandardKey || _selectedWorkMode == WorkModeServicesKey)
                {
                    System.IO.File.WriteAllText(ipsetFile, "203.0.113.113/32" + Environment.NewLine, System.Text.Encoding.UTF8);
                    AppLogger.Info("ApplyScenario: Файл ipset-all.txt заполнен dummy IP (обход по спискам хостов) для стандартного/сервисного сценария.");
                }
                else // Game mode
                {
                    if (_selectedGameScope == "Только нужные адреса")
                    {
                        System.IO.File.WriteAllText(ipsetFile, "203.0.113.113/32" + Environment.NewLine, System.Text.Encoding.UTF8);
                        AppLogger.Info("ApplyScenario: Файл ipset-all.txt заполнен dummy IP (обход по спискам хостов) для игрового сценария.");
                    }
                    else if (_selectedGameScope == "Больше адресов")
                    {
                        System.IO.File.Copy(ipsetBackupFile, ipsetFile, overwrite: true);
                        AppLogger.Info("ApplyScenario: Файл ipset-all.txt успешно восстановлен из резервной копии.");
                    }
                    else if (_selectedGameScope == "Максимальный охват")
                    {
                        System.IO.File.WriteAllText(ipsetFile, string.Empty, System.Text.Encoding.UTF8);
                        AppLogger.Info("ApplyScenario: Файл ipset-all.txt очищен (максимальный охват).");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error($"ApplyScenario: Исключение при работе со списками ipset в папке {listsPath}: {ex.Message}");
                ShowScenarioStatus("Не удалось применить", isError: true);
                return;
            }

            // Step 3: Service flow
            bool wasRunning = false;
            try
            {
                var status = await Task.Run(() => _statusService.GetStatus());
                wasRunning = status.IsRunning;
            }
            catch (Exception ex)
            {
                AppLogger.Warning($"ApplyScenario: Не удалось проверить статус службы перед переустановкой: {ex.Message}");
            }

            AppLogger.Info($"ApplyScenario: Инициализация переустановки службы. Предыдущее состояние запуска: {(wasRunning ? "запущена" : "остановлена")}");

            var reinstallResult = await _serviceManager.ReinstallAsync();
            if (!reinstallResult.Success)
            {
                AppLogger.Error($"ApplyScenario: Ошибка переустановки службы SCM: {reinstallResult.Message}");
                ShowScenarioStatus("Не удалось применить", isError: true);
                return;
            }

            bool shouldStart = _selectedWorkMode == WorkModeGameKey || wasRunning;

            if (shouldStart)
            {
                AppLogger.Info("ApplyScenario: Запуск службы после успешной переустановки...");
                var startResult = await _serviceManager.StartAsync();
                
                await Task.Delay(1000); // Wait for service to actually start up
                
                bool isRunningNow = false;
                try
                {
                    var statusAfter = await Task.Run(() => _statusService.GetStatus());
                    isRunningNow = statusAfter.IsRunning;
                }
                catch { }

                if (!startResult.Success || !isRunningNow)
                {
                    AppLogger.Error($"ApplyScenario: Ошибка запуска службы SCM после переустановки. Success: {startResult.Success}, IsRunning: {isRunningNow}");
                    
                    Dispatcher.Invoke(() => {
                        ShowScenarioStatus(isTurningOn ? "Не удалось включить" : "Не удалось выключить", isError: true);
                        SetFooterMessage("Служба не запущена. Повторите попытку или перезапустите приложение.", FooterMessageKind.Error);
                    });
                    
                    return;
                }
            }

            // Success!
            SaveAppliedScenarioSettings();
            _hasPendingScenarioChanges = false;
            operationSuccessful = true;
        }
        finally
        {
            if (!operationSuccessful)
            {
                _selectedWorkMode = Settings.AppliedWorkMode;
                if (string.IsNullOrEmpty(_selectedWorkMode)) _selectedWorkMode = WorkModeStandardKey;
                Settings.SelectedWorkMode = _selectedWorkMode;
                SafeSaveSettings();
            }

            _isGameFilterOperationRunning = false;
            Dispatcher.Invoke(() => SyncGameFilterUiFromActualState());
            _ = RefreshVpnStatusAsync();
        }
    }

    // ─── Settings — UI binding ─────────────────────────────────────────────────

    private void BindSettingsToUi()
    {
        // Checkboxes — temporarily detach handlers while setting values
        AutostartBypassCheckbox.Checked   -= AutostartBypassCheckbox_Changed;
        AutostartBypassCheckbox.Unchecked -= AutostartBypassCheckbox_Changed;
        AutostartAppCheckbox.Checked      -= AutostartAppCheckbox_Changed;
        AutostartAppCheckbox.Unchecked    -= AutostartAppCheckbox_Changed;
        ShowVpnWarningCheckbox.Checked   -= ShowVpnWarningCheckbox_Changed;
        ShowVpnWarningCheckbox.Unchecked -= ShowVpnWarningCheckbox_Changed;
        AutoCheckUpdatesCheckbox.Checked   -= AutoCheckUpdatesCheckbox_Changed;
        AutoCheckUpdatesCheckbox.Unchecked -= AutoCheckUpdatesCheckbox_Changed;
        AutoUpdateZapretCheckbox.Checked   -= AutoUpdateZapretCheckbox_Changed;
        AutoUpdateZapretCheckbox.Unchecked -= AutoUpdateZapretCheckbox_Changed;
        AutoUpdateZapretKmestuCheckbox.Checked   -= AutoUpdateZapretKmestuCheckbox_Changed;
        AutoUpdateZapretKmestuCheckbox.Unchecked -= AutoUpdateZapretKmestuCheckbox_Changed;
        MinimizeToTrayCheckbox.Checked    -= MinimizeToTrayCheckbox_Changed;
        MinimizeToTrayCheckbox.Unchecked  -= MinimizeToTrayCheckbox_Changed;
        StopBypassOnAppExitCheckbox.Checked   -= StopBypassOnAppExitCheckbox_Changed;
        StopBypassOnAppExitCheckbox.Unchecked -= StopBypassOnAppExitCheckbox_Changed;
        ShowWorkModesCheckbox.Checked   -= ShowWorkModesCheckbox_Changed;
        ShowWorkModesCheckbox.Unchecked -= ShowWorkModesCheckbox_Changed;
        UseDiagnosticsCheckbox.Checked   -= UseDiagnosticsCheckbox_Changed;
        UseDiagnosticsCheckbox.Unchecked -= UseDiagnosticsCheckbox_Changed;

        // Sync GUI autostart checkbox with real shortcut state
        bool shortcutPresent = _autostartService.IsShortcutPresent();
        if (Settings.AutoStartGui != shortcutPresent)
        {
            Settings.AutoStartGui = shortcutPresent;
            SafeSaveSettings();
        }

        AutostartBypassCheckbox.IsChecked = Settings.AutoStartBypass;
        AutostartAppCheckbox.IsChecked    = Settings.AutoStartGui;
        ShowVpnWarningCheckbox.IsChecked = Settings.ShowVpnWarning;
        AutoCheckUpdatesCheckbox.IsChecked = Settings.AutoCheckUpdatesOnStartup;
        AutoUpdateZapretCheckbox.IsChecked = Settings.AutoUpdateZapret;
        AutoUpdateZapretKmestuCheckbox.IsChecked = Settings.AutoCheckKmestuOnStartup;
        MinimizeToTrayCheckbox.IsChecked = Settings.MinimizeToTrayOnClose;
        StopBypassOnAppExitCheckbox.IsChecked = Settings.StopBypassOnAppExit;
        ShowWorkModesCheckbox.IsChecked = Settings.ShowWorkModesSection;
        UseDiagnosticsCheckbox.IsChecked = Settings.UseDiagnostics;

        AutostartBypassCheckbox.Checked   += AutostartBypassCheckbox_Changed;
        AutostartBypassCheckbox.Unchecked += AutostartBypassCheckbox_Changed;
        AutostartAppCheckbox.Checked      += AutostartAppCheckbox_Changed;
        AutostartAppCheckbox.Unchecked    += AutostartAppCheckbox_Changed;
        ShowVpnWarningCheckbox.Checked   += ShowVpnWarningCheckbox_Changed;
        ShowVpnWarningCheckbox.Unchecked += ShowVpnWarningCheckbox_Changed;
        AutoCheckUpdatesCheckbox.Checked   += AutoCheckUpdatesCheckbox_Changed;
        AutoCheckUpdatesCheckbox.Unchecked += AutoCheckUpdatesCheckbox_Changed;
        AutoUpdateZapretCheckbox.Checked   += AutoUpdateZapretCheckbox_Changed;
        AutoUpdateZapretCheckbox.Unchecked += AutoUpdateZapretCheckbox_Changed;
        AutoUpdateZapretKmestuCheckbox.Checked   += AutoUpdateZapretKmestuCheckbox_Changed;
        AutoUpdateZapretKmestuCheckbox.Unchecked += AutoUpdateZapretKmestuCheckbox_Changed;
        MinimizeToTrayCheckbox.Checked    += MinimizeToTrayCheckbox_Changed;
        MinimizeToTrayCheckbox.Unchecked  += MinimizeToTrayCheckbox_Changed;
        StopBypassOnAppExitCheckbox.Checked   += StopBypassOnAppExitCheckbox_Changed;
        StopBypassOnAppExitCheckbox.Unchecked += StopBypassOnAppExitCheckbox_Changed;
        ShowWorkModesCheckbox.Checked   += ShowWorkModesCheckbox_Changed;
        ShowWorkModesCheckbox.Unchecked += ShowWorkModesCheckbox_Changed;
        UseDiagnosticsCheckbox.Checked   += UseDiagnosticsCheckbox_Changed;
        UseDiagnosticsCheckbox.Unchecked += UseDiagnosticsCheckbox_Changed;

        ApplyWorkModesVisibility();
        ApplyDiagnosticsVisibility();

        // ─── Profile Mode ───
        if (string.IsNullOrEmpty(Settings.ProfileCheckMode))
        {
            Settings.ProfileCheckMode = "Fast";
            SafeSaveSettings();
        }
        ApplyProfileCardState();
    }

    /// <summary>
    /// Forces WPF to instantiate all ControlTemplates for Settings page switch controls
    /// while the page is still hidden, so that the first user-visible frame shows
    /// the correct saved state without any flicker.
    /// Must be called after BindSettingsToUi() and while _isInitialized is false.
    /// </summary>
    private void PreWarmSettingsPageVisuals()
    {
        // PageSettings is a ScrollViewer that starts Collapsed.
        // WPF defers ControlTemplate application until an element is Visible.
        // Temporarily make it visible so all switch templates are instantiated
        // with the correct IsChecked values already set by BindSettingsToUi().
        var originalVisibility = PageSettings.Visibility;
        PageSettings.Visibility = Visibility.Visible;

        // Force the ScrollViewer and its entire subtree to apply templates and lay out.
        PageSettings.ApplyTemplate();
        PageSettings.UpdateLayout();

        // Force each settings switch CheckBox to apply its ControlTemplate so that
        // the toggle track visual state is fully rendered before the page is shown.
        // Use fully-qualified name to avoid ambiguity with System.Windows.Forms.CheckBox.
        System.Windows.Controls.CheckBox[] settingsSwitches =
        {
            AutostartAppCheckbox,
            AutostartBypassCheckbox,
            ShowWorkModesCheckbox,
            UseDiagnosticsCheckbox,
            AutoCheckUpdatesCheckbox,
            AutoUpdateZapretCheckbox,
            AutoUpdateZapretKmestuCheckbox,
            ShowVpnWarningCheckbox,
            MinimizeToTrayCheckbox,
            StopBypassOnAppExitCheckbox,
        };

        foreach (var cb in settingsSwitches)
        {
            cb.ApplyTemplate();
            cb.UpdateLayout();
        }

        // Restore original collapsed visibility — no flash visible to the user.
        PageSettings.Visibility = originalVisibility;
    }

    private void SidebarThemeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        string currentResolved = App.GetCurrentResolvedThemeName();
        string newTheme = currentResolved == "dark" ? "light" : "dark";

        Settings.Theme = newTheme;
        SafeSaveSettings();
        App.ApplyTheme(newTheme);
        AppLogger.Info($"Быстрое переключение темы: {newTheme}");

        RefreshThemeDependentUi();
    }

    private void RefreshThemeDependentUi()
    {
        // 1. Instant status refresh (colors)
        _ = RefreshVpnStatusAsync();

        // 2. Sidebar toggle UI
        UpdateSidebarThemeToggleUi();

        // 3. Update status indicator
        UpdateUpdateStatusUi();

        // 4. Update diagnostics UI
        UpdateConnectionDiagnosticSummary();

        // 5. Update native title bar
        UpdateWindowTitleBar();
    }

    private void UpdateWindowTitleBar()
    {
        string theme = App.GetCurrentResolvedThemeName();
        WindowTitleBarService.ApplyTheme(this, theme == "dark");
    }

    private void UpdateSidebarThemeToggleUi()
    {
        string currentResolved = App.GetCurrentResolvedThemeName();
        if (currentResolved == "dark")
        {
            SidebarThemeToggleIcon.Data = (System.Windows.Media.Geometry)FindResource("IconMoon");
            SidebarThemeToggleText.Text = "Тёмная тема";
        }
        else
        {
            SidebarThemeToggleIcon.Data = (System.Windows.Media.Geometry)FindResource("IconSun");
            SidebarThemeToggleText.Text = "Светлая тема";
        }
    }

    private void AutoCheckUpdatesCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        Settings.AutoCheckUpdatesOnStartup = AutoCheckUpdatesCheckbox.IsChecked == true;
        SafeSaveSettings();
        AppLogger.Info($"Настройка сохранена: Автопроверка обновлений = {Settings.AutoCheckUpdatesOnStartup}");
    }

    private void AutoUpdateZapretCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        Settings.AutoUpdateZapret = AutoUpdateZapretCheckbox.IsChecked == true;
        SafeSaveSettings();
        AppLogger.Info($"Настройка сохранена: Автообновление zapret = {Settings.AutoUpdateZapret}");
    }

    private void AutoUpdateZapretKmestuCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        Settings.AutoCheckKmestuOnStartup = AutoUpdateZapretKmestuCheckbox.IsChecked == true;
        SafeSaveSettings();
        AppLogger.Info($"Настройка сохранена: Проверка обновлений Kmestu = {Settings.AutoCheckKmestuOnStartup}");
    }

    private void GithubZapretButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/Flowseal/zapret-discord-youtube/releases") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Не удалось открыть ссылку: {ex.Message}");
        }
    }

    private void ZapretKmestuReleasesButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://github.com/kmestu/ZapretKmestu/releases") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Не удалось открыть ссылку: {ex.Message}");
        }
    }


    private void MinimizeToTrayCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        Settings.MinimizeToTrayOnClose = MinimizeToTrayCheckbox.IsChecked == true;
        SafeSaveSettings();
        AppLogger.Info($"Настройка сохранена: Сворачивать в трей при закрытии = {Settings.MinimizeToTrayOnClose}");
    }

    private void StopBypassOnAppExitCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        Settings.StopBypassOnAppExit = StopBypassOnAppExitCheckbox.IsChecked == true;
        SafeSaveSettings();
        AppLogger.Info($"Настройка сохранена: Останавливать обход при выходе = {Settings.StopBypassOnAppExit}");
    }

    private void ShowVpnWarningCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        Settings.ShowVpnWarning = ShowVpnWarningCheckbox.IsChecked == true;
        SafeSaveSettings();
        AppLogger.Info($"Настройка сохранена: Показывать предупреждение о VPN = {Settings.ShowVpnWarning}");

        // Refresh UI immediately to reflect the change
        _ = CheckStatusOnStartup();
    }

    private async void ShowWorkModesCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        bool show = ShowWorkModesCheckbox.IsChecked == true;
        Settings.ShowWorkModesSection = show;
        SafeSaveSettings();
        ApplyWorkModesVisibility();
        AppLogger.Info($"Настройка сохранена: Использовать сценарии = {show}");

        if (!show)
        {
            // Reset selected scenario to Standard when disabled
            _selectedWorkMode = WorkModeStandardKey;
            Settings.SelectedWorkMode = _selectedWorkMode;
            SafeSaveSettings();

            // Update visuals to select standard card
            UpdateWorkModeVisuals();

            // If the currently applied scenario is NOT standard, apply the Standard scenario to revert on disk/service
            if (Settings.AppliedWorkMode != WorkModeStandardKey)
            {
                AppLogger.Info("Сценарии отключены пользователем. Автоматически переключаем и применяем Стандартный сценарий для очистки флагов...");
                await ApplyScenarioSettingsAsync();
            }
            else
            {
                // Already Standard on disk/service, just ensure _hasPendingScenarioChanges is false
                _hasPendingScenarioChanges = false;
                UpdateScenarioStatusUi();
            }
        }
        else
        {
            // If turning scenarios back ON, calculate if there is a pending difference from applied scenario
            _hasPendingScenarioChanges = !IsSelectedScenarioSameAsApplied();
            UpdateScenarioStatusUi();
        }

        // Refresh footer status immediately to update the scenario text label
        _ = RefreshVpnStatusAsync();
    }

    private void ApplyWorkModesVisibility()
    {
        if (NavScenariosButton != null)
        {
            bool show = Settings.ShowWorkModesSection;
            NavScenariosButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show && PageScenarios != null && PageScenarios.Visibility == Visibility.Visible)
            {
                ShowPage("home");
            }
        }
        UpdateConnectionDiagnosticSummary();
    }

    private void ApplyDiagnosticsVisibility()
    {
        if (NavDiagButton != null)
        {
            bool show = Settings.UseDiagnostics;
            NavDiagButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show && PageDiag != null && PageDiag.Visibility == Visibility.Visible)
            {
                ShowPage("home");
            }
        }
    }

    private void UseDiagnosticsCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        Settings.UseDiagnostics = UseDiagnosticsCheckbox.IsChecked == true;
        SafeSaveSettings();
        AppLogger.Info($"Настройка 'Использовать диагностику' изменена: {Settings.UseDiagnostics}");

        ApplyDiagnosticsVisibility();

        if (!Settings.UseDiagnostics)
        {
            if (PageDiag != null && PageDiag.Visibility == Visibility.Visible)
            {
                ShowPage("home");
            }
        }
    }

    private void OpenZapretFolder_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("Запрошено открытие папки zapret.");
        OpenFolder(AppPaths.ZapretDirectory, "папка zapret");
    }

    private void OpenFolder(string path, string label)
    {
        try
        {
            if (Directory.Exists(path))
            {
                System.Diagnostics.Process.Start("explorer.exe", path);
                AppLogger.Info($"Открыта {label}: {path}");
            }
            else
            {
                AppLogger.Warning($"Не удалось открыть {label}: папка не найдена ({path})");
                SetFooterMessage("Папка не найдена", FooterMessageKind.Warning, highlight: true);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при открытии {label}: {ex.Message}");
            SetFooterMessage("Не удалось открыть папку.", FooterMessageKind.Error, highlight: true);
        }
    }


    private void AutostartBypassCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        Settings.AutoStartBypass = AutostartBypassCheckbox.IsChecked == true;
        SafeSaveSettings();
        AppLogger.Info($"Настройка сохранена: Автозапуск обхода = {Settings.AutoStartBypass}");
        // Note: real autostart wiring is a placeholder for a future stage
    }

    private void AutostartAppCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isInitialized) return;
        bool enable = AutostartAppCheckbox.IsChecked == true;

        if (_autostartService.SetAutostart(enable))
        {
            Settings.AutoStartGui = enable;
            SafeSaveSettings();
        }
        else
        {
            // Revert on failure
            SetFooterMessage("Не удалось изменить состояние автозапуска", FooterMessageKind.Error, highlight: true);

            // Re-bind to restore correct checkbox state
            BindSettingsToUi();
        }
    }

    private static string ThemeDisplayName(string theme) => theme switch
    {
        "light" => "Тема: светлая",
        "dark"  => "Тема: тёмная",
        _       => "Тема: системная"
    };

    // ─── Expert page ───────────────────────────────────────────────────────────

    private void RefreshExpertPage()
    {
        bool isInstalled = Settings.IsZapretInstalled;

        // 1. Update Install Status Badge
        if (isInstalled)
        {
            ExpertInstallStatusText.Text = "Установлено";
            ExpertInstallStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
            ExpertStatusBadge.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
        }
        else
        {
            ExpertInstallStatusText.Text = "Не установлен";
            ExpertInstallStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            ExpertStatusBadge.SetResourceReference(Border.BackgroundProperty, "BorderSoftBrush");
        }

        // 2. Update Version summary
        ExpertVersionSummaryText.Text = string.IsNullOrWhiteSpace(Settings.InstalledZapretVersion)
            ? (Settings.IsZapretInstalled ? "Версия не определена · актуальность не проверена" : "zapret не установлен")
            : $"zapret {Settings.InstalledZapretVersion}";

        // 3. Discover profiles
        if (isInstalled)
        {
            var profiles = _profileService.GetAvailableProfiles()
                .OrderBy(p => GetProfileSortKey(p.FileName).family)
                .ThenBy(p => GetProfileSortKey(p.FileName).altNum)
                .ThenBy(p => GetProfileSortKey(p.FileName).name)
                .ToList();

            if (profiles.Count > 0)
            {
                AppLogger.Info($"Найдено профилей zapret: {profiles.Count}");

                ExpertSummaryDot.Visibility = Visibility.Visible;
                ExpertCountSummaryText.Text = $"найдено профилей: {profiles.Count}";
                ExpertCountSummaryText.Visibility = Visibility.Visible;

                ProfileComboBox.SelectionChanged -= ProfileComboBox_SelectionChanged;
                HomeProfileComboBox.SelectionChanged -= HomeProfileComboBox_SelectionChanged;

                ProfileComboBox.ItemsSource = profiles;
                HomeProfileComboBox.ItemsSource = profiles;

                // Try to select saved profile
                var saved = profiles.FirstOrDefault(p => p.FileName == Settings.SelectedProfile);
                var selected = saved ?? profiles.FirstOrDefault();

                ProfileComboBox.SelectedItem = selected;
                HomeProfileComboBox.SelectedItem = selected;

                ProfileComboBox.SelectionChanged += ProfileComboBox_SelectionChanged;
                HomeProfileComboBox.SelectionChanged += HomeProfileComboBox_SelectionChanged;

                if (selected != null)
                {
                    ExpertSummaryText.Text = $"Текущий профиль: {selected.DisplayName}";
                    ProfileText.Text = $"zapret {Settings.InstalledZapretVersion ?? "???"}";
                    _lastAppliedProfile = selected.FileName;
                    ParseSelectedProfile(selected.FullPath);
                }
                else
                {
                    ExpertSummaryText.Text = "Текущий профиль: не выбран";
                }

                ProfileComboBox.Visibility = Visibility.Visible;
                HomeProfileComboBox.Visibility = Visibility.Visible;
                ExpertProfileText.Visibility = Visibility.Collapsed;
                ReinstallServiceButton.IsEnabled = _adminService.IsRunningAsAdministrator();
            }
            else
            {
                ProfileComboBox.Visibility = Visibility.Collapsed;
                HomeProfileComboBox.Visibility = Visibility.Collapsed;
                ExpertProfileText.Visibility = Visibility.Visible;
                ExpertProfileText.Text = "Профили не найдены. Установите zapret.";
                ProfileParseStatusText.Visibility = Visibility.Collapsed;

                ExpertSummaryText.Text = "Профили не найдены";
                ExpertSummaryDot.Visibility = Visibility.Collapsed;
                ExpertCountSummaryText.Visibility = Visibility.Collapsed;
                ReinstallServiceButton.IsEnabled = false;
            }
        }
        else
        {
            ProfileComboBox.Visibility = Visibility.Collapsed;
            HomeProfileComboBox.Visibility = Visibility.Collapsed;
            ExpertProfileText.Visibility = Visibility.Visible;
            ExpertProfileText.Text = "Сначала установите zapret";
            ProfileParseStatusText.Visibility = Visibility.Collapsed;

            ExpertSummaryText.Text = "Текущий профиль: не выбран";
            ExpertSummaryDot.Visibility = Visibility.Collapsed;
            ExpertCountSummaryText.Visibility = Visibility.Collapsed;
            ReinstallServiceButton.IsEnabled = false;
        }
    }

    private void RefreshProfilesButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("Запрошено ручное обновление списка профилей.");
        RefreshExpertPage();
        SetFooterMessage("Список профилей обновлён", FooterMessageKind.Info, highlight: true);
    }

    private async void ProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _isSyncingProfiles || _isApplyingProfile) return;
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked ProfileComboBox_SelectionChanged while auto-pick is active.");
            _isSyncingProfiles = true;
            try
            {
                string? targetProfile = !string.IsNullOrEmpty(_originalProfileBeforeWizard)
                    ? _originalProfileBeforeWizard
                    : _lastAppliedProfile;
                ProfileComboBox.SelectedItem = ProfileComboBox.Items.Cast<ZapretProfileInfo>().FirstOrDefault(p => p.FileName == targetProfile);
            }
            finally
            {
                _isSyncingProfiles = false;
            }
            return;
        }
        if (!_isInitialized || _isSyncingProfiles || _isApplyingProfile) return;
        if (ProfileComboBox.SelectedItem is ZapretProfileInfo profile)
        {
            if (profile.FileName == _lastAppliedProfile) return;
            await ApplyProfileCoreAsync(profile, "Эксперт");
        }
    }

    private async void HomeProfileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isInitialized || _isSyncingProfiles || _isApplyingProfile) return;
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked HomeProfileComboBox_SelectionChanged while auto-pick is active.");
            _isSyncingProfiles = true;
            try
            {
                string? targetProfile = !string.IsNullOrEmpty(_originalProfileBeforeWizard)
                    ? _originalProfileBeforeWizard
                    : _lastAppliedProfile;
                HomeProfileComboBox.SelectedItem = HomeProfileComboBox.Items.Cast<ZapretProfileInfo>().FirstOrDefault(p => p.FileName == targetProfile);
            }
            finally
            {
                _isSyncingProfiles = false;
            }
            return;
        }
        if (!_isInitialized || _isSyncingProfiles || _isApplyingProfile) return;
        if (HomeProfileComboBox.SelectedItem is ZapretProfileInfo profile)
        {
            if (profile.FileName == _lastAppliedProfile) return;
            await ApplyProfileCoreAsync(profile, "Главная");
        }
    }

    private async Task ApplyProfileCoreAsync(ZapretProfileInfo profile, string sourceName)
    {
        if (profile == null || string.IsNullOrWhiteSpace(profile.FileName) || string.IsNullOrWhiteSpace(profile.FullPath))
        {
            AppLogger.Error($"{sourceName}: попытка применить пустой профиль отклонена.");
            throw new ArgumentException("Недопустимый профиль.");
        }

        if (profile.FileName.Equals("service.bat", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Error($"{sourceName}: попытка применить service.bat отклонена.");
            throw new InvalidOperationException("Выбор service.bat запрещён.");
        }

        var availableProfiles = _profileService.GetAvailableProfiles();
        var match = availableProfiles.FirstOrDefault(p => p.FileName.Equals(profile.FileName, StringComparison.OrdinalIgnoreCase));
        if (match == null)
        {
            AppLogger.Error($"{sourceName}: профиль {profile.FileName} не найден на диске.");
            throw new FileNotFoundException("Профиль не найден.");
        }

        _isApplyingProfile = true;

        string previousSelectedProfile = Settings.SelectedProfile;
        string? previousLastAppliedProfile = _lastAppliedProfile;

        // Verify service state before touching anything
        var status = await Task.Run(() => _statusService.GetStatus());
        bool wasRunning = status.IsRunning;

        try
        {
            AppLogger.Info($"{sourceName}: смена профиля на {match.DisplayName}. Запуск применения...");

            // 1. In memory only for ReinstallAsync
            Settings.SelectedProfile = match.FileName;

            // 2. Perform actions
            await _serviceManager.ReinstallAsync();
            if (wasRunning)
            {
                await _serviceManager.StartAsync();
            }

            // 3. Success -> apply UI and persistence
            _lastAppliedProfile = match.FileName;
            SafeSaveSettings();

            // Synchronize combo boxes without triggering recursion
            _isSyncingProfiles = true;
            try
            {
                if (sourceName == "Эксперт" || sourceName == "Сравнение") HomeProfileComboBox.SelectedItem = match;
                if (sourceName == "Главная" || sourceName == "Сравнение") ProfileComboBox.SelectedItem = match;
            }
            finally
            {
                _isSyncingProfiles = false;
            }

            ExpertSummaryText.Text = $"Текущий профиль: {match.DisplayName}";
            SetFooterMessage($"Профиль {match.DisplayName} применён", FooterMessageKind.Success, highlight: true);

            ParseSelectedProfile(match.FullPath);
            UpdateWorkModeVisuals();
            UpdateGameSettingsVisuals();
            UpdateScenarioStatusUi();

            _ = ExecuteNetworkDiagnosticAsync(false);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при смене профиля: {ex.GetType().Name} - {ex.Message}");
            // Rollback in memory
            // Rollback in memory
            Settings.SelectedProfile = previousSelectedProfile;
            _lastAppliedProfile = previousLastAppliedProfile;

            // Sync combos back to previous state
            _isSyncingProfiles = true;
            try
            {
                var previousProfileInfo = availableProfiles.FirstOrDefault(p => p.FileName.Equals(previousSelectedProfile, StringComparison.OrdinalIgnoreCase));
                HomeProfileComboBox.SelectedItem = previousProfileInfo;
                ProfileComboBox.SelectedItem = previousProfileInfo;
            }
            finally
            {
                _isSyncingProfiles = false;
            }

            // Optional: try to safely rollback the service to the previous profile
            if (!string.IsNullOrEmpty(previousSelectedProfile))
            {
                try
                {
                    AppLogger.Info($"Откат службы на предыдущий профиль: {previousSelectedProfile}");
                    await _serviceManager.ReinstallAsync();
                    if (wasRunning)
                    {
                        await _serviceManager.StartAsync();
                    }
                }
                catch (Exception rollbackEx)
                {
                    AppLogger.Error($"Ошибка при откате службы: {rollbackEx.Message}");
                }
            }

            // Do NOT throw to caller if we don't want caller to crash, but the caller handles it?
            // "If applying the target fails ... 8. Log the exact exception type and message. 9. Do not call ClearOverlayState."
            // We should re-throw so the caller sets button to Error without calling ClearOverlayState.
            throw;
        }
        finally
        {
            _isApplyingProfile = false;
            UpdateWindowTitleBar();
        }
    }

    private void SyncProfileComboBoxes(string targetProfileName)
    {
        _isSyncingProfiles = true;
        try
        {
            var target = _profileService.GetAvailableProfiles().FirstOrDefault(p => p.FileName.Equals(targetProfileName, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                HomeProfileComboBox.SelectedItem = target;
                ProfileComboBox.SelectedItem = target;
                _lastAppliedProfile = target.FileName;
            }
        }
        finally
        {
            _isSyncingProfiles = false;
        }
    }

    private void ParseSelectedProfile(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        var info = _commandParser.ParseProfile(path);

        if (info.Success)
        {
            AppLogger.Info($"Профиль успешно прочитан: {System.IO.Path.GetFileName(path)}");
            ProfileParseStatusText.Text = "Профиль готов к применению.";
            ProfileParseStatusText.Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush");
        }
        else
        {
            AppLogger.Warning($"Не удалось прочитать команду профиля: {info.ErrorMessage}");
            ProfileParseStatusText.Text = $"Профиль не удалось прочитать: {info.ErrorMessage}";
            ProfileParseStatusText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
        }

        ProfileParseStatusText.Visibility = Visibility.Visible;
    }

    // ─── Journal ──────────────────────────────────────────────────────────────

    private List<LogEntryDisplay> GetSanitizedJournalEntries(IEnumerable<string> allEntries)
    {
        var tempList = new List<LogEntryDisplay>();
        foreach (var rawEntry in allEntries)
        {
            var parsed = ParseLogEntry(rawEntry);
            if (parsed == null) continue;

            if (parsed.Severity == "INFO" && IsTechnicalNoise(parsed.Message))
                continue;

            tempList.Add(parsed);
        }

        bool hasAdminStateStartup = tempList.Any(e => 
            e.Message == "Приложение запущено с правами администратора." || 
            e.Message == "Запущено без прав администратора.");

        var displayList = new List<LogEntryDisplay>();
        var seenKeys = new HashSet<string>();

        foreach (var entry in tempList)
        {
            // Prefer admin-specific startup message over generic one
            if (hasAdminStateStartup && entry.Message == "Приложение запущено.")
                continue;

            // Deduplicate exact same Message + Severity (keep newest first, which is the first encounter in tempList)
            string key = $"{entry.Message}|||{entry.Severity}";
            if (seenKeys.Contains(key))
                continue;

            seenKeys.Add(key);
            displayList.Add(entry);

            if (displayList.Count >= 50) break;
        }

        if (displayList.Count == 0)
        {
            displayList.Add(new LogEntryDisplay
            {
                Time = "--:--",
                Severity = "INFO",
                Message = "Пока нет важных событий."
            });
        }

        return displayList;
    }

    private void RefreshJournal()
    {
        var allEntries = AppLogger.GetRecentEntries();
        int totalErrorCount = allEntries.Count(e => e.Contains("[ERR ]"));

        var displayList = GetSanitizedJournalEntries(allEntries);

        LogItems.ItemsSource = displayList;

        // Update Summary Info
        LogFileNameText.Text = "в приложении";
        LogEntryCountText.Text = (displayList.Count == 1 && displayList[0].Message == "Пока нет важных событий.") ? "0" : displayList.Count.ToString();
        LogErrorCountText.Text = totalErrorCount.ToString();

        // Dynamic coloring for error count
        if (totalErrorCount > 0)
            LogErrorCountText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        else
            LogErrorCountText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

        if (allEntries.Count > 0)
        {
            // Extract HH:mm from "[yyyy-MM-dd HH:mm:ss] [LEVEL] Message"
            string latest = allEntries[0];
            int spaceIdx = latest.IndexOf(' ');
            int closeBracketIdx = latest.IndexOf(']', spaceIdx);

            if (spaceIdx != -1 && closeBracketIdx != -1)
            {
                string timePart = latest.Substring(spaceIdx + 1, closeBracketIdx - spaceIdx - 1);
                if (timePart.Length >= 5)
                    LogLastUpdateText.Text = timePart.Substring(0, 5);
                else
                    LogLastUpdateText.Text = DateTime.Now.ToString("HH:mm");
            }
            else
            {
                LogLastUpdateText.Text = DateTime.Now.ToString("HH:mm");
            }
        }
        else
        {
            LogLastUpdateText.Text = "--:--";
        }

        LogScrollViewer.ScrollToHome();
    }

    private LogEntryDisplay? ParseLogEntry(string entry)
    {
        // Format: [yyyy-MM-dd HH:mm:ss] [LEVEL] Message
        if (entry.Length > 21 && entry.StartsWith("[") && entry[20] == ']')
        {
            string timePart = entry.Substring(12, 5); // "HH:mm"
            string rest = entry.Substring(21).Trim(); // "[LEVEL] Message"

            string severity = "INFO";
            string message = rest;

            if (rest.StartsWith("[") && rest.Contains("]"))
            {
                int closeBracket = rest.IndexOf(']');
                severity = rest.Substring(1, closeBracket - 1).Trim();
                if (severity == "ERR" || severity == "ERROR") severity = "ERROR";
                message = rest.Substring(closeBracket + 1).Trim();
            }

            // Normalization & translation mapping:
            if (message.Contains("=== Zapret Kmestu запущен ==="))
            {
                message = "Приложение запущено.";
                severity = "INFO";
            }
            else if (message.Contains("Приложение запущено с правами администратора."))
            {
                message = "Приложение запущено с правами администратора.";
                severity = "INFO";
            }
            else if (message.Contains("Запущено без прав администратора."))
            {
                message = "Запущено без прав администратора.";
                severity = "WARN";
            }
            else if (message.Contains("Приложение завершено."))
            {
                message = "Приложение завершено.";
                severity = "INFO";
            }
            else if (message.Contains("Результат проверки обновлений: актуальная версия") ||
                     message.Contains("Результат: актуальная версия") ||
                     message.Contains("Установлена актуальная версия zapret."))
            {
                message = "Установлена актуальная версия zapret.";
                severity = "SUCCESS";
            }
            else if (message.Contains("доступна новая версия") || 
                     message.Contains("Результат: доступно обновление"))
            {
                message = "Доступно обновление zapret.";
                severity = "WARN";
            }
            else if (message.Contains("Запущена установка zapret через интерфейс.") ||
                     message.Contains("Установка zapret началась."))
            {
                message = "Установка zapret началась.";
                severity = "INFO";
            }
            else if (message.Contains("Установка zapret завершена."))
            {
                message = "Установка zapret завершена.";
                severity = "SUCCESS";
            }
            else if (message.Contains("Установка zapret отменена."))
            {
                message = "Установка zapret отменена.";
                severity = "INFO";
            }
            else if (message.Contains("Запрошена отмена мастера подбора профиля.") ||
                     message.Contains("Автоподбор отменён."))
            {
                message = "Автоподбор отменён.";
                severity = "INFO";
            }
            else if (message.Contains("Запрошена отмена операции с файлами zapret.") ||
                     message.Contains("Операция отменена."))
            {
                message = "Операция отменена.";
                severity = "INFO";
            }
            else if (message.StartsWith("Главная: смена профиля на ") || message.StartsWith("Эксперт: смена профиля на "))
            {
                string display = message.Replace("Главная: смена профиля на ", "")
                                        .Replace("Эксперт: смена профиля на ", "")
                                        .Replace(". Применяем немедленно.", "")
                                        .Trim();
                message = $"Профиль изменён: {display}.";
                severity = "SUCCESS";
            }
            else if (message.StartsWith("Профиль ") && message.EndsWith(" применён"))
            {
                string profile = message.Substring(8, message.Length - 8 - 9).Trim();
                message = $"Профиль применён: {profile}.";
                severity = "SUCCESS";
            }
            else if (message.Contains("Служба zapret успешно запущена.") || message.Contains("Обход включён."))
            {
                message = "Обход включён.";
                severity = "SUCCESS";
            }
            else if (message.Contains("Запуск службы zapret"))
            {
                message = "Обход запускается...";
                severity = "INFO";
            }
            else if (message.Contains("Остановка службы zapret") || message.Contains("Обход выключается..."))
            {
                message = "Обход выключается...";
                severity = "INFO";
            }
            else if (message.Contains("Обход остановлен.") || message.Contains("Обход выключен.") || message.Contains("Служба zapret остановлена."))
            {
                message = "Обход выключен.";
                severity = "INFO";
            }
            else if (message.Contains("VPN: возможно включён") || message.Contains("VPN: обнаружено активное"))
            {
                message = "Обнаружен активный VPN.";
                severity = "WARN";
            }
            else if (message.Contains("VPN: явных признаков не найдено"))
            {
                return null;
            }
            else if (message.StartsWith("Last auto-pick results loaded. Profiles:"))
            {
                string sub = message.Substring("Last auto-pick results loaded. Profiles:".Length).Trim();
                int commaIdx = sub.IndexOf(',');
                string countStr = commaIdx != -1 ? sub.Substring(0, commaIdx).Trim() : sub;
                message = $"Загружен последний подбор: {countStr} профилей.";
                severity = "INFO";
            }
            else if (message.Contains("Автоподбор запущен."))
            {
                message = "Автоподбор запущен.";
                severity = "INFO";
            }
            else if (message.StartsWith("Проверяется профиль:"))
            {
                severity = "INFO";
            }
            else if (message.Contains("Автоподбор завершён."))
            {
                message = "Автоподбор завершён.";
                severity = "SUCCESS";
            }
            else if (message.StartsWith("Лучший профиль:"))
            {
                severity = "SUCCESS";
            }

            return new LogEntryDisplay
            {
                Time = timePart,
                Severity = severity,
                Message = message
            };
        }
        return null;
    }

    private bool IsTechnicalNoise(string message)
    {
        if (message.Contains("ProgramData", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("AppData", StringComparison.OrdinalIgnoreCase) ||
            message.Contains(":\\") ||
            message.Contains(":/"))
        {
            return true;
        }

        // Check for version log: "Версия: ... | .NET ... | Admin: ..."
        if (message.Contains("Версия:", StringComparison.OrdinalIgnoreCase) &&
            message.Contains(".NET", StringComparison.OrdinalIgnoreCase) &&
            message.Contains("Admin:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] noisePatterns = {
            "Tray icon changed",
            "TCP Timestamps",
            "Статус службы zapret",
            "Сравнение версий",
            "Инициализация обновления",
            "Проверка обновлений завершена",
            "Последняя версия Flowseal",
            "Установленная версия",
            "=== Проверка обновлений zapret ===",
            "Автопроверка обновлений при запуске включена.",
            "Тема изменена:",
            "Тема применена:",
            "Быстрое переключение темы:",
            "Подготовка окружения",
            "Служба WinDivert остановлена",
            "Журнал:",
            "Файл журнала",
            "Настройки загружены из",
            "Папка проверена",
            "Инициализация завершена",
            "Локальная установка zapret проверена",
            "Найдено профилей zapret:",
            "Профиль успешно прочитан",
            "Открыта страница:",
            "Настройка сохранена:",
            "Запрошено открытие папки",
            "Открыта Папка",
            "Проверка обновлений уже выполняется",
            "Отчёт скопирован в буфер обмена",
            "Настройки загружены успешно",
            "Settings loaded",
            "Logger initialized",
            "Запрошено включение обхода (запуск службы zapret)",
            "Выполнение автоматического запуска обхода...",
            "Обход запускается..."
        };

        foreach (var pattern in noisePatterns)
        {
            if (message.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Hide successful HTTPS checks ("успешные проверки HTTPS-отклика"):
        if ((message.Contains("YouTube: Доступен", StringComparison.OrdinalIgnoreCase) ||
             message.Contains("Discord: Доступен", StringComparison.OrdinalIgnoreCase)) &&
            !message.Contains("недоступен", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("ошибка", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public class LogEntryDisplay
    {
        public string Time { get; set; } = "";
        public string Message { get; set; } = "";
        public string Severity { get; set; } = "INFO";
    }

    private void RefreshJournalButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshJournal();
        SetFooterMessage("Журнал обновлён", FooterMessageKind.Info, highlight: true);
    }

    // ─── Главная — Toggle ─────────────────────────────────────────────────────

    private async void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_isWizardRunning)
            {
                if (_wizardCts != null && !_wizardCts.IsCancellationRequested)
                {
                    AppLogger.Info("Запрошена отмена автоподбора через главную кнопку.");
                    CancelCurrentOperation();
                    ToggleButtonText.Text = "ОТМЕНА...";
                    ToggleButton.IsEnabled = false;
                }
                else
                {
                    AppLogger.Info("Blocked bypass toggle while auto-pick is active or cancelling.");
                }
                return;
            }
            if (_installCts != null)
            {
                CancelCurrentOperation();
                return;
            }

            if (!Settings.IsZapretInstalled)
            {
                // Fix: Hero button now starts installation instead of showing an alert
                InstallZapretButton_Click(sender, e);
                return;
            }

            // 1. Admin check
            if (!EnsureAdmin())
            {
                return;
            }

            // 2. Get current status to decide action
            var status = await Task.Run(() => _statusService.GetStatus());

            if (!status.Exists)
            {
                if (status.ErrorMessage != null)
                {
                    AppLogger.Warning($"Ошибка при получении статуса перед переключением: {status.ErrorMessage}");
                    ShowOverlay(
                        "Не удалось проверить службу",
                        "Приложение не смогло получить статус службы zapret. Попробуйте нажать «Починить всё».",
                        "Понятно",
                        "",
                        () => { },
                        closeBehavesAsPrimary: true
                    );
                    await CheckStatusOnStartup();
                    return;
                }

                AppLogger.Warning("Попытка запуска, но служба zapret отсутствует в Windows.");
                SetFooterMessage("Служба не установлена. Нажмите «Починить всё»", FooterMessageKind.Warning, highlight: true);
                return;
            }

            bool turningOn = !status.IsRunning;
            ToggleButton.IsEnabled = false;

            try
            {
                ZapretServiceActionResult result;
                if (turningOn)
                {
                    result = await _serviceManager.StartAsync();
                }
                else
                {
                    result = await _serviceManager.StopAsync();
                }

                if (result.Success && turningOn)
                {
                    _ = ExecuteNetworkDiagnosticAsync(false);
                }

                if (!result.Success)
                {
                    ShowOverlay(
                        "Не удалось изменить состояние обхода",
                        "Обход не удалось включить или выключить. Попробуйте нажать «Починить всё».",
                        "Понятно",
                        "",
                        () => { },
                        closeBehavesAsPrimary: true
                    );
                }

                // Refresh status
                await CheckStatusOnStartup();
            }
            finally
            {
                ToggleButton.IsEnabled = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Критическая ошибка в ToggleButton_Click: {ex.Message}");
            ToggleButton.IsEnabled = true;
            SetFooterMessage("Не удалось выполнить восстановление.", FooterMessageKind.Error, highlight: true);
        }
    }

    private void CancelOperationButton_Click(object sender, RoutedEventArgs e)
    {
        CancelCurrentOperation();
    }

    private void CancelCurrentOperation()
    {
        if (_isWizardRunning)
        {
            _wizardCts?.Cancel();
            AppLogger.Info("Запрошена отмена мастера подбора профиля.");
        }
        else if (_installCts != null)
        {
            _installCts.Cancel();
            AppLogger.Info("Запрошена отмена операции с файлами zapret.");
        }

        SetFooterMessage("Операция отменена", FooterMessageKind.Info, highlight: true);
    }

    // ─── Главная — Install zapret ─────────────────────────────────────────────

    private bool TryGetLocalZapretVersion(out string version)
    {
        version = string.Empty;
        try
        {
            string serviceBatPath = System.IO.Path.Combine(AppPaths.ZapretDirectory, "service.bat");
            if (!System.IO.File.Exists(serviceBatPath))
            {
                AppLogger.Warning("TryGetLocalZapretVersion: service.bat не найден.");
                return false;
            }

            foreach (var line in System.IO.File.ReadLines(serviceBatPath))
            {
                string trimmed = line.Trim();
                if (trimmed.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
                {
                    string varPart = trimmed.Substring(4).TrimStart('"');
                    if (varPart.StartsWith("LOCAL_VERSION=", StringComparison.OrdinalIgnoreCase))
                    {
                        string val = varPart.Substring(14).TrimEnd('"');
                        if (!string.IsNullOrWhiteSpace(val))
                        {
                            version = val.Trim();
                            AppLogger.Info($"TryGetLocalZapretVersion: обнаружена версия {version}");
                            return true;
                        }
                    }
                }
            }

            AppLogger.Warning("TryGetLocalZapretVersion: маркер LOCAL_VERSION не найден в service.bat.");
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"TryGetLocalZapretVersion: ошибка при чтении версии: {ex.Message}");
            return false;
        }
    }

    private void ReconcileZapretState()
    {
        bool serviceExists = false;
        try
        {
            var status = _statusService.GetStatus();
            serviceExists = status.Exists;
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Reconcile: Ошибка получения статуса службы: {ex.Message}");
        }

        bool localValid = false;
        try
        {
            localValid = _installer.ValidateLocalInstall();
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Reconcile: Ошибка проверки локальных файлов: {ex.Message}");
        }

        bool needsSave = false;

        // State A: valid local files and Windows service exists -> IsZapretInstalled = true
        if (localValid && serviceExists)
        {
            if (!Settings.IsZapretInstalled)
            {
                Settings.IsZapretInstalled = true;
                needsSave = true;
            }

            if (string.IsNullOrWhiteSpace(Settings.InstalledZapretVersion))
            {
                if (TryGetLocalZapretVersion(out string version))
                {
                    Settings.InstalledZapretVersion = version;
                    needsSave = true;
                }
            }
        }
        else
        {
            if (Settings.IsZapretInstalled)
            {
                Settings.IsZapretInstalled = false;
                needsSave = true;
            }

            if (serviceExists && !localValid)
            {
                AppLogger.Warning("Reconcile: Несогласованное состояние - служба существует, но локальные файлы отсутствуют или повреждены.");
            }
        }

        if (needsSave)
        {
            if (!SafeSaveSettings())
            {
                AppLogger.Warning("Reconcile: Не удалось сохранить обновленный статус установки.");
            }
        }
    }

    private ZapretProfileInfo? ResolveDefaultProfile()
    {
        var profiles = _profileService.GetAvailableProfiles()
            .OrderBy(p => GetProfileSortKey(p.FileName).family)
            .ThenBy(p => GetProfileSortKey(p.FileName).altNum)
            .ThenBy(p => GetProfileSortKey(p.FileName).name)
            .ToList();
        if (profiles.Count == 0) return null;

        return profiles.FirstOrDefault(p => !string.IsNullOrEmpty(Settings.SelectedProfile) && p.FileName.Equals(Settings.SelectedProfile, StringComparison.OrdinalIgnoreCase))
               ?? profiles.FirstOrDefault(p => p.FileName.Equals("general (ALT11).bat", StringComparison.OrdinalIgnoreCase))
               ?? profiles.FirstOrDefault();
    }

    private async Task RunZapretInstallOrUpdateAsync()
    {
        if (_installCts != null) return; // Already running

        if (!EnsureAdmin())
        {
            return;
        }


        _installCts = new CancellationTokenSource();
        _hasInstallError = false;
        _hasUpdateCheckError = false; // Clear check-error state when install starts
        InstallZapretButton.IsEnabled = false;
        InstallProgressPanel.Visibility = Visibility.Visible;
        CancelOperationButton.Visibility = Visibility.Visible;
        InstallBottomStatusText.Visibility = Visibility.Hidden;
        InstallProgressBar.Value = 0;
        InstallProgressBar.IsIndeterminate = true;
        InstallStepText.Text = "Инициализация...";
        InstallPercentText.Text = "0%";

        AppLogger.Info("Запущена установка zapret через интерфейс.");

        // Refresh Hero UI to show installing state
        _ = CheckStatusOnStartup();
        UpdateUpdateStatusUi();
        ApplyProfileCardState();

        bool wasRunning = false;
        try
        {
            // 0. Check if this is an Update
            bool isUpdate = _isUpdateAvailable && _latestRelease != null;
            if (isUpdate)
            {
                AppLogger.Info($"Запущено обновление до {_latestRelease!.TagName} без подтверждения.");
            }

            var progress = new Progress<InstallProgressInfo>(info =>
            {
                if (!string.IsNullOrEmpty(info.Step))
                    InstallStepText.Text = info.Step;

                if (info.Percent.HasValue)
                {
                    InstallProgressBar.IsIndeterminate = false;
                    InstallProgressBar.Value = info.Percent.Value;
                    InstallPercentText.Text = $"{info.Percent.Value}%";
                }
                else
                {
                    InstallProgressBar.IsIndeterminate = true;
                    InstallPercentText.Text = "";
                }
            });

            // Part 3 — Safe cleanup before file replacement
            IProgress<InstallProgressInfo> progressReporter = progress;
            progressReporter.Report(new InstallProgressInfo { Step = "Подготовка..." });

            // 1. Stop zapret service if it exists
            var status = await Task.Run(() => _statusService.GetStatus());
            if (status.Exists && status.IsRunning)
            {
                wasRunning = true;
                progressReporter.Report(new InstallProgressInfo { Step = "Остановка службы zapret..." });
                await _serviceManager.StopAsync();
                AppLogger.Info("Служба zapret остановлена перед заменой файлов.");
            }

            // 2. Kill winws.exe and clean up WinDivert
            progressReporter.Report(new InstallProgressInfo { Step = "Очистка процессов..." });
            await _serviceManager.PrepareFlowsealLikeEnvironmentAsync(isUpdate ? "обновление" : "переустановка");

            // 3. Brief wait for file handles to be released
            await Task.Delay(1000, _installCts.Token);
            AppLogger.Info("Файлы готовы к замене.");

            // 4. Local files preflight and GitHub fallback logic
            progressReporter.Report(new InstallProgressInfo { Step = "Проверяем локальные файлы zapret..." });
            bool localFilesValid = await Task.Run(() => _installer.ValidateLocalInstall());
            AppLogger.Info($"Валидность локальных файлов: {localFilesValid}");

            ZapretInstallResult? bundledResult = null;
            bool requiresRemoteCheck = true;

            if (!localFilesValid)
            {
                string bundledArchivePath = Path.Combine(AppContext.BaseDirectory, "Engine", BundledArchiveFileName);
                bool bundledFileExists = File.Exists(bundledArchivePath);
                bool targetDirEmptyOrAbsent = !Directory.Exists(AppPaths.ZapretDirectory) || !Directory.EnumerateFileSystemEntries(AppPaths.ZapretDirectory).Any();

                if (!isUpdate && !Settings.IsZapretInstalled && !status.Exists && bundledFileExists && targetDirEmptyOrAbsent)
                {
                    progressReporter.Report(new InstallProgressInfo { Step = "Устанавливаем zapret из файлов приложения..." });
                    try
                    {
                        var extractResult = await _extractionService.ExtractAndInstallAsync(bundledArchivePath, progressReporter, _installCts!.Token);
                        if (!extractResult.IsValid)
                        {
                            bundledResult = ZapretInstallResult.Failed("Не удалось извлечь встроенные файлы zapret.");
                        }
                        else
                        {
                            Settings.IsZapretInstalled = true;
                            Settings.InstalledZapretVersion = BundledArchiveExpectedVersion;
                            Settings.ZapretPath = AppPaths.ZapretDirectory;
                            SettingsService.Save(Settings, AppPaths.SettingsFilePath);
                            bundledResult = ZapretInstallResult.Successful(BundledArchiveExpectedVersion, AppPaths.ZapretDirectory);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        bundledResult = ZapretInstallResult.Failed("Операция отменена.");
                    }
                    requiresRemoteCheck = false;
                }
            }

            GitHubReleaseInfo? remoteRelease = null;
            bool githubUnavailable = false;

            if (requiresRemoteCheck)
            {
                progressReporter.Report(new InstallProgressInfo { Step = "Проверяем актуальную версию..." });
                try
                {
                    remoteRelease = await _releaseService.GetLatestReleaseAsync(_installCts.Token);
                }
                catch (OperationCanceledException) when (_installCts.Token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is InvalidOperationException)
                {
                    githubUnavailable = true;
                    AppLogger.Error($"Ошибка при проверке GitHub: {ex.Message}");
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Неожиданная ошибка при проверке GitHub: {ex.Message}");
                    throw;
                }
            }

            ZapretInstallResult result;

            if (bundledResult != null)
            {
                result = bundledResult;
            }
            // Branch A
            else if (!localFilesValid)
            {
                if (githubUnavailable)
                {
                    result = ZapretInstallResult.Failed("Не удалось получить файлы zapret. GitHub временно недоступен");
                }
                else
                {
                    AppLogger.Info("Локальные файлы невалидны. Скачиваем с GitHub...");
                    result = await _installer.InstallAsync(progress, _installCts.Token);
                }
            }
            // Branch B
            else if (remoteRelease != null)
            {
                bool localVersionIsTrustworthy = Settings.IsZapretInstalled && !string.IsNullOrWhiteSpace(Settings.InstalledZapretVersion);
                string localVersion = localVersionIsTrustworthy ? Settings.InstalledZapretVersion : string.Empty;

                if (localVersionIsTrustworthy && !IsNewerVersion(localVersion, remoteRelease.TagName))
                {
                    AppLogger.Info("Локальная версия актуальна или новее. Оставляем локальные файлы.");
                    result = await AdoptValidatedLocalZapretAsync(progressReporter);
                }
                else
                {
                    AppLogger.Info(!localVersionIsTrustworthy
                        ? "Локальная версия неизвестна или недостоверна. Скачиваем с GitHub..."
                        : "Найдена более новая версия. Скачиваем с GitHub...");

                    var remoteResult = await _installer.InstallAsync(progress, _installCts.Token);
                    if (!remoteResult.Success)
                    {
                        AppLogger.Warning("Установка с GitHub не удалась. Откат к локальным файлам.");
                        result = await AdoptValidatedLocalZapretAsync(progressReporter);
                    }
                    else
                    {
                        result = remoteResult;
                    }
                }
            }
            // Branch C
            else
            {
                AppLogger.Info("GitHub временно недоступен — используются локальные файлы");
                result = await AdoptValidatedLocalZapretAsync(progressReporter);
            }

            if (result.Success)
            {
                AppLogger.Info($"Файлы успешно установлены: {result.Version}");

                // 4.5. Auto-select profile
                var defaultProfile = ResolveDefaultProfile();
                ZapretServiceActionResult svcResult;

                if (defaultProfile == null)
                {
                    AppLogger.Warning("Нет доступных профилей. Пропуск переустановки службы.");
                    svcResult = ZapretServiceActionResult.Error("Не найдено ни одного профиля general*.bat.");
                }
                else
                {
                    if (Settings.SelectedProfile != defaultProfile.FileName)
                    {
                        Settings.SelectedProfile = defaultProfile.FileName;
                        SafeSaveSettings();
                        AppLogger.Info($"Автоматически выбран профиль: {defaultProfile.FileName}");
                    }

                    // 5. Reinstall service to ensure binPath and everything is fresh
                    progressReporter.Report(new InstallProgressInfo { Step = "Обновление службы..." });
                    svcResult = await _serviceManager.ReinstallAsync();
                }

                if (!svcResult.Success)
                {
                    AppLogger.Warning($"Файлы обновлены, но не удалось переустановить службу: {svcResult.Message}");
                    ShowOverlay(
                        "Служба не настроена",
                        "Файлы zapret установлены, но службу не удалось подготовить. Попробуйте нажать «Починить всё».",
                        "Починить всё",
                        "Позже",
                        () => TriggerFixAll());
                }
                else
                {
                    AppLogger.Info("Служба zapret успешно переустановлена после обновления.");
                    
                    if (wasRunning)
                    {
                        AppLogger.Info("Восстановление состояния: запуск службы zapret после успешного обновления...");
                        var startResult = await _serviceManager.StartAsync();
                        var finalStatus = await Task.Run(() => _statusService.GetStatus());
                        if (!finalStatus.IsRunning)
                        {
                            AppLogger.Warning($"Не удалось запустить службу после успешного обновления: {startResult.Message}");
                            ShowOverlay(
                                "Не удалось запустить обход",
                                $"zapret успешно обновлён, но запуск службы не удался: {startResult.Message}",
                                "Понятно",
                                "",
                                () => { },
                                closeBehavesAsPrimary: true
                            );
                        }
                        else
                        {
                            AppLogger.Info("Служба zapret успешно запущена после обновления.");
                        }
                    }
                }

                // Clear update state
                _isUpdateAvailable = false;
                _latestRelease = null;

                UpdateUpdateStatusUi();
                UpdateInstallCard();
                RefreshExpertPage();
                _ = CheckStatusOnStartup();
            }
            else
            {
                _hasInstallError = true;
                AppLogger.Warning($"Установка/обновление не удались: {result.ErrorMessage}");
                
                string errorMsg = string.IsNullOrWhiteSpace(result.ErrorMessage) 
                    ? "Обновление не завершилось. Попробуйте ещё раз." 
                    : $"Обновление прервано: {result.ErrorMessage}\nПопробуйте ещё раз.";

                ShowOverlay(
                    "Не удалось обновить zapret",
                    errorMsg,
                    "Понятно",
                    "",
                    () => { },
                    null,
                    closeBehavesAsPrimary: true
                );
                
                if (wasRunning)
                {
                    AppLogger.Info("Перезапуск службы zapret после неудачного обновления для восстановления работы...");
                    await _serviceManager.StartAsync();
                }

                UpdateUpdateStatusUi();
                UpdateInstallCard();
                RefreshExpertPage();
            }
        }
        catch (Exception ex)
        {
            _hasInstallError = true;
            AppLogger.Error($"Критическая ошибка при установке: {ex.Message}");
            ShowOverlay(
                "Не удалось установить zapret",
                $"Критическая ошибка: {ex.Message}",
                "Понятно",
                "",
                () => { },
                closeBehavesAsPrimary: true
            );

            if (wasRunning)
            {
                AppLogger.Info("Перезапуск службы zapret после критической ошибки обновления...");
                await _serviceManager.StartAsync();
            }

            UpdateUpdateStatusUi();
            UpdateInstallCard();
            RefreshExpertPage();
        }
        finally
        {
            _installCts?.Dispose();
            _installCts = null;
            InstallZapretButton.IsEnabled = true;
            CancelOperationButton.Visibility = Visibility.Collapsed;
            ApplyProfileCardState();
            UpdateUpdateStatusUi();
        }
    }

    private async Task<ZapretInstallResult> AdoptValidatedLocalZapretAsync(IProgress<InstallProgressInfo> progressReporter)
    {
        progressReporter.Report(new InstallProgressInfo { Step = "Регистрация локальных файлов..." });

        if (!await Task.Run(() => _installer.ValidateLocalInstall()))
        {
            return ZapretInstallResult.Failed("Не удалось подключить локальные файлы zapret");
        }

        var defaultProfile = ResolveDefaultProfile();

        if (defaultProfile == null)
        {
            return ZapretInstallResult.Failed("Не найдены профили. Не удалось подключить локальные файлы zapret");
        }

        bool origIsInstalled = Settings.IsZapretInstalled;
        string origProfile = Settings.SelectedProfile;
        string origZapretPath = Settings.ZapretPath;
        bool serviceConfirmed = false;

        try
        {
            Settings.SelectedProfile = defaultProfile.FileName;
            Settings.IsZapretInstalled = true;

            if (_installCts != null && _installCts.Token.IsCancellationRequested)
            {
                throw new OperationCanceledException();
            }

            var svcResult = await _serviceManager.ReinstallAsync();
            if (!svcResult.Success)
            {
                Settings.IsZapretInstalled = origIsInstalled;
                Settings.SelectedProfile = origProfile;
                Settings.ZapretPath = origZapretPath;
                return ZapretInstallResult.Failed("Не удалось подключить локальные файлы zapret");
            }

            var status = await Task.Run(() => _statusService.GetStatus());
            if (!status.Exists)
            {
                Settings.IsZapretInstalled = origIsInstalled;
                Settings.SelectedProfile = origProfile;
                Settings.ZapretPath = origZapretPath;
                return ZapretInstallResult.Failed("Не удалось подключить локальные файлы zapret");
            }

            serviceConfirmed = true;
            Settings.ZapretPath = AppPaths.ZapretDirectory;
            if (TryGetLocalZapretVersion(out string localVer))
            {
                Settings.InstalledZapretVersion = localVer;
            }
            else
            {
                Settings.InstalledZapretVersion = string.Empty;
            }

            if (!SafeSaveSettings())
            {
                AppLogger.Warning("Zapret установлен, но настройки не удалось сохранить");
                SetFooterMessage("Zapret установлен, но настройки не удалось сохранить", FooterMessageKind.Warning);
            }

            AppLogger.Info("Zapret установлен из локальных файлов");
            string versionReport = string.IsNullOrWhiteSpace(Settings.InstalledZapretVersion) ? "" : Settings.InstalledZapretVersion;
            return ZapretInstallResult.Successful(versionReport, AppPaths.ZapretDirectory);
        }
        catch
        {
            if (!serviceConfirmed)
            {
                Settings.IsZapretInstalled = origIsInstalled;
                Settings.SelectedProfile = origProfile;
                Settings.ZapretPath = origZapretPath;
            }
            throw;
        }
    }

    private async void InstallZapretButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked InstallZapretButton_Click while auto-pick is active.");
            return;
        }
        await RunZapretInstallOrUpdateAsync();
    }

    private async void HeaderInstallUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Установка/обновление пропущены: выполняется автоподбор профиля.");
            return;
        }

        if (_installCts != null || _isCheckingUpdates) return; // Ignore if running

        bool hasZapretUpdate = (!Settings.IsZapretInstalled && _latestRelease != null) || _isUpdateAvailable || _hasInstallError;
        bool hasKmestuUpdate = _isKmestuUpdateAvailable && !string.IsNullOrWhiteSpace(_latestKmestuRelease);

#if DEBUG
        if (_updateCenterDebugState != UpdateCenterDebugState.None && !_isCheckingUpdates && _installCts == null && !_hasInstallError)
        {
            hasKmestuUpdate = _updateCenterDebugState == UpdateCenterDebugState.OnlyKmestu || _updateCenterDebugState == UpdateCenterDebugState.Both;
            hasZapretUpdate = _updateCenterDebugState == UpdateCenterDebugState.OnlyZapret || _updateCenterDebugState == UpdateCenterDebugState.Both;
        }
#endif

        if (hasZapretUpdate && hasKmestuUpdate)
        {
            // Both updates available, force user to choose from the popup
            UpdateCenterPopup.IsOpen = true;
        }
        else if (hasKmestuUpdate)
        {
            // Only Kmestu update available — run real in-app update flow
            await RunKmestuUpdateAsync();
        }
        else if (hasZapretUpdate)
        {
            // Only zapret update available
            await RunZapretInstallOrUpdateAsync();
        }
    }

    // ─── Kmestu in-app update flow ────────────────────────────────────────────

    /// <summary>
    /// Downloads the latest ZapretKmestu.exe from GitHub, launches the dedicated
    /// ZapretKmestu.Updater.exe process, then closes this application so the
    /// updater can safely replace the running executable.
    ///
    /// On any failure the application keeps running and shows a status message.
    /// No MessageBox is shown at any point.
    /// </summary>
    private async Task RunKmestuUpdateAsync()
    {
        if (_isKmestuUpdating)
        {
            AppLogger.Info("Обновление Kmestu уже выполняется.");
            return;
        }

        // ── 1. Locate the bundled updater ────────────────────────────────────
        string? updaterPath = AppUpdateService.FindUpdaterExecutable();
        if (updaterPath == null)
        {
            AppLogger.Warning("ZapretKmestu.Updater.exe не найден рядом с приложением.");
            SetFooterMessage("Обновление недоступно: файл обновления не найден.", FooterMessageKind.Error, highlight: true);
            return;
        }

        // ── 2. Ensure we have a ZIP download URL ─────────────────────────────
        if (string.IsNullOrWhiteSpace(_latestKmestuZipUrl))
        {
            AppLogger.Warning("URL для загрузки ZIP-архива Kmestu не определён. Попробуйте повторить проверку обновлений.");
            SetFooterMessage("Нет ссылки на ZIP-архив обновления. Повторите проверку.", FooterMessageKind.Warning, highlight: true);
            return;
        }

        _isKmestuUpdating = true;
        UpdateUpdateStatusUi();

        try
        {
            // ── 3. Download ZIP, extract safely, locate EXE ───────────────
            string tag = _latestKmestuRelease ?? "update";
            AppLogger.Info($"Начало загрузки ZIP-обновления Kmestu {tag} с {_latestKmestuZipUrl}");
            SetFooterMessage($"Загрузка обновления {tag}…", FooterMessageKind.Info, highlight: true);

            var progress = new Progress<InstallProgressInfo>(info =>
            {
                if (!string.IsNullOrEmpty(info.Step))
                    Dispatcher.Invoke(() => SetFooterMessage(info.Step +
                        (info.Percent.HasValue ? $" {info.Percent.Value}%" : "") +
                        (info.Details != null ? $" · {info.Details}" : ""),
                        FooterMessageKind.Info, suppressPulse: true));
            });

            // Derive the ZIP file name from the URL (or fall back to a safe default)
            string zipFileName = Uri.TryCreate(_latestKmestuZipUrl, UriKind.Absolute, out var zipUri)
                ? Path.GetFileName(zipUri.LocalPath)
                : "ZapretKmestu.zip";
            if (string.IsNullOrWhiteSpace(zipFileName))
                zipFileName = "ZapretKmestu.zip";

            string newExePath = await _appUpdateService.DownloadExtractAndLocateExeAsync(
                _latestKmestuZipUrl, tag, zipFileName, progress).ConfigureAwait(false);

            // ── 4. Validate downloaded file ────────────────────────────────
            if (!File.Exists(newExePath) || new FileInfo(newExePath).Length == 0)
            {
                AppLogger.Error($"Загруженный файл повреждён или отсутствует: {newExePath}");
                SetFooterMessage("Файл обновления повреждён. Попробуйте снова.", FooterMessageKind.Error, highlight: true);
                return;
            }

            AppLogger.Info($"Файл обновления загружен: {newExePath} ({new FileInfo(newExePath).Length:N0} байт)");

            // ── 5. Determine paths ─────────────────────────────────────────
            string currentExePath = Environment.ProcessPath
                ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppContext.BaseDirectory, "ZapretKmestu.exe");

            string backupPath = currentExePath + ".backup";
            int currentPid    = Environment.ProcessId;

            AppLogger.Info($"Текущий EXE   : {currentExePath}");
            AppLogger.Info($"Новый EXE     : {newExePath}");
            AppLogger.Info($"Резервная копия: {backupPath}");
            AppLogger.Info($"PID            : {currentPid}");
            AppLogger.Info($"Обновляющий   : {updaterPath}");

            // ── 6. Build updater argument string ──────────────────────────
            string updaterArgs = $"--pid {currentPid}" +
                                 $" --current \"{currentExePath}\"" +
                                 $" --new \"{newExePath}\"" +
                                 $" --backup \"{backupPath}\"";

            AppLogger.Info($"Запуск обновляющего процесса: {updaterPath} {updaterArgs}");
            SetFooterMessage("Подготовка обновления… Приложение закроется.", FooterMessageKind.Info, highlight: true);

            // Brief UI pause so the user sees the message before the window closes.
            await Task.Delay(800).ConfigureAwait(false);

            // ── 7. Launch updater and close this application ──────────────
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName        = updaterPath,
                Arguments       = updaterArgs,
                UseShellExecute = false,
                CreateNoWindow  = true
            };

            _installCts?.Cancel();

            System.Diagnostics.Process.Start(startInfo);
            AppLogger.Info("Обновляющий процесс запущен. Закрываем приложение.");

            // Shut down cleanly — updater will wait for this process to exit.
            Dispatcher.Invoke(() =>
            {
                _isReallyClosing = true;
                System.Windows.Application.Current.Shutdown(0);
            });
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("Загрузка обновления Kmestu отменена.");
            SetFooterMessage("Загрузка обновления отменена.", FooterMessageKind.Info, highlight: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при обновлении Kmestu: {ex.Message}");
            SetFooterMessage("Ошибка загрузки обновления. Приложение продолжает работу.", FooterMessageKind.Error, highlight: true);
        }
        finally
        {
            _isKmestuUpdating = false;
            Dispatcher.Invoke(() => UpdateUpdateStatusUi());
        }
    }

    private void UpdateInstallCard(bool filesMissing = false)
    {
        // HIDDEN: This card is no longer visible on the Home page.
        // We only use this method to trigger status updates and handle update alerts.

        if (_isUpdateAvailable && _latestRelease != null && Settings.IsZapretInstalled)
        {
            SetFooterMessage("Доступно обновление zapret", FooterMessageKind.Info);
        }

        _ = CheckStatusOnStartup();
        ApplyProfileCardState();
    }

    private Task<OverlayResult> ShowOverlayAsync(string title, string body, string primaryText, string secondaryText, string? primaryButtonStyle = null, string? tertiaryText = null)
    {
        var tcs = new TaskCompletionSource<OverlayResult>();
        ShowOverlay(title, body, primaryText, secondaryText,
            () => tcs.TrySetResult(OverlayResult.Primary),
            () => tcs.TrySetResult(OverlayResult.Secondary),
            primaryButtonStyle,
            tertiaryText,
            () => tcs.TrySetResult(OverlayResult.Tertiary));
        return tcs.Task;
    }

    private void ShowOverlay(string title, string body, string primaryText, string secondaryText, Action onPrimary, Action? onSecondary = null, string? primaryButtonStyle = null, string? tertiaryText = null, Action? onTertiary = null, bool closeBehavesAsPrimary = false)
    {
        OverlayTitle.Text = title;
        OverlayBody.Text = body;

        bool isModeSelect = title == "Выберите режим подбора";
        OverlayModeContainer.Visibility = isModeSelect ? Visibility.Visible : Visibility.Collapsed;
        
        if (OverlayStandardButtons != null)
        {
            OverlayStandardButtons.Visibility = isModeSelect ? Visibility.Collapsed : Visibility.Visible;
            OverlayStandardButtons.ClearValue(FrameworkElement.MarginProperty);
            OverlayStandardButtons.ClearValue(FrameworkElement.WidthProperty);
            OverlayStandardButtons.ClearValue(System.Windows.Controls.StackPanel.OrientationProperty);
            OverlayStandardButtons.ClearValue(FrameworkElement.HorizontalAlignmentProperty);
        }

        if (OverlayComparisonHeaderContainer != null) OverlayComparisonHeaderContainer.Visibility = Visibility.Collapsed;
        if (OverlayComparisonScroll != null) OverlayComparisonScroll.Visibility = Visibility.Collapsed;
        if (OverlayComparisonContainer != null) OverlayComparisonContainer.Children.Clear();
        if (OverlayComparisonHeaderContainer != null) {
            OverlayComparisonHeaderContainer.Children.Clear();
        }

        if (OverlayPrimaryButton != null) {
            OverlayPrimaryButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
            OverlayPrimaryButton.ClearValue(System.Windows.Controls.Control.PaddingProperty);
            OverlayPrimaryButton.Height = 42;
            OverlayPrimaryButton.ClearValue(System.Windows.Controls.Control.FontSizeProperty);
            OverlayPrimaryButton.ClearValue(FrameworkElement.WidthProperty);
            OverlayPrimaryButton.ClearValue(FrameworkElement.MarginProperty);
            OverlayPrimaryButton.ClearValue(Grid.RowProperty);
            OverlayPrimaryButton.ClearValue(Grid.ColumnProperty);
        }
        if (OverlaySecondaryButton != null) {
            OverlaySecondaryButton.Height = 42;
            OverlaySecondaryButton.ClearValue(System.Windows.Controls.Control.FontSizeProperty);
            OverlaySecondaryButton.ClearValue(FrameworkElement.WidthProperty);
            OverlaySecondaryButton.ClearValue(FrameworkElement.MarginProperty);
            OverlaySecondaryButton.ClearValue(Grid.RowProperty);
            OverlaySecondaryButton.ClearValue(Grid.ColumnProperty);
        }
        if (OverlayTertiaryButton != null) {
            OverlayTertiaryButton.ClearValue(FrameworkElement.HeightProperty);
            OverlayTertiaryButton.ClearValue(System.Windows.Controls.Control.FontSizeProperty);
            OverlayTertiaryButton.ClearValue(FrameworkElement.WidthProperty);
            OverlayTertiaryButton.ClearValue(FrameworkElement.MarginProperty);
            OverlayTertiaryButton.ClearValue(Grid.RowProperty);
            OverlayTertiaryButton.ClearValue(Grid.ColumnProperty);
            OverlayTertiaryButton.ClearValue(Grid.ColumnSpanProperty);
        }
        if (OverlayCard != null) {
            OverlayCard.Width = 440;
            OverlayCard.ClearValue(FrameworkElement.MaxHeightProperty);
        }
        if (OverlayBody != null) OverlayBody.Visibility = Visibility.Visible;

        if (OverlayPrimaryButton != null) OverlayPrimaryButton.Content = primaryText;
        if (OverlaySecondaryButton != null) OverlaySecondaryButton.Content = secondaryText;

        if (string.IsNullOrEmpty(secondaryText))
        {
            if (OverlaySecondaryButton != null) OverlaySecondaryButton.Visibility = Visibility.Collapsed;
            if (OverlayPrimaryButton != null)
            {
                Grid.SetColumn(OverlayPrimaryButton, 0);
                Grid.SetColumnSpan(OverlayPrimaryButton, 2);
                OverlayPrimaryButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
                OverlayPrimaryButton.Width = 180;
                OverlayPrimaryButton.Margin = new Thickness(0);
            }
        }
        else
        {
            if (OverlaySecondaryButton != null)
            {
                OverlaySecondaryButton.Visibility = Visibility.Visible;
                Grid.SetColumn(OverlaySecondaryButton, 1);
                Grid.SetColumnSpan(OverlaySecondaryButton, 1);
                OverlaySecondaryButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Left;
                OverlaySecondaryButton.Width = 180;
                OverlaySecondaryButton.Margin = new Thickness(6, 0, 0, 0);
            }
            if (OverlayPrimaryButton != null)
            {
                Grid.SetColumn(OverlayPrimaryButton, 0);
                Grid.SetColumnSpan(OverlayPrimaryButton, 1);
                OverlayPrimaryButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
                OverlayPrimaryButton.Width = 180;
                OverlayPrimaryButton.Margin = new Thickness(0, 0, 6, 0);
            }
        }

        _overlayPrimaryAction = onPrimary;
        _overlaySecondaryAction = onSecondary;
        _overlayTertiaryAction = onTertiary;
        _closeBehavesAsPrimary = closeBehavesAsPrimary;

        if (!string.IsNullOrEmpty(tertiaryText) && onTertiary != null)
        {
            if (OverlayTertiaryButton != null)
            {
                OverlayTertiaryButton.Content = tertiaryText;
                OverlayTertiaryButton.Visibility = Visibility.Visible;
            }
        }
        else
        {
            if (OverlayTertiaryButton != null) OverlayTertiaryButton.Visibility = Visibility.Collapsed;
        }

        // Apply style
        var styleKey = primaryButtonStyle ?? "PrimaryButtonStyle";
        if (TryFindResource(styleKey) is Style style)
        {
            if (OverlayPrimaryButton != null) OverlayPrimaryButton.Style = style;
        }

        MainOverlay.Visibility = Visibility.Visible;
    }

    private void OverlayPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        var action = _overlayPrimaryAction;
        ClearOverlayState();
        action?.Invoke();
    }

    private void OverlaySecondaryButton_Click(object sender, RoutedEventArgs e)
    {
#if DEBUG
        if (_isDebugMenuOpen && sender == OverlayCloseButton)
        {
            ClearOverlayState();
            _isDebugMenuOpen = false;
            return;
        }
#endif
        var action = (_closeBehavesAsPrimary && sender == OverlayCloseButton)
            ? _overlayPrimaryAction
            : _overlaySecondaryAction;

        ClearOverlayState();
#if DEBUG
        _isDebugMenuOpen = false;
#endif
        action?.Invoke();
    }

    private void OverlayTertiaryButton_Click(object sender, RoutedEventArgs e)
    {
        var action = _overlayTertiaryAction;
        ClearOverlayState();
        action?.Invoke();
    }

    private void ClearOverlayState()
    {
        MainOverlay.Visibility = Visibility.Collapsed;
        _overlayPrimaryAction = null;
        _overlaySecondaryAction = null;
        _overlayTertiaryAction = null;
    }


    // ─── Главная — Action buttons ─────────────────────────────────────────────

    private void AutoCheckTimer_Tick(object? sender, EventArgs e)
    {
        _ = ExecuteNetworkDiagnosticAsync(false);
    }

    private async Task ExecuteNetworkDiagnosticAsync(bool isManualCheck)
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked ExecuteNetworkDiagnosticAsync while auto-pick is active.");
            return;
        }
        if (!Settings.UseDiagnostics) return;
        if (_isNetworkCheckRunning)
        {
            if (isManualCheck) SetFooterMessage("Проверка уже выполняется.", FooterMessageKind.Warning, suppressPulse: true);
            return;
        }
        _isNetworkCheckRunning = true;

        if (CheckConnectionButton != null)
        {
            CheckConnectionButton.Opacity = 0.6;
        }

        try
        {
            if (isManualCheck)
            {
                AppLogger.Info("Запуск диагностики сети...");

                if (CheckConnectionButtonText != null)
                {
                    CheckConnectionButtonText.Text = "Проверка";
                }

                if (RefreshIconRotation != null)
                {
                    var animation = new DoubleAnimation
                    {
                        From = 0,
                        To = 360,
                        Duration = TimeSpan.FromSeconds(1.5),
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    RefreshIconRotation.BeginAnimation(RotateTransform.AngleProperty, animation);
                }
            }

            if (isManualCheck && CheckConnectionButton != null)
            {
                CheckConnectionButton.IsEnabled = false;
            }

            UpdateConnectionDiagnosticSummary();
            _ = RefreshVpnStatusAsync(animate: false);

            var youtubeTask = CheckServiceAvailabilityAsync("YouTube", "https://www.youtube.com/generate_204", YouTubeStatusText, isManualCheck);
            var discordTask = CheckServiceAvailabilityAsync("Discord", "https://discord.com/api/v10/gateway", DiscordStatusText, isManualCheck);

            await Task.WhenAll(youtubeTask, discordTask);

            UpdateConnectionDiagnosticSummary();
            await RefreshVpnStatusAsync(animate: false);

            _lastCheckTime = DateTime.Now;
            if (LastCheckText != null)
            {
                LastCheckText.Text = $"Обновлено {_lastCheckTime.Value:HH:mm:ss}";
            }
        }
        catch (Exception ex)
        {
            if (isManualCheck)
            {
                AppLogger.Error($"Ошибка при проверке сети: {ex.Message}");
                ApplyDiagnosticsErrorState("Не удалось выполнить проверку");
            }
        }
        finally
        {
            if (isManualCheck)
            {
                if (CheckConnectionButtonText != null)
                {
                    CheckConnectionButtonText.Text = "Обновить";
                }

                if (RefreshIconRotation != null)
                {
                    RefreshIconRotation.BeginAnimation(RotateTransform.AngleProperty, null);
                }
            }

            if (CheckConnectionButton != null)
            {
                bool isBusy = _isWizardRunning || _installCts != null || _isTrayBypassToggleRunning || _isTrayProfileApplyRunning || OperationProgressCard.Visibility == Visibility.Visible;
                CheckConnectionButton.IsEnabled = !isBusy;
                CheckConnectionButton.Opacity = 1.0;
            }
            _isNetworkCheckRunning = false;
        }
    }

    private void ApplyDiagnosticsErrorState(string userText)
    {
        if (YouTubeStatusText != null)
        {
            YouTubeStatusText.Text = "Ошибка";
            YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }
        if (DiscordStatusText != null)
        {
            DiscordStatusText.Text = "Ошибка";
            DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }

        if (YouTubeQualityBadge != null) YouTubeQualityBadge.Visibility = Visibility.Collapsed;
        if (DiscordQualityBadge != null) DiscordQualityBadge.Visibility = Visibility.Collapsed;

        if (YouTubeLatencyBadge != null) YouTubeLatencyBadge.Visibility = Visibility.Collapsed;
        if (DiscordLatencyBadge != null) DiscordLatencyBadge.Visibility = Visibility.Collapsed;

        if (DiagTitleText != null)
        {
            DiagTitleText.Text = "Ошибка";
            DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }
        
        if (DiagDescText != null)
        {
            DiagDescText.Text = userText;
        }

        if (DiagPingText != null)
        {
            DiagPingText.Text = "Сбой";
            DiagPingText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            DiagDnsText.Text = "Сбой";
            DiagDnsText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            DiagLossText.Text = "Сбой";
            DiagLossText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            DiagStabilityText.Text = "Ошибка";
            DiagStabilityText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }

        _youtubeProbeState = DiagnosticProbeState.Error;
        _discordProbeState = DiagnosticProbeState.Error;
    }

    private async void QuickCheckButton_Click(object sender, RoutedEventArgs e)
    {
        await ExecuteNetworkDiagnosticAsync(true);
    }

    private async Task RunQuickCheckAsync()
    {
        try
        {
            AppLogger.Info("=== Быстрая проверка zapret ===");
            bool issuesFound = false;

            // 1. Admin status
            bool isAdmin = _adminService.IsRunningAsAdministrator();
            AppLogger.Info($"- Права администратора: {(isAdmin ? "да" : "нет")}");
            if (!isAdmin) issuesFound = true;

            // 2. Local zapret installation
            bool zapretDirExists = Directory.Exists(AppPaths.ZapretDirectory);
            AppLogger.Info($"- Локальная установка zapret: {(zapretDirExists ? "найдена" : "не найдена")}");
            if (!zapretDirExists) issuesFound = true;

            bool winwsExists = File.Exists(Path.Combine(AppPaths.ZapretDirectory, "bin", "winws.exe"));
            AppLogger.Info($"- winws.exe: {(winwsExists ? "найден" : "не найден")}");
            if (!winwsExists) issuesFound = true;

            bool serviceBatExists = File.Exists(Path.Combine(AppPaths.ZapretDirectory, "service.bat"));
            AppLogger.Info($"- service.bat: {(serviceBatExists ? "найден" : "не найден")}");
            if (!serviceBatExists) issuesFound = true;

            // 3. Profile state
            string profileName = Settings.SelectedProfile ?? "не выбран";
            AppLogger.Info($"- Профиль: {profileName}");
            if (string.IsNullOrEmpty(Settings.SelectedProfile)) issuesFound = true;

            // 4. Service state
            var status = await Task.Run(() => _statusService.GetStatus());
            string serviceStatus = status.Exists
                ? (status.IsRunning ? "запущена" : "остановлена")
                : "не найдена";
            AppLogger.Info($"- Служба zapret: {serviceStatus}");
            if (!status.Exists) issuesFound = true;

            // 5. Process state
            bool isProcessRunning = _serviceManager.IsWinwsProcessRunning();
            AppLogger.Info($"- Процесс winws.exe: {(isProcessRunning ? "запущен" : "не запущен")}");

            // 6. Argument reference file
            bool argsExists = File.Exists(AppPaths.ZapretArgsFile);
            if (argsExists)
            {
                long size = new FileInfo(AppPaths.ZapretArgsFile).Length;
                AppLogger.Info($"- Файл параметров: найден, размер {size} байт");
                AppLogger.Info("  (Примечание: этот файл используется только для диагностики и не влияет на работу службы)");
            }
            else
            {
                AppLogger.Info("- Файл параметров: не найден");
            }

            // 7. VPN check
            bool isVpn = IsPossibleVpnActive();
            AppLogger.Info(isVpn ? "VPN: возможно включён" : "VPN: явных признаков не найдено");

            // 8. Service checks
            AppLogger.Info("- Проверка доступности сервисов...");
            SetFooterMessage("Проверяем подключение...", FooterMessageKind.Info, highlight: true);
            var youtubeTask = CheckServiceAvailabilityAsync("YouTube", "https://www.youtube.com/generate_204", YouTubeStatusText);
            var discordTask = CheckServiceAvailabilityAsync("Discord", "https://discord.com/api/v10/gateway", DiscordStatusText);

            UpdateConnectionDiagnosticSummary();
            _ = RefreshVpnStatusAsync();

            await Task.WhenAll(youtubeTask, discordTask);

            UpdateConnectionDiagnosticSummary();
            await RefreshVpnStatusAsync();

            // Result
            _lastCheckTime = DateTime.Now;
            LastCheckText.Text = $"Обновлено {_lastCheckTime.Value:HH:mm:ss}";

            if (!isAdmin)
            {
                AppLogger.Info("Итог: нет прав администратора");
                SetFooterMessage("Для корректной работы нужны права администратора", FooterMessageKind.Warning, highlight: true);
            }
            else if (!zapretDirExists || !winwsExists)
            {
                AppLogger.Info("Итог: zapret не установлен");
                SetFooterMessage("Сначала установите zapret", FooterMessageKind.Info, highlight: true);
            }
            else if (issuesFound)
            {
                AppLogger.Info("Итог: найдены проблемы");
            }
            else
            {
                AppLogger.Info("Итог: всё выглядит нормально");
            }

            // Refresh UI status as well
            await CheckStatusOnStartup();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Критическая ошибка при быстрой проверке: {ex.Message}");
            ShowOverlay(
                "Проверка не завершилась",
                "Не удалось выполнить проверку подключения. Попробуйте ещё раз.",
                "Понятно",
                "",
                () => { },
                closeBehavesAsPrimary: true
            );
        }
    }

    private async void BestProfileButton_Click(object sender, RoutedEventArgs e)
    {
        if (!Settings.IsZapretInstalled)
        {
            // The Hero card is the primary UI for this state.
            // Clicking "Подбор профиля" when not installed will just do nothing or we could show a tooltip.
            // But definitely not the shifting top alert.
            return;
        }

        if (!EnsureAdmin())
        {
            return;
        }

        var result = await ShowOverlayAsync(
            "Выберите режим подбора",
            "Выберите, сколько вариантов проверить.",
            "Быстрый",
            "Отмена",
            null,
            "Точный"
        );

        if (result == OverlayResult.Primary)
        {
            Settings.ProfileCheckMode = "Fast";
            SafeSaveSettings();
            await RunBestProfileWizardAsync(ProfileCheckMode.Fast);
        }
        else if (result == OverlayResult.Tertiary)
        {
            Settings.ProfileCheckMode = "Accurate";
            SafeSaveSettings();
            await RunBestProfileWizardAsync(ProfileCheckMode.Accurate);
        }
        else
        {
            AppLogger.Info("Подбор профиля отменён пользователем (оверлей).");
            SetFooterMessage("Подбор профиля отменён", FooterMessageKind.Info, highlight: true);
        }

        AppLogger.Info("Мастер подбора: показан выбор режима в оверлее (Быстрый/Точный).");
    }

    private void SetAutoPickInteractionState(bool active)
    {
        bool enable = !active;

        if (GameFilterToggle != null)
        {
            GameFilterToggle.IsEnabled = enable;
            GameFilterToggle.IsHitTestVisible = enable;
            GameFilterToggle.Focusable = enable;
            GameFilterToggle.IsTabStop = enable;
        }
        if (GameFilterUdpButton != null) GameFilterUdpButton.IsEnabled = enable;
        if (GameFilterTcpButton != null) GameFilterTcpButton.IsEnabled = enable;
        if (GameFilterTcpUdpButton != null) GameFilterTcpUdpButton.IsEnabled = enable;
        if (GameScopeListsButton != null) GameScopeListsButton.IsEnabled = enable;
        if (GameScopeExtendedButton != null) GameScopeExtendedButton.IsEnabled = enable;
        if (GameScopeAllButton != null) GameScopeAllButton.IsEnabled = enable;

        if (active)
        {
            if (HomeProfileComboBox != null) HomeProfileComboBox.IsEnabled = false;
            if (ProfileComboBox != null) ProfileComboBox.IsEnabled = false;

            if (ReinstallServiceButton != null) ReinstallServiceButton.IsEnabled = false;
            if (FixAllButton != null) FixAllButton.IsEnabled = false;
            if (InstallZapretButton != null) InstallZapretButton.IsEnabled = false;
            if (UninstallAppButton != null) UninstallAppButton.IsEnabled = false;
            if (CheckConnectionButton != null) CheckConnectionButton.IsEnabled = false;
            if (BestProfileButton != null) BestProfileButton.IsEnabled = false;

            if (GameFilterToggle != null) GameFilterToggle.Opacity = 0.5;

            if (YouTubeGraphPausedState != null) YouTubeGraphPausedState.Visibility = Visibility.Visible;
            if (DiscordGraphPausedState != null) DiscordGraphPausedState.Visibility = Visibility.Visible;
            if (YouTubeGraphPlaceholderText != null) YouTubeGraphPlaceholderText.Visibility = Visibility.Collapsed;
            if (DiscordGraphPlaceholderText != null) DiscordGraphPlaceholderText.Visibility = Visibility.Collapsed;
            if (YouTubeGraphContainer != null) YouTubeGraphContainer.Visibility = Visibility.Collapsed;
            if (DiscordGraphContainer != null) DiscordGraphContainer.Visibility = Visibility.Collapsed;

            if (YouTubeLatencyBadge != null) YouTubeLatencyBadge.Visibility = Visibility.Collapsed;
            if (YouTubeQualityBadge != null) YouTubeQualityBadge.Visibility = Visibility.Collapsed;
            if (YouTubeStatusText != null) YouTubeStatusText.Visibility = Visibility.Collapsed;

            if (DiscordLatencyBadge != null) DiscordLatencyBadge.Visibility = Visibility.Collapsed;
            if (DiscordQualityBadge != null) DiscordQualityBadge.Visibility = Visibility.Collapsed;
            if (DiscordStatusText != null) DiscordStatusText.Visibility = Visibility.Collapsed;

            SyncGameFilterUiFromActualState(isBusy: true, busyStatus: "Недоступно во время автоподбора");
        }
        else
        {
            if (GameFilterToggle != null) GameFilterToggle.Opacity = 1.0;
            if (YouTubeGraphPausedState != null) YouTubeGraphPausedState.Visibility = Visibility.Collapsed;
            if (DiscordGraphPausedState != null) DiscordGraphPausedState.Visibility = Visibility.Collapsed;
            if (YouTubeGraphContainer != null) YouTubeGraphContainer.Visibility = Visibility.Visible;
            if (DiscordGraphContainer != null) DiscordGraphContainer.Visibility = Visibility.Visible;

            if (YouTubeStatusText != null) YouTubeStatusText.Visibility = Visibility.Visible;
            if (DiscordStatusText != null) DiscordStatusText.Visibility = Visibility.Visible;

            SyncGameFilterUiFromActualState();

            if (InstallZapretButton != null) InstallZapretButton.IsEnabled = _installCts == null;

            var currentStatus = _statusService.GetStatus();
            ApplyStatusToUi(currentStatus, _lastVpnActive, false);
        }

        if (active)
        {
            CloseUpdateCenterPopup();
        }

        UpdateUpdateStatusUi();
    }

    private async Task RunBestProfileWizardAsync(ProfileCheckMode mode)
    {
        if (_isWizardRunning)
        {
            AppLogger.Warning("Попытка запустить подбор профиля, когда он уже работает.");
            return;
        }

        _currentWizardMode = mode;
        AppLogger.Info($"=== Запуск мастера подбора профиля (Режим: {mode}) ===");

        _isWizardRunning = true;
        _wizardCompletionTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SetAutoPickInteractionState(true);
        _suppressWizardFooterNoise = true;
        _autopickStartTime = DateTime.Now;
        _wizardCts = new CancellationTokenSource();
        var token = _wizardCts.Token;

        _originalProfileBeforeWizard = Settings.SelectedProfile;
        var initialStatus = await Task.Run(() => _statusService.GetStatus());
        _wasRunningBeforeWizard = initialStatus.IsRunning;
        bool vpnWasActive = IsPossibleVpnActive();

        // Update UI
        ApplyStatusToUi(initialStatus, vpnWasActive, false);
        UpdateConnectionDiagnosticSummary();
        SetFooterMessage("Идёт проверка профилей…", FooterMessageKind.Info, highlight: true);

        BestProfileButton.IsEnabled = false;
        CheckConnectionButton.IsEnabled = false;
        FixAllButton.IsEnabled = false;

        OperationProgressCard.Visibility = Visibility.Visible;
        InstallProgressPanel.Visibility = Visibility.Visible;
        CancelOperationButton.Visibility = Visibility.Collapsed;
        InstallProgressBar.IsIndeterminate = false;
        InstallProgressBar.Value = 0;
        InstallPercentText.Text = "0%";

        SetLeftProfileBusyState(true);
        UpdateLeftProfileBusyDetails("Подготовка профилей…", "Оцениваем время…");

        try
        {
            // 1. Determine candidates
            var allProfiles = _profileService.GetAvailableProfiles().Select(p => p.FileName).ToList();
            List<string> candidates;

            if (mode == ProfileCheckMode.Fast)
            {
                candidates = BuildProfileCandidateList(allProfiles);
                AppLogger.Info($"Быстрый подбор: сформирован список кандидатов: {candidates.Count} из {allProfiles.Count}.");
            }
            else
            {
                // Accurate mode tests all
                candidates = BuildProfileCandidateList(allProfiles);
            }

            if (candidates.Count == 0)
                throw new Exception("В папке zapret не найдено ни одного профиля (.bat)");

            AppLogger.Info($"Всего кандидатов для проверки: {candidates.Count}");

            List<ProfileCheckResult> results = new();

            // 2. Loop candidates
            for (int i = 0; i < candidates.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                string profile = candidates[i];
                double overallBase = (double)i / candidates.Count * 90;
                double stepSize = 90.0 / candidates.Count;

                var currentResult = new ProfileCheckResult { ProfileName = profile };
                var startTime = DateTime.Now;

                UpdateLeftProfileBusyDetails(profile);
                AppLogger.Info($"--- Тестирование: {profile} ---");

                Settings.SelectedProfile = profile;

                // Sub-step: Reinstall
                string reinstallStep = (mode == ProfileCheckMode.Accurate ? $"Точная проверка {i + 1} из {candidates.Count}: {profile}\n" : "") + "Подготовка...";
                UpdateWizardStatus(overallBase + (stepSize * 0.05), reinstallStep, results, candidates.Count);
                await _serviceManager.ReinstallAsync();

                // Sub-step: Start
                token.ThrowIfCancellationRequested();
                string startStep = (mode == ProfileCheckMode.Accurate ? $"Точная проверка {i + 1} из {candidates.Count}: {profile}\n" : "") + "Запуск службы...";
                UpdateWizardStatus(overallBase + (stepSize * 0.15), startStep, results, candidates.Count);
                await _serviceManager.StartAsync();

                // Sub-step: Warm-up
                token.ThrowIfCancellationRequested();
                int warmUpDelay = mode == ProfileCheckMode.Fast ? 2000 : 4500;
                string warmupStep = (mode == ProfileCheckMode.Accurate ? $"Точная проверка {i + 1} из {candidates.Count}: {profile}\n" : "") + "Ожидание запуска службы...";
                UpdateWizardStatus(overallBase + (stepSize * 0.25), warmupStep, results, candidates.Count);
                await Task.Delay(warmUpDelay, token);

                // Sub-step: Probes
                token.ThrowIfCancellationRequested();
                string probeStep = (mode == ProfileCheckMode.Accurate ? $"Точная проверка {i + 1} из {candidates.Count}: {profile}\n" : "") + "Проверка ресурсов...";
                UpdateWizardStatus(overallBase + (stepSize * 0.45), probeStep, results, candidates.Count);

                var ytTask = PerformMultiProbeCheckAsync(
                    "YouTube",
                    new[] { "https://www.youtube.com/generate_204", "https://i.ytimg.com/generate_204" },
                    new[] { 30, 20 },
                    mode == ProfileCheckMode.Fast ? 5000 : 10000,
                    mode == ProfileCheckMode.Fast ? 1 : 2,
                    token);

                var dsTask = PerformMultiProbeCheckAsync(
                    "Discord",
                    new[] { "https://discord.com/api/v10/gateway", "https://discordapp.com/api/v9/experiments" },
                    new[] { 30, 20 },
                    mode == ProfileCheckMode.Fast ? 5000 : 10000,
                    mode == ProfileCheckMode.Fast ? 1 : 2,
                    token);

                await Task.WhenAll(ytTask, dsTask);
                var ytProbe = ytTask.Result;
                var dsProbe = dsTask.Result;

                currentResult.YouTubeAvailable = ytProbe.available;
                currentResult.YouTubeScore = ytProbe.score;
                if (!string.IsNullOrEmpty(ytProbe.errors)) currentResult.Errors += ytProbe.errors + "; ";

                currentResult.DiscordAvailable = dsProbe.available;
                currentResult.DiscordScore = dsProbe.score;
                if (!string.IsNullOrEmpty(dsProbe.errors)) currentResult.Errors += dsProbe.errors;

                currentResult.SuccessCount = ytProbe.successCount + dsProbe.successCount;
                currentResult.TotalProbes = ytProbe.totalProbes + dsProbe.totalProbes;
                currentResult.CheckDuration = DateTime.Now - startTime;
                currentResult.WasConfirmationChecked = mode == ProfileCheckMode.Accurate;
                results.Add(currentResult);

                AppLogger.Info($"Результат {profile}: YT={currentResult.YouTubeScore}, DS={currentResult.DiscordScore}, Total={currentResult.TotalScore}");

                // Sub-step: Stop
                string stopStep = (mode == ProfileCheckMode.Accurate ? $"Точная проверка {i + 1} из {candidates.Count}: {profile}\n" : "") + "Остановка...";
                UpdateWizardStatus(overallBase + (stepSize * 0.95), stopStep, results, candidates.Count);
                await _serviceManager.StopAsync();
            }

            // Accurate mode: re-test tied leaders
            if (mode == ProfileCheckMode.Accurate && results.Count > 0 && !token.IsCancellationRequested)
            {
                long maxAccurateScore = results.Max(r => GetMeasuredRankingScore(r));
                if (maxAccurateScore > 0)
                {
                    var finalists = results.Where(r => GetMeasuredRankingScore(r) == maxAccurateScore && (r.YouTubeAvailable || r.DiscordAvailable)).ToList();
                    AppLogger.Info($"Точный подбор: повторная проверка {finalists.Count} финалист(ов): {string.Join(", ", finalists.Select(f => f.ProfileName))}");

                    if (finalists.Count > 0)
                    {
                        var recheckResults = new List<(ProfileCheckResult Original, bool YtAvail, int YtScore, bool DsAvail, int DsScore, int Succ, int Probes)>();
                        InstallStepText.Text = "Повторная проверка финалистов...";

                        foreach (var finalist in finalists)
                        {
                            token.ThrowIfCancellationRequested();
                            AppLogger.Info($"--- Повторная проверка финалиста: {finalist.ProfileName} ---");
                            Settings.SelectedProfile = finalist.ProfileName;
                            await _serviceManager.ReinstallAsync();
                            await _serviceManager.StartAsync();
                            await Task.Delay(4500, token);

                            var ytRecheckTask = PerformMultiProbeCheckAsync(
                                "YouTube",
                                new[] { "https://www.youtube.com/generate_204" },
                                new[] { 30 },
                                10000, 2, token);

                            var dsRecheckTask = PerformMultiProbeCheckAsync(
                                "Discord",
                                new[] { "https://discord.com/api/v10/gateway" },
                                new[] { 30 },
                                10000, 2, token);

                            await Task.WhenAll(ytRecheckTask, dsRecheckTask);
                            var ytRecheck = ytRecheckTask.Result;
                            var dsRecheck = dsRecheckTask.Result;

                            await _serviceManager.StopAsync();
                            recheckResults.Add((finalist, ytRecheck.available, ytRecheck.score, dsRecheck.available, dsRecheck.score, ytRecheck.successCount + dsRecheck.successCount, ytRecheck.totalProbes + dsRecheck.totalProbes));
                        }

                        // Update finalists with new scores
                        foreach (var rr in recheckResults)
                        {
                            rr.Original.YouTubeAvailable = rr.YtAvail;
                            rr.Original.DiscordAvailable = rr.DsAvail;
                            rr.Original.YouTubeScore = rr.YtScore;
                            rr.Original.DiscordScore = rr.DsScore;
                            rr.Original.SuccessCount = rr.Succ;
                            rr.Original.TotalProbes = rr.Probes;
                        }
                    }
                }
            }

            long maxFinalScore = results.Count > 0 ? results.Max(r => GetMeasuredRankingScore(r)) : 0;
            var tiedLeaders = results.Where(r => GetMeasuredRankingScore(r) == maxFinalScore && maxFinalScore > 0 && (r.YouTubeAvailable || r.DiscordAvailable)).ToList();

            foreach (var r in results)
            {
                r.IsWinner = false;
                r.IsTie = false;
            }

            if (tiedLeaders.Count == 1)
            {
                AppLogger.Info($"Единоличный лидер: {tiedLeaders[0].ProfileName}");
                tiedLeaders[0].IsWinner = true;
            }
            else if (tiedLeaders.Count > 1)
            {
                AppLogger.Info($"После проверки осталось несколько лидеров. Устанавливаем статус IsTie = true.");
                foreach (var leader in tiedLeaders)
                {
                    leader.IsTie = true;
                }
            }

            UpdateWizardStatus(100, "Завершение...", results, candidates.Count);
            InstallProgressBar.Value = 100;
            InstallPercentText.Text = "100%";

            ProfileCheckResult? bestResult = results.FirstOrDefault(r => r.IsWinner);

            bool hasUsableBestResult =
                bestResult != null
                && bestResult.TotalScore > 0
                && (bestResult.YouTubeAvailable || bestResult.DiscordAvailable);

            _lastWizardResults = results.ToList();
            _lastWizardCompletedAt = DateTime.Now;

            // Ensure IsWinner is only true if we have a usable bestResult. Otherwise, if there is a tie, no one is IsWinner.
            foreach (var item in _lastWizardResults)
            {
                if (item != bestResult)
                    item.IsWinner = false;
            }

            _lastWizardResult = hasUsableBestResult ? bestResult : null;

            SaveLastAutoPickResults();
            UpdateLastWizardResultsButtonState();

            if (hasUsableBestResult || tiedLeaders.Count > 1)
            {
                // 3. Handle Result UI
                if (bestResult != null)
                {
                    _bestProfileCandidate = bestResult.ProfileName;
                    _bestProfileScore = bestResult.TotalScore;
                    AppLogger.Info($"Мастер завершён. Результат показан в оверлее-таблице. Лучший: {bestResult.ProfileName}, Счёт: {bestResult.TotalScore}");
                }
                else
                {
                    _bestProfileCandidate = string.Empty;
                    _bestProfileScore = -1;
                    AppLogger.Info($"Мастер завершён. Результат показан в оверлее-таблице. Победителя нет (ничья).");
                }

                _suppressWizardFooterNoise = false;
                // RESTORE ORIGINAL PROFILE BEFORE OVERLAY
                // RESTORE ORIGINAL PROFILE BEFORE OVERLAY
                UpdateWizardStatus(90, "Восстанавливаем исходный профиль…", _lastWizardResults!, candidates.Count);
                await RestoreBestProfileWizardOriginalState(_originalProfileBeforeWizard, _wasRunningBeforeWizard);
                UpdateWizardStatus(100, "Готово", _lastWizardResults!, candidates.Count);

                SetFooterMessage(bestResult != null ? "Профиль найден" : "Проверка завершена", FooterMessageKind.Success, highlight: true);

                var overlayResult = await ShowFinalWizardResultsOverlayAsync(_lastWizardResults!, bestResult, mode, vpnWasActive);

                if (overlayResult == OverlayResult.Primary)
                {
                    if (bestResult != null &&
                        bestResult.IsWinner &&
                        !bestResult.IsTie &&
                        !string.IsNullOrWhiteSpace(bestResult.ProfileName) &&
                        !string.IsNullOrWhiteSpace(_bestProfileCandidate) &&
                        _lastWizardResults!.Count(r => r.IsWinner) == 1)
                    {
                        SetLeftProfileBusyState(false);
                        LastWizardResultsButton.IsEnabled = false;
                        LastWizardResultsButton.Opacity = 0.5;

                        try
                        {
                            Settings.SelectedProfile = _bestProfileCandidate;
                            SafeSaveSettings();
                            SetFooterMessage("Применение профиля…", FooterMessageKind.Info, highlight: false);
                            await _serviceManager.ReinstallAsync();
                            await _serviceManager.StartAsync();
                            SyncProfileComboBoxes(_bestProfileCandidate);
                            SyncProfileComboBoxes(_bestProfileCandidate);

                            AppLogger.Info($"Мастер: профиль {_bestProfileCandidate} успешно применён.");
                            SetFooterMessage("Профиль найден", FooterMessageKind.Success, highlight: true);
                            _ = ExecuteNetworkDiagnosticAsync(false);
                        }
                        finally
                        {
                            UpdateLastWizardResultsButtonState();
                        }
                    }
                    else
                    {
                        AppLogger.Info("Мастер: пользователь нажал Готово во время ничьей.");
                        SetFooterMessage("Преимущество не выявлено — текущий профиль сохранён", FooterMessageKind.Info, highlight: true);
                    }
                }
                else if (overlayResult == OverlayResult.Secondary)
                {
                    AppLogger.Info("Мастер: пользователь выбрал оставить старый профиль (явный выбор или закрытие).");
                }
                else if (overlayResult == OverlayResult.Tertiary)
                {
                    await RunBestProfileWizardAsync(ProfileCheckMode.Accurate);
                }
                else
                {
                    AppLogger.Info("Мастер: пользователь закрыл окно оверлея или отменил действие.");
                }
            }
            else
            {
                // No usable result: bestResult is null, or TotalScore == 0,
                // or neither service is available. Restore original profile.
                AppLogger.Warning("Мастер: стабильный профиль не найден. Возвращаем исходный профиль.");
                _suppressWizardFooterNoise = false;
                SetFooterMessage("Стабильный профиль не найден", FooterMessageKind.Warning, highlight: true);

                AppLogger.Info("Подбор завершился безрезультатно. Результаты сохранены.");

                UpdateWizardStatus(90, "Восстанавливаем исходный профиль…", results, candidates.Count);
                await RestoreBestProfileWizardOriginalState(_originalProfileBeforeWizard, _wasRunningBeforeWizard);
                UpdateWizardStatus(100, "Готово", results, candidates.Count);

                ShowOverlay(
                    "Не удалось подобрать профиль",
                    "К сожалению, ни один из проверенных профилей не смог обеспечить стабильный доступ к заблокированным ресурсам. Возвращён исходный профиль.",
                    "Понятно",
                    "",
                    () => { },
                    null,
                    closeBehavesAsPrimary: true
                );
            }
        }
        catch (OperationCanceledException)
        {
            AppLogger.Info("Мастер подбора отменён пользователем.");
            SetFooterMessage("Подбор профиля отменён", FooterMessageKind.Info, highlight: true);
            await RestoreBestProfileWizardOriginalState(_originalProfileBeforeWizard, _wasRunningBeforeWizard);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка мастера подбора: {ex.GetType().Name} - {ex.Message}");
            SetFooterMessage("Ошибка подбора профиля.", FooterMessageKind.Error, highlight: true);
            await RestoreBestProfileWizardOriginalState(_originalProfileBeforeWizard, _wasRunningBeforeWizard);
            ShowOverlay(
                "Ошибка во время подбора",
                $"Произошла непредвиденная ошибка:\n{ex.Message}\n\nИсходный профиль восстановлен.",
                "Понятно",
                "",
                () => { },
                null,
                closeBehavesAsPrimary: true
            );
        }
        finally
        {
            _isWizardRunning = false;
            SetAutoPickInteractionState(false);

            var tcs = _wizardCompletionTcs;
            _wizardCompletionTcs = null;
            tcs?.TrySetResult();

            _autopickStartTime = null;
            UpdateConnectionDiagnosticSummary();
            _wizardCts?.Dispose();
            _wizardCts = null;

            OperationProgressCard.Visibility = Visibility.Collapsed;
            CancelOperationButton.Visibility = Visibility.Collapsed;
            BestProfileButton.IsEnabled = true;
            CheckConnectionButton.IsEnabled = true;
            FixAllButton.IsEnabled = true;

            SetLeftProfileBusyState(false);
            UpdateLeftProfileBusyDetails("", "Оцениваем время…");

            await CheckStatusOnStartup();
            RefreshExpertPage();

            _suppressWizardFooterNoise = false;

            if (!_isReallyClosing)
            {
                _ = ExecuteNetworkDiagnosticAsync(false);
            }
        }
    }

    private void UpdateLastWizardResultsButtonState()
    {
        LastWizardResultsButton.Visibility = Visibility.Visible;
        if (_lastWizardResults != null && _lastWizardResults.Count > 0)
        {
            LastWizardResultsButton.IsEnabled = true;
            LastWizardResultsButton.ToolTip = "Показать результаты последнего подбора";
            LastWizardResultsButton.Opacity = 1.0;
        }
        else
        {
            LastWizardResultsButton.IsEnabled = false;
            LastWizardResultsButton.ToolTip = "Результатов подбора пока нет";
            LastWizardResultsButton.Opacity = 0.5;
        }
    }

    private static (int family, int altNum, string name) GetProfileSortKey(string filename)
    {
        string lower = filename.ToLowerInvariant();
        if (lower == "general.bat") return (1, 0, lower);
        if (lower == "general (alt).bat") return (2, 0, lower);
        var altMatch = System.Text.RegularExpressions.Regex.Match(lower, @"^general\s*\(alt(\d+)\)\.bat$");
        if (altMatch.Success) return (3, int.Parse(altMatch.Groups[1].Value), lower);
        if (lower == "general (fake tls auto).bat") return (4, 0, lower);
        if (lower == "general (fake tls auto alt).bat") return (5, 0, lower);
        var ftaAltMatch = System.Text.RegularExpressions.Regex.Match(lower, @"^general\s*\(fake tls auto alt(\d+)\)\.bat$");
        if (ftaAltMatch.Success) return (6, int.Parse(ftaAltMatch.Groups[1].Value), lower);
        if (lower == "general (simple fake).bat") return (7, 0, lower);
        if (lower == "general (simple fake alt).bat") return (8, 0, lower);
        var sfaAltMatch = System.Text.RegularExpressions.Regex.Match(lower, @"^general\s*\(simple fake alt(\d+)\)\.bat$");
        if (sfaAltMatch.Success) return (9, int.Parse(sfaAltMatch.Groups[1].Value), lower);
        return (10, 0, lower);
    }

    private List<string> BuildProfileCandidateList(IReadOnlyList<string> allProfiles)
    {
        var sorted = allProfiles
            .OrderBy(p => GetProfileSortKey(p).family)
            .ThenBy(p => GetProfileSortKey(p).altNum)
            .ThenBy(p => GetProfileSortKey(p).name)
            .ToList();

        var result = new List<string>();
        string currentProfile = Settings.SelectedProfile;

        if (!string.IsNullOrEmpty(currentProfile))
        {
            var match = sorted.FirstOrDefault(p => p.Equals(currentProfile, StringComparison.OrdinalIgnoreCase));
            if (match != null)
            {
                result.Add(match);
                sorted.Remove(match);
            }
        }

        result.AddRange(sorted);
        return result;
    }


    private void LastWizardResultsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastWizardResults == null || _lastWizardResults.Count == 0)
        {
            return;
        }
        ShowWizardResultsOverlay(_lastWizardResults);
    }

    private bool IsDebugPreviewModeActive()
    {
#if DEBUG
        return _isDebugPreviewMode;
#else
        return false;
#endif
    }

    private void LoadLastAutoPickResults()
    {
        try
        {
            string filePath = AppPaths.LastAutoPickResultsFilePath;
            if (!File.Exists(filePath))
            {
                return;
            }

            string json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<LastAutoPickData>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (data == null || data.Results == null)
            {
                AppLogger.Warning("Saved auto-pick results data is null.");
                return;
            }

            var resultsList = data.Results.Where(r => r != null).ToList();
            if (resultsList.Count == 0)
            {
                AppLogger.Warning("Saved auto-pick results has no valid non-null entries.");
                return;
            }

            // Stage 10 - LoadLastAutoPickResults: load IsTie and WasConfirmationChecked
            // Do not manufacture a winner. Count winners.
            var winners = resultsList.Where(r => r.IsWinner).ToList();

            // Legacy normalization: If there is 1 winner, but another top result has identical measured evidence, treat as tie.
            if (winners.Count == 1)
            {
                var w = winners[0];
                var tiedTop = resultsList.Where(r =>
                    r != w &&
                    r.YouTubeScore == w.YouTubeScore &&
                    r.DiscordScore == w.DiscordScore &&
                    r.SuccessCount == w.SuccessCount &&
                    r.TotalProbes == w.TotalProbes &&
                    r.YouTubeAvailable == w.YouTubeAvailable &&
                    r.DiscordAvailable == w.DiscordAvailable).ToList();

                if (tiedTop.Count > 0)
                {
                    AppLogger.Info("Legacy persistence: found matching top profiles. Converting to tie.");
                    foreach (var r in resultsList)
                    {
                        r.IsWinner = false;
                    }
                    w.IsTie = true;
                    foreach (var t in tiedTop)
                    {
                        t.IsTie = true;
                    }
                    winners.Clear();
                }
            }

            if (winners.Count > 1)
            {
                AppLogger.Warning("Corrupted in-memory data contains more than one IsWinner. Clearing winner flags.");
                foreach (var r in resultsList)
                {
                    r.IsWinner = false;
                }
                winners.Clear();
            }

            _lastWizardResults = resultsList;
            _lastWizardResult = winners.Count == 1 ? winners[0] : null;
            _lastWizardCompletedAt = data.CompletedAt;

            AppLogger.Info($"Last auto-pick results loaded. Profiles: {_lastWizardResults.Count}, completed at: {_lastWizardCompletedAt}, IsWinner count: {winners.Count}");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to load saved auto-pick results: {ex.Message}");

            // Try to delete the corrupted file safely
            try
            {
                if (File.Exists(AppPaths.LastAutoPickResultsFilePath))
                {
                    File.Delete(AppPaths.LastAutoPickResultsFilePath);
                    AppLogger.Info("Corrupted auto-pick results file deleted.");
                }
            }
            catch (Exception delEx)
            {
                AppLogger.Error($"Failed to delete corrupted auto-pick results file: {delEx.Message}");
            }
        }
    }

    private void SaveLastAutoPickResults()
    {
        if (IsDebugPreviewModeActive())
        {
            return;
        }

        if (_lastWizardResults == null || _lastWizardResults.Count == 0)
        {
            return;
        }

        try
        {
            string filePath = AppPaths.LastAutoPickResultsFilePath;
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Copy results list
            var resultsCopy = _lastWizardResults.Select(r => new ProfileCheckResult
            {
                ProfileName = r.ProfileName,
                YouTubeAvailable = r.YouTubeAvailable,
                DiscordAvailable = r.DiscordAvailable,
                YouTubeScore = r.YouTubeScore,
                DiscordScore = r.DiscordScore,
                SuccessCount = r.SuccessCount,
                TotalProbes = r.TotalProbes,
                Errors = r.Errors,
                CheckDuration = r.CheckDuration,
                IsWinner = r.IsWinner,
                IsTie = r.IsTie,
                WasConfirmationChecked = r.WasConfirmationChecked
            }).ToList();

            var data = new LastAutoPickData
            {
                Results = resultsCopy,
                CompletedAt = _lastWizardCompletedAt ?? DateTime.Now
            };

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            // Write safely using a temporary file in the same folder and atomically replacing it
            string tempPath = filePath + ".tmp";
            string json = JsonSerializer.Serialize(data, options);
            File.WriteAllText(tempPath, json, Encoding.UTF8);

            if (File.Exists(filePath))
            {
                File.Replace(tempPath, filePath, null);
            }
            else
            {
                File.Move(tempPath, filePath);
            }

            AppLogger.Info("Last auto-pick results saved to disk.");
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Failed to save last auto-pick results: {ex.Message}");

            // Try to delete the temporary file if it exists
            try
            {
                string tempPath = AppPaths.LastAutoPickResultsFilePath + ".tmp";
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
                // Swallow cleanup failures
            }
        }
    }

    private enum ComparisonSortColumn { Status, YouTube, Discord }
    private enum ComparisonSortDirection { Descending, Ascending }

    private System.Collections.Generic.List<ProfileCheckResult>? _currentComparisonResults = null;
    private DateTime? _currentComparisonCompletedAt = null;
    private ComparisonSortColumn _currentComparisonSortCol = ComparisonSortColumn.Status;
    private ComparisonSortDirection _currentComparisonSortDir = ComparisonSortDirection.Descending;
    private bool _currentComparisonVpnWarning = false;
    private string? _selectedComparisonProfile = null;

    private async Task<OverlayResult> ShowFinalWizardResultsOverlayAsync(System.Collections.Generic.List<ProfileCheckResult> results, ProfileCheckResult? bestResult, ProfileCheckMode mode, bool vpnWasActive)
    {
        _currentComparisonResults = results.ToList();
        _currentComparisonCompletedAt = _lastWizardCompletedAt;
        _currentComparisonSortCol = ComparisonSortColumn.Status;
        _currentComparisonSortDir = ComparisonSortDirection.Descending;
        _currentComparisonVpnWarning = vpnWasActive;

        string title;
        string primaryBtn;
        if (bestResult == null)
        {
            title = "Преимущество не выявлено";
            primaryBtn = "Готово";
        }
        else
        {
            title = bestResult.IsPerfect
                ? (mode == ProfileCheckMode.Accurate ? "Точная проверка завершена" : "Идеальный профиль найден")
                : (mode == ProfileCheckMode.Accurate ? "Точная проверка завершена" : "Найден лучший профиль");
            primaryBtn = "Применить лучший";
        }

        // Two buttons only: "Apply best" / "Готово" (Primary) and optionally "Check more accurately" (Tertiary).
        // "Keep old" is removed. X/close fires the Secondary action (empty text = hidden button)
        // which maps to OverlayResult.Secondary and triggers the restore path.
        string? tertiaryBtn = (mode == ProfileCheckMode.Fast) ? "Проверить точнее" : null;

        var tcs = new TaskCompletionSource<OverlayResult>();

        // secondaryText is empty so the Secondary button is hidden, but onSecondary is wired
        // so that OverlayCloseButton (X) still calls it and resolves Secondary → restore path.
        ShowOverlay(title, "", primaryBtn, "",
            () => tcs.TrySetResult(OverlayResult.Primary),
            () => tcs.TrySetResult(OverlayResult.Secondary),
            null,
            tertiaryBtn,
            () => tcs.TrySetResult(OverlayResult.Tertiary));

        // Match ShowWizardResultsOverlay layout: 800 wide, 640 max height
        if (OverlayCard != null)
        {
            OverlayCard.Width = 800;
            OverlayCard.MaxHeight = 640;
        }
        if (OverlayComparisonHeaderContainer != null) OverlayComparisonHeaderContainer.Visibility = Visibility.Visible;
        if (OverlayComparisonScroll != null) OverlayComparisonScroll.Visibility = Visibility.Visible;
        if (OverlayBody != null) OverlayBody.Visibility = Visibility.Collapsed;
        if (OverlayComparisonScroll != null)
        {
            OverlayComparisonScroll.Visibility = Visibility.Visible;
            OverlayComparisonScroll.Height = ComparisonBodyHeight;
            OverlayComparisonScroll.MaxHeight = ComparisonBodyHeight;
        }

        // Two large buttons centered, same size
        double btnWidth = 200;
        double btnHeight = 44;
        double btnMargin = 6;

        if (OverlayStandardButtons != null)
        {
            OverlayStandardButtons.Orientation = System.Windows.Controls.Orientation.Horizontal;
            OverlayStandardButtons.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            OverlayStandardButtons.Margin = new Thickness(0, 14, 0, 0);
        }

        if (OverlayPrimaryButton != null)
        {
            OverlayPrimaryButton.Margin = new Thickness(btnMargin, 0, btnMargin, 0);
            OverlayPrimaryButton.Padding = new Thickness(0);
            OverlayPrimaryButton.Height = btnHeight;
            OverlayPrimaryButton.FontSize = 14;
            OverlayPrimaryButton.Width = btnWidth;
            OverlayPrimaryButton.Visibility = Visibility.Visible;
            OverlayPrimaryButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        }
        // Secondary button is hidden (empty text), tertiary takes its visual slot
        if (OverlaySecondaryButton != null)
        {
            OverlaySecondaryButton.Visibility = Visibility.Collapsed;
        }
        if (OverlayTertiaryButton != null)
        {
            if (tertiaryBtn != null)
            {
                OverlayTertiaryButton.Margin = new Thickness(btnMargin, 0, btnMargin, 0);
                OverlayTertiaryButton.Padding = new Thickness(0);
                OverlayTertiaryButton.Height = btnHeight;
                OverlayTertiaryButton.FontSize = 14;
                OverlayTertiaryButton.Width = btnWidth;
                OverlayTertiaryButton.Visibility = Visibility.Visible;
                OverlayTertiaryButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            }
            else
            {
                OverlayTertiaryButton.Visibility = Visibility.Collapsed;
            }
        }

        // VPN warning is now rendered inline in the metadata row by RenderComparisonOverlayContent
        RenderComparisonOverlayContent();

        return await tcs.Task;
    }

    private void ShowWizardResultsOverlay(System.Collections.Generic.List<ProfileCheckResult>? results, DateTime? completedAtOverride = null, Action? onBack = null)
    {
        if (results == null)
        {
            ShowOverlay("Последний подбор", "Результатов последнего подбора пока нет.", "ОК", "", () => {});
            return;
        }

        var filteredResults = results.Where(r => r != null).ToList();
        if (filteredResults.Count == 0)
        {
            ShowOverlay("Последний подбор", "Результатов последнего подбора пока нет.", "ОК", "", () => {});
            return;
        }

        _currentComparisonResults = filteredResults;
        _currentComparisonCompletedAt = completedAtOverride ?? _lastWizardCompletedAt;
        _currentComparisonSortCol = ComparisonSortColumn.Status;
        _currentComparisonSortDir = ComparisonSortDirection.Descending;
        _currentComparisonVpnWarning = false;
        _selectedComparisonProfile = null;

        ShowOverlay("Сравнение профилей", "", "Применить профиль", "Назад", async () => {
            if (!string.IsNullOrEmpty(_selectedComparisonProfile))
            {
                var target = HomeProfileComboBox.Items.Cast<ZapretProfileInfo>().FirstOrDefault(p => p.FileName == _selectedComparisonProfile);
                if (target != null)
                {
                    if (target.FileName == _lastAppliedProfile) return;
                    if (target.FileName == "service.bat") return;

                    OverlayPrimaryButton!.IsEnabled = false;
                    OverlayPrimaryButton.Content = "Применение...";

                    try {
                        await ApplyProfileCoreAsync(target, "Сравнение");
                        ClearOverlayState();
                    } catch {
                        OverlayPrimaryButton.Content = "Повторить";
                        OverlayPrimaryButton.IsEnabled = true;
                    }
                }
            }
        }, onBack ?? (() => {}));

        if (OverlayCard != null) {
            OverlayCard.Width = 800;
            OverlayCard.MaxHeight = 640;
        }
        if (OverlayComparisonHeaderContainer != null) OverlayComparisonHeaderContainer.Visibility = Visibility.Visible;
        if (OverlayComparisonScroll != null) OverlayComparisonScroll.Visibility = Visibility.Visible;
        if (OverlayBody != null) OverlayBody.Visibility = Visibility.Collapsed;
        if (OverlayComparisonScroll != null) {
            OverlayComparisonScroll.Visibility = Visibility.Visible;
            OverlayComparisonScroll.Height = ComparisonBodyHeight;
            OverlayComparisonScroll.MaxHeight = ComparisonBodyHeight;
        }
        if (OverlayComparisonHeaderContainer != null) {
            OverlayComparisonHeaderContainer.Visibility = Visibility.Visible;
        }

        double btnWidth = 200;
        double btnHeight = 44;
        double btnMargin = 6;

        if (OverlayStandardButtons != null)
        {
            OverlayStandardButtons.Orientation = System.Windows.Controls.Orientation.Horizontal;
            OverlayStandardButtons.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
            OverlayStandardButtons.Margin = new Thickness(0, 14, 0, 0);
        }

        if (OverlaySecondaryButton != null)
        {
            OverlaySecondaryButton.Margin = new Thickness(btnMargin, 0, btnMargin, 0);
            OverlaySecondaryButton.Padding = new Thickness(0);
            OverlaySecondaryButton.Height = btnHeight;
            OverlaySecondaryButton.FontSize = 14;
            OverlaySecondaryButton.Width = btnWidth;
            OverlaySecondaryButton.Visibility = Visibility.Visible;
            OverlaySecondaryButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        }

        if (OverlayPrimaryButton != null) {
            OverlayPrimaryButton.Margin = new Thickness(btnMargin, 0, btnMargin, 0);
            OverlayPrimaryButton.Padding = new Thickness(0);
            OverlayPrimaryButton.Height = btnHeight;
            OverlayPrimaryButton.FontSize = 14;
            OverlayPrimaryButton.Width = btnWidth;
            OverlayPrimaryButton.Visibility = Visibility.Visible;
            OverlayPrimaryButton.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        }

        if (OverlayTertiaryButton != null)
        {
            OverlayTertiaryButton.Visibility = Visibility.Collapsed;
        }

        RenderComparisonOverlayContent();
    }

    private long GetMeasuredRankingScore(ProfileCheckResult r)
    {
        long score = 0;
        if (r.IsPerfect) score += 10000000;
        score += r.TotalScore * 10000;
        score += r.SuccessCount;
        return score;
    }

    private int GetComparisonStatusRank(ProfileCheckResult p, ProfileCheckResult? trueWinner)
    {
        if (p.IsWinner) return 5;
        if (p.IsTie) return 4;
        if (p.IsPerfect) return 3;
        if (p.IsPartial) return 2;
        return 1;
    }

    private void HandleComparisonSortClick(ComparisonSortColumn col)
    {
        if (_currentComparisonSortCol == col)
        {
            _currentComparisonSortDir = _currentComparisonSortDir == ComparisonSortDirection.Descending ?
                ComparisonSortDirection.Ascending : ComparisonSortDirection.Descending;
        }
        else
        {
            _currentComparisonSortCol = col;
            _currentComparisonSortDir = ComparisonSortDirection.Descending;
        }
        RenderComparisonOverlayContent();
        if (OverlayComparisonScroll != null) OverlayComparisonScroll.ScrollToTop();
    }

    private const double ComparisonProfileCol = 330;
    private const double ComparisonYouTubeCol = 62;
    private const double ComparisonDiscordCol = 62;
    private const double ComparisonTotalCol = 58;
    private const double ComparisonChecksCol = 76;
    private const double ComparisonStatusCol = 110;

    private const int ComparisonVisibleRows = 7;
    private const double ComparisonRowHeight = 44;
    private const double ComparisonRowGap = 8;
    private const double ComparisonBodyHeight = (ComparisonVisibleRows * ComparisonRowHeight) + ((ComparisonVisibleRows - 1) * ComparisonRowGap);
    private const double ComparisonHeaderNudgeX = 5;

    private const double ComparisonInnerWidth = 698;
    private const double ComparisonRowHorizontalPadding = 14;
    private const double ComparisonRowBorderThickness = 1;
    private const double ComparisonRowOuterWidth = ComparisonInnerWidth + (ComparisonRowHorizontalPadding * 2) + (ComparisonRowBorderThickness * 2);
    private const double ComparisonScrollbarGutter = 22;
    private const double ComparisonScrollWidth = ComparisonRowOuterWidth + ComparisonScrollbarGutter;

    private static void ApplyComparisonColumns(Grid grid, bool includeScrollbarGutter)
    {
        grid.ColumnDefinitions.Clear();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ComparisonProfileCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ComparisonYouTubeCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ComparisonDiscordCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ComparisonTotalCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ComparisonChecksCol) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ComparisonStatusCol) });

        if (includeScrollbarGutter)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ComparisonScrollbarGutter) });
    }

    private void RenderComparisonOverlayContent()
    {
        if (_currentComparisonResults == null) return;
        var displayTime = _currentComparisonCompletedAt;

        if (OverlayComparisonContainer != null) OverlayComparisonContainer.Children.Clear();
        if (OverlayComparisonHeaderContainer != null) 
        {
            OverlayComparisonHeaderContainer.Children.Clear();
            OverlayComparisonHeaderContainer.Width = ComparisonScrollWidth;
            OverlayComparisonHeaderContainer.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        }
        if (OverlayComparisonScroll != null)
        {
            OverlayComparisonScroll.Width = ComparisonScrollWidth;
            OverlayComparisonScroll.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        }

        var validResults = _currentComparisonResults.Where(r => r != null).ToList();
        if (validResults.Count == 0) return;

        var originalSorted = validResults.OrderByDescending(r => r.TotalScore).ToList();
        var trueWinner = originalSorted.FirstOrDefault(r => r.IsWinner);

        System.Collections.Generic.IEnumerable<ProfileCheckResult> sortedEnumerable;
        if (_currentComparisonSortCol == ComparisonSortColumn.Status)
        {
            if (_currentComparisonSortDir == ComparisonSortDirection.Descending)
                sortedEnumerable = validResults.OrderByDescending(r => GetComparisonStatusRank(r, trueWinner)).ThenByDescending(r => r.TotalScore).ThenBy(r => r.ProfileName ?? string.Empty);
            else
                sortedEnumerable = validResults.OrderBy(r => GetComparisonStatusRank(r, trueWinner)).ThenByDescending(r => r.TotalScore).ThenBy(r => r.ProfileName ?? string.Empty);
        }
        else if (_currentComparisonSortCol == ComparisonSortColumn.YouTube)
        {
            if (_currentComparisonSortDir == ComparisonSortDirection.Descending)
                sortedEnumerable = validResults.OrderByDescending(r => r.YouTubeScore).ThenByDescending(r => r.TotalScore).ThenByDescending(r => GetComparisonStatusRank(r, trueWinner)).ThenBy(r => r.ProfileName ?? string.Empty);
            else
                sortedEnumerable = validResults.OrderBy(r => r.YouTubeScore).ThenByDescending(r => r.TotalScore).ThenByDescending(r => GetComparisonStatusRank(r, trueWinner)).ThenBy(r => r.ProfileName ?? string.Empty);
        }
        else
        {
            if (_currentComparisonSortDir == ComparisonSortDirection.Descending)
                sortedEnumerable = validResults.OrderByDescending(r => r.DiscordScore).ThenByDescending(r => r.TotalScore).ThenByDescending(r => GetComparisonStatusRank(r, trueWinner)).ThenBy(r => r.ProfileName ?? string.Empty);
            else
                sortedEnumerable = validResults.OrderBy(r => r.DiscordScore).ThenByDescending(r => r.TotalScore).ThenByDescending(r => GetComparisonStatusRank(r, trueWinner)).ThenBy(r => r.ProfileName ?? string.Empty);
        }

        var topProfiles = sortedEnumerable.ToList();

        var headerStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 16) };
        if (displayTime.HasValue)
        {
            var dateBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 0),
                BorderThickness = new Thickness(1)
            };
            dateBadge.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            dateBadge.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var dateStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };

            var textPart1 = new TextBlock { Text = "Последняя проверка ", FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            textPart1.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            var textDate = new TextBlock { Text = displayTime.Value.ToString("dd.MM.yyyy"), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
            textDate.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var textTime = new TextBlock { Text = displayTime.Value.ToString("HH:mm"), FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            textTime.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            dateStack.Children.Add(textPart1);
            dateStack.Children.Add(textDate);
            dateStack.Children.Add(textTime);

            dateBadge.Child = dateStack;
            headerStack.Children.Add(dateBadge);

            var countText = new TextBlock
            {
                Text = $"Показаны все результаты: {validResults.Count}",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            countText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            headerStack.Children.Add(countText);

            // Inline VPN warning badge — only rendered when _currentComparisonVpnWarning is set
            if (_currentComparisonVpnWarning)
            {
                var vpnBadge = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#20FF9800")!,
                    BorderThickness = new Thickness(1),
                    BorderBrush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#60FF9800")!
                };

                var vpnText = new TextBlock
                {
                    Text = "⚠ VPN был включён",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                };
                vpnText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");

                vpnBadge.Child = vpnText;
                headerStack.Children.Add(vpnBadge);
            }
        }

        var headerBorder = new Border { 
            Width = ComparisonRowOuterWidth,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 8), 
            Padding = new Thickness(ComparisonRowHorizontalPadding, 0, ComparisonRowHorizontalPadding, 0),
            BorderThickness = new Thickness(ComparisonRowBorderThickness),
            BorderBrush = System.Windows.Media.Brushes.Transparent
        };
        var headerGrid = new Grid();
        ApplyComparisonColumns(headerGrid, includeScrollbarGutter: true);

        string[] headers = { "ПРОФИЛЬ", "", "", "ИТОГ", "ПРОВЕРКИ", "СТАТУС" };
        for (int i = 0; i < headers.Length; i++)
        {
            UIElement elementToAdd;

            if (i == 1 || i == 2)
            {
                var colGrid = new Grid { Background = System.Windows.Media.Brushes.Transparent };
                
                var viewbox = new System.Windows.Controls.Viewbox { Width = 14, Height = 14, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
                var path = new System.Windows.Shapes.Path { Stretch = System.Windows.Media.Stretch.Uniform };
                path.SetResourceReference(System.Windows.Shapes.Path.FillProperty, "TextMutedBrush");
                path.Data = TryFindResource(i == 1 ? "IconYoutube" : "IconDiscord") as System.Windows.Media.Geometry;
                viewbox.Child = path;
                colGrid.Children.Add(viewbox);

                elementToAdd = colGrid;
            }
            else if (i == 5)
            {
                var colGrid = new Grid { Background = System.Windows.Media.Brushes.Transparent };
                
                var txt = new TextBlock
                {
                    Text = headers[i],
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center
                };
                txt.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                colGrid.Children.Add(txt);

                elementToAdd = colGrid;
            }
            else
            {
                var txt = new TextBlock
                {
                    Text = headers[i],
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = (i > 0) ? System.Windows.HorizontalAlignment.Center : System.Windows.HorizontalAlignment.Left,
                    TextAlignment = (i > 0) ? TextAlignment.Center : TextAlignment.Left
                };
                txt.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
                elementToAdd = txt;
            }

            elementToAdd.RenderTransform = new System.Windows.Media.TranslateTransform(ComparisonHeaderNudgeX, 0);

            Grid.SetColumn(elementToAdd, i);
            headerGrid.Children.Add(elementToAdd);
        }
        
        headerBorder.Child = headerGrid;

        if (OverlayComparisonHeaderContainer != null)
        {
            if (displayTime.HasValue) OverlayComparisonHeaderContainer.Children.Add(headerStack);
            OverlayComparisonHeaderContainer.Children.Add(headerBorder);
        }

        int rowIndex = 0;

        foreach (var p in topProfiles)
        {
            if (p == null) continue;
            bool isLast = (rowIndex == topProfiles.Count - 1);
            rowIndex++;

            bool isSelected = (_selectedComparisonProfile == p.ProfileName);
            bool isRowWinner = (p == trueWinner);

            var border = new Border
            {
                Height = ComparisonRowHeight,
                Width = ComparisonRowOuterWidth,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                UseLayoutRounding = true,
                SnapsToDevicePixels = true,
                BorderThickness = new Thickness(ComparisonRowBorderThickness),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(ComparisonRowHorizontalPadding, 0, ComparisonRowHorizontalPadding, 0),
                Margin = new Thickness(0, 0, 0, isLast ? 0 : ComparisonRowGap)
            };

            bool isHovered = false;
            bool isPressed = false;

            void UpdateRowVisuals()
            {
                if (isPressed)
                {
                    border.SetResourceReference(Border.BackgroundProperty, "BorderBrush");
                    border.SetResourceReference(Border.BorderBrushProperty, isSelected ? "PrimaryBrush" : "BorderBrush");
                }
                else if (isSelected)
                {
                    border.SetResourceReference(Border.BackgroundProperty, "RowHoverBrush");
                    border.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
                }
                else if (isHovered)
                {
                    border.SetResourceReference(Border.BackgroundProperty, "NavHoverBgBrush");
                    border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                }
                else
                {
                    border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
                    border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
                }
            }

            border.MouseEnter += (s, e) =>
            {
                isHovered = true;
                border.Cursor = System.Windows.Input.Cursors.Hand;
                UpdateRowVisuals();
            };

            border.MouseLeave += (s, e) =>
            {
                isHovered = false;
                isPressed = false;
                border.Cursor = System.Windows.Input.Cursors.Arrow;
                UpdateRowVisuals();
            };

            border.MouseLeftButtonDown += (s, e) =>
            {
                isPressed = true;
                border.CaptureMouse();
                UpdateRowVisuals();
            };

            border.MouseLeftButtonUp += (s, e) =>
            {
                if (isPressed)
                {
                    isPressed = false;
                    border.ReleaseMouseCapture();
                    UpdateRowVisuals();
                    _selectedComparisonProfile = isSelected ? null : p.ProfileName;
                    RenderComparisonOverlayContent();
                }
            };

            border.LostMouseCapture += (s, e) =>
            {
                isPressed = false;
                UpdateRowVisuals();
            };

            UpdateRowVisuals();

            var grid = new Grid();
            ApplyComparisonColumns(grid, includeScrollbarGutter: false);

            var nameGrid = new Grid { VerticalAlignment = VerticalAlignment.Center };
            nameGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });

            var nameText = new TextBlock
            {
                Text = p.ProfileName ?? string.Empty,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(nameText, 0);
            nameGrid.Children.Add(nameText);

            if (p.ProfileName == _lastAppliedProfile)
            {
                var currentBadge = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 3, 10, 3),
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    BorderThickness = new Thickness(0)
                };
                currentBadge.SetResourceReference(Border.BackgroundProperty, "VersionBadgeBackgroundBrush");

                var currentText = new TextBlock
                {
                    Text = "Текущий",
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold
                };
                currentText.SetResourceReference(TextBlock.ForegroundProperty, "VersionBadgeTextBrush");
                currentBadge.Child = currentText;

                Grid.SetColumn(currentBadge, 1);
                nameGrid.Children.Add(currentBadge);
            }

            Grid.SetColumn(nameGrid, 0);
            grid.Children.Add(nameGrid);

            int ytVal = p.YouTubeScore * 5;
            var ytText = new TextBlock
            {
                Text = ytVal.ToString(),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            if (ytVal == 0) ytText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            else if (ytVal >= 40) ytText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
            else ytText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            Grid.SetColumn(ytText, 1);
            grid.Children.Add(ytText);

            int dsVal = p.DiscordScore * 5;
            var dsText = new TextBlock
            {
                Text = dsVal.ToString(),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            if (dsVal == 0) dsText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            else if (dsVal >= 40) dsText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
            else dsText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            Grid.SetColumn(dsText, 2);
            grid.Children.Add(dsText);

            var scoreText = new TextBlock
            {
                Text = (p.TotalScore * 5).ToString(),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            scoreText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            Grid.SetColumn(scoreText, 3);
            grid.Children.Add(scoreText);

            var checksText = new TextBlock
            {
                Text = p.StabilityText ?? string.Empty,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            checksText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            Grid.SetColumn(checksText, 4);
            grid.Children.Add(checksText);

            var statusBadge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Width = 90,
                Height = 22,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            string statusTextStr = isRowWinner
                ? "Лучший"
                : p.DisplayStatusRu;

            var statusText = new TextBlock
            {
                Text = statusTextStr,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                LineHeight = 14,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, -1, 0, 0),
            };

            if (isRowWinner) statusBadge.SetResourceReference(Border.BackgroundProperty, "PrimaryBrush");
            else if (p.IsTie) statusBadge.SetResourceReference(Border.BackgroundProperty, "IndigoBrush");
            else if (p.IsPerfect) statusBadge.SetResourceReference(Border.BackgroundProperty, "StatusOnBrush");
            else if (p.IsPartial) statusBadge.SetResourceReference(Border.BackgroundProperty, "WarningBrush");
            else statusBadge.SetResourceReference(Border.BackgroundProperty, "DangerBrush");

            statusBadge.Child = statusText;
            Grid.SetColumn(statusBadge, 5);
            grid.Children.Add(statusBadge);

            border.Child = grid;
            if (OverlayComparisonContainer != null)
            {
                OverlayComparisonContainer.Children.Add(border);
            }
        }

        if (OverlayComparisonScroll != null)
        {
            OverlayComparisonScroll.ScrollToTop();
        }

        if (OverlayPrimaryButton != null && OverlayPrimaryButton.Content != null)
        {
            string contentStr = OverlayPrimaryButton.Content.ToString() ?? "";
            if (contentStr != "Применение..." && contentStr != "Повторить")
            {
                if (string.IsNullOrEmpty(_selectedComparisonProfile))
                {
                    OverlayPrimaryButton.IsEnabled = false;
                    OverlayPrimaryButton.Content = "Применить профиль";
                }
                else if (_selectedComparisonProfile == _lastAppliedProfile || _selectedComparisonProfile == "service.bat")
                {
                    OverlayPrimaryButton.IsEnabled = false;
                    OverlayPrimaryButton.Content = "Уже выбран";
                }
                else
                {
                    OverlayPrimaryButton.IsEnabled = true;
                    OverlayPrimaryButton.Content = "Применить профиль";
                }
            }
        }
    }

    private void UpdateWizardStatus(double percent, string step, List<ProfileCheckResult> results, int totalCount)
    {
        InstallProgressBar.Value = percent;
        InstallPercentText.Text = $"{(int)percent}%";

        if (_suppressWizardFooterNoise)
        {
            string profileName = Settings.SelectedProfile;
            if (!string.IsNullOrEmpty(profileName) && totalCount > 0)
            {
                int currentIndex = Math.Min(results.Count + 1, totalCount);
                string shortStep = step.Contains('\n') ? step.Substring(step.LastIndexOf('\n') + 1) : step;
                shortStep = shortStep.TrimEnd('.').ToLower();
                if (string.IsNullOrWhiteSpace(shortStep)) shortStep = "проверка";

                SetFooterMessage($"Профиль {currentIndex} из {totalCount}: {profileName} · {shortStep}", FooterMessageKind.Info, highlight: true, suppressPulse: true);
            }
        }

        string etaText = "";
        string dynamicText = "";

        int completedCount = results.Count;
        int remainingCount = totalCount - completedCount;

        if (percent >= 90)
        {
            etaText = "";
            if (percent < 100)
            {
                dynamicText = "Восстанавливаем исходный профиль…";
            }
        }
        else
        {
            if (completedCount < 2)
            {
                etaText = " — Оцениваем время…";
                dynamicText = "Оцениваем время…";
            }
            else
            {
                double avgMs = results.Average(r => r.CheckDuration.TotalMilliseconds);
                double estRemainingMs = avgMs * remainingCount;
                double estSeconds = estRemainingMs / 1000.0;
                string fullTooltip;

                if (estSeconds <= 15)
                {
                    etaText = " — Осталось несколько секунд";
                    dynamicText = "Осталось несколько секунд";
                    fullTooltip = "Осталось несколько секунд";
                }
                else if (estSeconds < 60)
                {
                    string sec = $"{(int)Math.Round(estSeconds)} сек";
                    etaText = $" — Осталось ~ {sec}";
                    dynamicText = $"Осталось ~ {sec}";
                    fullTooltip = $"Осталось ~ {sec}";
                }
                else
                {
                    int mins = (int)(estSeconds / 60);
                    int secs = (int)Math.Round(estSeconds % 60);
                    string timeStr = secs > 0 ? $"{mins} мин {secs} сек" : $"{mins} мин";
                    etaText = $" — Осталось ~ {timeStr}";
                    dynamicText = $"Осталось ~ {timeStr}";
                    fullTooltip = $"Осталось ~ {timeStr}";
                }

                if (LeftProfileCheckingTimeText != null)
                {
                    LeftProfileCheckingTimeText.ToolTip = fullTooltip;
                }
            }
        }

        InstallStepText.Text = step + etaText;

        if (_autopickStartTime.HasValue)
        {
            HeroHelperText.Text = "Ищем лучший профиль";
            _lastHelperText = "Ищем лучший профиль";
            UpdateLeftProfileBusyDetails(etaText: percent >= 100 ? "" : dynamicText);
            if (percent < 100 && LeftProfileCheckingTimeText != null)
            {
                LeftProfileCheckingTimeText.ToolTip = dynamicText.Contains("Оцениваем время") ? null : LeftProfileCheckingTimeText.ToolTip;
            }
        }
        if (percent >= 100)
        {
            UpdateLeftProfileBusyDetails(etaText: "");
            if (LeftProfileCheckingTimeText != null) LeftProfileCheckingTimeText.ToolTip = null;
        }
    }

    private void ApplyProfileCardState()
    {
        if (LeftProfileNormalPanel == null || LeftProfileBusyPanel == null || LeftProfileNotInstalledPanel == null || BestProfileButton == null || OperationProgressCard == null || LeftProfileNotInstalledText == null)
            return;

        // 1. Determine profile left-side panel visibility
        if (_isWizardRunning)
        {
            LeftProfileNormalPanel.Visibility = Visibility.Collapsed;
            LeftProfileBusyPanel.Visibility = Visibility.Visible;
            LeftProfileNotInstalledPanel.Visibility = Visibility.Collapsed;
        }
        else if (!Settings.IsZapretInstalled)
        {
            LeftProfileNormalPanel.Visibility = Visibility.Collapsed;
            LeftProfileBusyPanel.Visibility = Visibility.Collapsed;
            LeftProfileNotInstalledPanel.Visibility = Visibility.Visible;

            if (_installCts != null)
            {
                LeftProfileNotInstalledText.Text = "Устанавливаем zapret...";
            }
            else
            {
                LeftProfileNotInstalledText.Text = "Сначала установите zapret";
            }
        }
        else
        {
            LeftProfileNormalPanel.Visibility = Visibility.Visible;
            LeftProfileBusyPanel.Visibility = Visibility.Collapsed;
            LeftProfileNotInstalledPanel.Visibility = Visibility.Collapsed;
        }

        // 2. Manage right-side action buttons during installation/wizard
        if (_isWizardRunning)
        {
            BestProfileButton.Visibility = Visibility.Collapsed;
            OperationProgressCard.Visibility = Visibility.Visible;
            InstallProgressPanel.Visibility = Visibility.Visible;
        }
        else if (_installCts != null)
        {
            BestProfileButton.Visibility = Visibility.Visible;
            BestProfileButton.IsEnabled = false;
            OperationProgressCard.Visibility = Visibility.Collapsed;
        }
        else
        {
            BestProfileButton.Visibility = Visibility.Visible;
            BestProfileButton.IsEnabled = Settings.IsZapretInstalled;
            OperationProgressCard.Visibility = Visibility.Collapsed;
        }
    }

    private void SetLeftProfileBusyState(bool isBusy)
    {
        ApplyProfileCardState();
    }

    private void UpdateLeftProfileBusyDetails(string? profileName = null, string? etaText = null)
    {
        if (profileName != null && LeftProfileCheckingNameText != null)
        {
            LeftProfileCheckingNameText.Text = profileName;
        }
        if (etaText != null && LeftProfileCheckingTimeText != null)
        {
            LeftProfileCheckingTimeText.Text = etaText;
        }
    }

    private async Task RestoreBestProfileWizardOriginalState(string? profile, bool wasRunning)
    {
        SetLeftProfileBusyState(false);
        UpdateLeftProfileBusyDetails("", "Оцениваем время…");

        AppLogger.Info($"Восстановление состояния: {profile ?? "null"}, был запущен={wasRunning}");
        Settings.SelectedProfile = profile ?? "";
        SafeSaveSettings();

        try
        {
            if (!string.IsNullOrEmpty(profile))
            {
                await _serviceManager.ReinstallAsync();
                if (wasRunning)
                    await _serviceManager.StartAsync();
                else
                    await _serviceManager.StopAsync();
            }
            else
            {
                await _serviceManager.StopAsync();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при восстановлении исходного состояния службы: {ex.Message}");
        }

        if (profile != null)
        {
            SyncProfileComboBoxes(profile);
        }
    }

    private async Task<bool> IsServiceAvailableInternalAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = GitHubReleaseService.CreateHttpClient();
            var response = await client.GetAsync(url, cts.Token);
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private async Task<(bool available, int score, string errors, int successCount, int totalProbes)> PerformMultiProbeCheckAsync(
        string name,
        string[] urls,
        int[] scores,
        int timeoutMs,
        int probesCount,
        CancellationToken token)
    {
        int totalWeightedScore = 0;
        bool anySuccess = false;
        List<string> errorsList = new();
        int totalSuccessCount = 0;
        int totalProbesCount = urls.Length * probesCount;
        int[] urlSuccessCounts = new int[urls.Length];

        async Task<(int index, bool success, string? error)> ExecuteProbeAsync(string url, int urlIndex, int attempt)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                cts.CancelAfter(timeoutMs);
                using var client = GitHubReleaseService.CreateHttpClient();
                var response = await client.GetAsync(url, cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return (urlIndex, true, null);
                }
                else
                {
                    return (urlIndex, false, $"{name} url {urlIndex + 1} probe {attempt + 1} failed: {response.StatusCode}");
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                return (urlIndex, false, $"{name} url {urlIndex + 1} probe {attempt + 1} failed: Timeout");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (urlIndex, false, $"{name} url {urlIndex + 1} probe {attempt + 1} error: {ex.Message}");
            }
        }

        for (int p = 0; p < probesCount; p++)
        {
            var tasks = new Task<(int index, bool success, string? error)>[urls.Length];
            for (int i = 0; i < urls.Length; i++)
            {
                tasks[i] = ExecuteProbeAsync(urls[i], i, p);
            }

            var probeResults = await Task.WhenAll(tasks);

            foreach (var r in probeResults.OrderBy(x => x.index))
            {
                if (r.success)
                {
                    urlSuccessCounts[r.index]++;
                    totalSuccessCount++;
                    anySuccess = true;
                }
                else if (r.error != null)
                {
                    errorsList.Add(r.error);
                }
            }
        }

        for (int i = 0; i < urls.Length; i++)
        {
            if (urlSuccessCounts[i] > 0)
            {
                double ratio = (double)urlSuccessCounts[i] / probesCount;
                totalWeightedScore += (int)(scores[i] * ratio);
            }
        }

        return (anySuccess, totalWeightedScore, string.Join("; ", errorsList.Distinct().Take(3)), totalSuccessCount, totalProbesCount);
    }

    private void FixAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked FixAllButton_Click while auto-pick is active.");
            return;
        }
        TriggerFixAll();
    }

    private async void TriggerFixAll()
    {
        FixAllButton.IsEnabled = false;
        try
        {
            await RunFixEverythingAsync();
        }
        finally
        {
            FixAllButton.IsEnabled = true;
        }
    }

    private async Task RunFixEverythingAsync()
    {
        AppLogger.Info("=== Починить всё ===");
        bool success = false;

        try
        {
            if (!EnsureAdmin())
            {
                AppLogger.Warning("Починить всё: нет прав администратора.");
                return;
            }

            AppLogger.Info("Диагностика состояния...");

            bool filesExist = await Task.Run(() => _installer.ValidateLocalInstall());
            bool winwsExists = File.Exists(Path.Combine(AppPaths.ZapretDirectory, "bin", "winws.exe"));
            bool serviceBatExists = File.Exists(Path.Combine(AppPaths.ZapretDirectory, "service.bat"));
            bool profileSelected = !string.IsNullOrEmpty(Settings.SelectedProfile);
            var status = await Task.Run(() => _statusService.GetStatus());
            bool winwsRunning = _serviceManager.IsWinwsProcessRunning();
            bool vpnDetected = IsPossibleVpnActive();

            bool needsFileRepair = !Settings.IsZapretInstalled || !filesExist || !winwsExists || !serviceBatExists;
            bool needsServiceInstall = !status.Exists;
            bool needsServiceRefresh = status.Exists;
            bool needsCleanup = winwsRunning || status.IsRunning || needsFileRepair || needsServiceInstall || needsServiceRefresh;

            List<string> plannedActions = new();

            if (needsCleanup)
                plannedActions.Add("• Очистить зависшие процессы и WinDivert");

            if (needsFileRepair)
                plannedActions.Add("• Установить zapret заново");

            if (needsServiceInstall || needsServiceRefresh)
            {
                if (needsServiceInstall)
                    plannedActions.Add("• Установить службу zapret");
                else
                    plannedActions.Add("• Обновить службу под текущий профиль");

                plannedActions.Add("• Подготовить текущий профиль");
            }

            if (plannedActions.Count == 0 && !vpnDetected)
            {
                AppLogger.Info("Проблем не найдено. zapret готов к работе.");
                UpdateInstallCard();
                RefreshExpertPage();
                await CheckStatusOnStartup();
                SetFooterMessage("Проблем не найдено. zapret готов к работе", FooterMessageKind.Success, highlight: true);
                return;
            }

            // Log technical details for developers
            StringBuilder logSb = new();
            logSb.AppendLine("Планируемые действия:");
            foreach (var action in plannedActions) logSb.AppendLine(action);
            if (vpnDetected) logSb.AppendLine("- Обнаружен VPN");
            if (!profileSelected) logSb.AppendLine("- Профиль не выбран");
            AppLogger.Info(logSb.ToString());

            // Part A & B: Show simplified in-app overlay for confirmation
            string bodyText = "Приложение очистит зависшие процессы, подготовит службу zapret и применит текущий профиль. Обход не включится автоматически.";
            if (vpnDetected) bodyText += "\n\n⚠ Обнаружен включённый VPN, он может мешать работе обхода.";
            if (needsServiceInstall && !profileSelected) bodyText += "\n\n⚠ Внимание: Служба не установлена, а профиль не выбран. Ремонт не сможет восстановить службу.";
            else if (!profileSelected) bodyText += "\n\n⚠ Профиль не выбран. Нужно будет выбрать его в разделе «Эксперт».";

            var confirmResult = await ShowOverlayAsync(
                "Починить всё?",
                bodyText,
                "Починить",
                "Отмена"
            );

            if (confirmResult != OverlayResult.Primary)
            {
                AppLogger.Info("Починить всё: пользователь отменил ремонт.");
                return;
            }

            AppLogger.Info("Начало выполнения ремонта...");

            OperationProgressCard.Visibility = Visibility.Visible;
            InstallProgressPanel.Visibility = Visibility.Visible;
            CancelOperationButton.Visibility = Visibility.Collapsed;
            InstallStepText.Text = "Ремонт: Подготовка...";
            InstallProgressBar.IsIndeterminate = true;
            InstallPercentText.Text = "";

            AppLogger.Info("Действие: Очистка...");
            InstallStepText.Text = "Ремонт: Очистка процессов...";
            await _serviceManager.PrepareFlowsealLikeEnvironmentAsync("ремонт");

            if (needsFileRepair)
            {
                AppLogger.Info("Действие: Переустановка файлов...");
                InstallStepText.Text = "Ремонт: Загрузка и установка файлов...";

                var progress = new Progress<InstallProgressInfo>(info =>
                {
                    Dispatcher.Invoke(() => {
                        if (!string.IsNullOrEmpty(info.Step))
                            InstallStepText.Text = $"Ремонт: {info.Step}";

                        if (info.Percent.HasValue)
                        {
                            InstallProgressBar.IsIndeterminate = false;
                            InstallProgressBar.Value = info.Percent.Value;
                            InstallPercentText.Text = $"{info.Percent.Value}%";
                        }
                    });
                });

                var fileResult = await _installer.InstallAsync(progress, CancellationToken.None);
                if (!fileResult.Success)
                {
                    AppLogger.Error($"Ошибка при установке файлов: {fileResult.ErrorMessage}");
                    if (fileResult.ErrorMessage?.Contains("WinDivert64.sys", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        ShowOverlay(
                            "Файл занят",
                            "Не удалось заменить системный файл zapret. Обычно это происходит, если драйвер ещё используется Windows или его удерживает антивирус. Перезагрузите компьютер и запустите ремонт снова.",
                            "Понятно",
                            "",
                            () => { },
                            closeBehavesAsPrimary: true
                        );
                    }
                    else
                    {
                        ShowOverlay(
                            "Не удалось восстановить файлы",
                            "Ремонт не смог восстановить нужные файлы zapret. Попробуйте запустить ремонт ещё раз.",
                            "Понятно",
                            "",
                            () => { },
                            closeBehavesAsPrimary: true
                        );
                    }
                    return;
                }
            }

            if (needsServiceInstall || needsServiceRefresh)
            {
                if (profileSelected)
                {
                    AppLogger.Info("Действие: Переустановка службы...");
                    InstallStepText.Text = "Ремонт: Обновление службы...";
                    InstallProgressBar.IsIndeterminate = true;
                    InstallPercentText.Text = "";

                    var svcResult = await _serviceManager.ReinstallAsync();
                    if (!svcResult.Success)
                    {
                        AppLogger.Warning($"Ошибка при настройке службы: {svcResult.Message}");
                        ShowOverlay(
                            "Служба не настроена",
                            "Файлы zapret восстановлены, но службу не удалось подготовить. Попробуйте нажать «Починить всё» ещё раз.",
                            "Понятно",
                            "",
                            () => { },
                            closeBehavesAsPrimary: true
                        );
                        return;
                    }
                }
                else if (needsServiceInstall)
                {
                    AppLogger.Error("Ремонт службы невозможен: профиль не выбран.");
                    ShowOverlay(
                        "Профиль не выбран",
                        "Файлы восстановлены, но для установки службы zapret необходимо выбрать профиль. Перейдите в раздел «Эксперт» и выберите профиль, затем повторите ремонт.",
                        "Понятно",
                        "",
                        () => { },
                        null,
                        closeBehavesAsPrimary: true
                    );
                    return;
                }
                else
                {
                    AppLogger.Info("Пропуск настройки службы: профиль не выбран.");
                }
            }

            AppLogger.Info("Ремонт завершён успешно.");

            // Part C: Communicate repair completion without layout jump
            InstallProgressBar.IsIndeterminate = false;
            InstallProgressBar.Value = 100;
            InstallPercentText.Text = "100%";
            InstallStepText.Text = "Ремонт завершён. Можно включить обход.";

            success = true;

            // Give user time to see the success state
            await Task.Delay(2500);
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Критическая ошибка при ремонте: {ex.Message}");
            ShowOverlay(
                "Ремонт не завершился",
                "Не удалось завершить ремонт. Попробуйте перезагрузить компьютер и запустить ремонт снова.",
                "Понятно",
                "",
                () => { },
                closeBehavesAsPrimary: true
            );
            SetFooterMessage("Не удалось выполнить восстановление.", FooterMessageKind.Error, highlight: true);
        }
        finally
        {
            OperationProgressCard.Visibility = Visibility.Collapsed;
            CancelOperationButton.Visibility = Visibility.Collapsed;
            InstallProgressPanel.Visibility = Visibility.Hidden;
            UpdateInstallCard();
            RefreshExpertPage();
            await CheckStatusOnStartup();

            if (success)
            {
                SetFooterMessage("Ремонт завершён. Можно включить обход", FooterMessageKind.Success, highlight: true);
            }
        }
    }

    private async void ReinstallServiceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isWizardRunning)
        {
            AppLogger.Info("Blocked ReinstallServiceButton_Click while auto-pick is active.");
            return;
        }
        AppLogger.Info("Запрошена переустановка службы.");

        if (!EnsureAdmin())
        {
            return;
        }

        ShowOverlay(
            "Применить профиль?",
            "Приложение пересоздаст службу zapret с выбранным профилем. Обход не включится автоматически.",
            "Применить",
            "Отмена",
            async () =>
            {
                ReinstallServiceButton.IsEnabled = false;
                AppLogger.Info("Пользователь подтвердил применение профиля.");

                try
                {
                    var result = await _serviceManager.ReinstallAsync();

                    if (result.Success)
                    {
                        SetFooterMessage("Профиль применён", FooterMessageKind.Success, highlight: true);
                        if (HomeProfileComboBox.SelectedItem is ZapretProfileInfo p)
                        {
                            _lastAppliedProfile = p.FileName;
                        }
                        _ = ExecuteNetworkDiagnosticAsync(false);
                    }
                    else
                    {
                        AppLogger.Warning($"Не удалось применить профиль: {result.Message}");
                        ShowOverlay(
                            "Не удалось применить профиль",
                            "Не удалось подготовить службу zapret с выбранным профилем. Попробуйте нажать «Починить всё».",
                            "Понятно",
                            "",
                            () => { },
                            closeBehavesAsPrimary: true
                        );
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Ошибка при применении профиля: {ex.Message}");
                    ShowOverlay(
                        "Ошибка применения профиля",
                        "Не удалось завершить применение профиля. Попробуйте ещё раз.",
                        "Понятно",
                        "",
                        () => { },
                        closeBehavesAsPrimary: true
                    );
                }
                finally
                {
                    ReinstallServiceButton.IsEnabled = true;

                    // Strengthen state refresh: call all relevant UI update methods
                    await CheckStatusOnStartup();
                    RefreshExpertPage();
                    UpdateInstallCard();
                }
            }
        );
    }

    private void PlaceholderButton_Click(object sender, RoutedEventArgs e)
    {
        SetFooterMessage("Эта функция появится позже", FooterMessageKind.Info, highlight: true);
    }

    private void CopyReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var allEntries = AppLogger.GetRecentEntries();
            int totalErrorCount = allEntries.Count(e => e.Contains("[ERR ]"));

            var displayList = GetSanitizedJournalEntries(allEntries);

            var sb = new StringBuilder();
            sb.AppendLine("=== Zapret Kmestu — Отчёт ===");
            sb.AppendLine($"Версия приложения : v0.1 beta");
            sb.AppendLine($"Платформа         : .NET 8 · WPF");
            sb.AppendLine($"Время отчёта      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Ошибок            : {totalErrorCount}");
            sb.AppendLine();
            sb.AppendLine("--- События ---");

            foreach (var item in displayList)
            {
                sb.AppendLine($"[{item.Time}] [{item.Severity}] {item.Message}");
            }

            System.Windows.Clipboard.SetText(sb.ToString());
            AppLogger.Info("Отчёт скопирован в буфер обмена.");
            SetFooterMessage("Отчёт скопирован в буфер обмена", FooterMessageKind.Success, highlight: true);
        }
        catch (Exception ex)
        {
            SetFooterMessage("Не удалось скопировать отчёт.", FooterMessageKind.Error, highlight: true);
            AppLogger.Warning($"Не удалось скопировать отчёт: {ex.Message}");
        }
    }

    private bool IsPossibleVpnActive()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            var keywords = new[] {
                "vpn", "wireguard", "wg", "openvpn", "tap", "tun", "wintun",
                "tailscale", "zerotier", "outline", "amnezia", "proton",
                "nord", "mullvad", "clash", "sing-box", "tun2socks"
            };

            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up)
                {
                    string name = ni.Name.ToLower();
                    string desc = ni.Description.ToLower();
                    if (keywords.Any(k => name.Contains(k) || desc.Contains(k)))
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"VPN: не удалось проверить ({ex.Message})");
        }
        return false;
    }

    private async Task CheckServiceAvailabilityAsync(string name, string url, TextBlock uiElement, bool isManualCheck = true)
    {
        uiElement.Text = "Проверяется…";
        uiElement.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");

        if (name == "YouTube")
        {
            _youtubeProbeState = DiagnosticProbeState.Checking;
            _lastYouTubeDurationMs = null;
        }
        else if (name == "Discord")
        {
            _discordProbeState = DiagnosticProbeState.Checking;
            _lastDiscordDurationMs = null;
        }

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var client = GitHubReleaseService.CreateHttpClient();

            var response = await client.GetAsync(url, cts.Token);
            sw.Stop();
            bool success = response.IsSuccessStatusCode;

            uiElement.Text = success ? "Доступен" : "Недоступен";
            uiElement.SetResourceReference(TextBlock.ForegroundProperty, success
                ? "StatusOnBrush"
                : "StatusOffBrush");

            if (name == "YouTube")
            {
                _youtubeProbeState = success ? DiagnosticProbeState.Available : DiagnosticProbeState.Unavailable;
                _lastYouTubeDurationMs = success ? (int)sw.ElapsedMilliseconds : null;
            }
            else if (name == "Discord")
            {
                _discordProbeState = success ? DiagnosticProbeState.Available : DiagnosticProbeState.Unavailable;
                _lastDiscordDurationMs = success ? (int)sw.ElapsedMilliseconds : null;
            }

            if (isManualCheck) AppLogger.Info($"{name}: {uiElement.Text} ({(success ? sw.ElapsedMilliseconds + " мс" : "-")})");
        }
        catch (OperationCanceledException)
        {
            uiElement.Text = "Недоступен";
            uiElement.SetResourceReference(TextBlock.ForegroundProperty, "StatusOffBrush");

            if (name == "YouTube")
            {
                _youtubeProbeState = DiagnosticProbeState.Unavailable;
                _lastYouTubeDurationMs = null;
            }
            else if (name == "Discord")
            {
                _discordProbeState = DiagnosticProbeState.Unavailable;
                _lastDiscordDurationMs = null;
            }

            if (isManualCheck) AppLogger.Info($"{name}: недоступен (таймаут)");
        }
        catch (Exception ex)
        {
            uiElement.Text = "Ошибка проверки";
            uiElement.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

            if (name == "YouTube")
            {
                _youtubeProbeState = DiagnosticProbeState.Error;
                _lastYouTubeDurationMs = null;
            }
            else if (name == "Discord")
            {
                _discordProbeState = DiagnosticProbeState.Error;
                _lastDiscordDurationMs = null;
            }

            if (isManualCheck) AppLogger.Warning($"{name}: ошибка проверки ({ex.Message})");
        }
    }

    private void UpdateConnectionDiagnosticSummary()
    {
#if DEBUG
        if (_isDebugPreviewMode) return;
#endif
        if (YouTubeStatusText == null || DiscordStatusText == null) return;

        // Dashboard info updates
        if (DiagVpnStatusText != null)
        {
            DiagVpnStatusText.Text = _lastVpnActive ? "Обнаружен" : "Не обнаружен";
            DiagVpnStatusText.SetResourceReference(TextBlock.ForegroundProperty, _lastVpnActive ? "WarningBrush" : "TextPrimaryBrush");
        }

        if (DiagProfileText != null)
        {
            if (string.IsNullOrWhiteSpace(Settings.SelectedProfile))
            {
                DiagProfileText.Text = "Не выбран";
                DiagProfileText.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            }
            else
            {
                DiagProfileText.Text = Settings.SelectedProfile;
                // Color: green if both probes are Available, yellow if selected but health is uncertain/partial
                bool allOk = _youtubeProbeState == DiagnosticProbeState.Available &&
                             _discordProbeState == DiagnosticProbeState.Available;
                bool anyChecked = _youtubeProbeState != DiagnosticProbeState.NotChecked ||
                                  _discordProbeState != DiagnosticProbeState.NotChecked;
                if (allOk)
                    DiagProfileText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                else if (anyChecked)
                    DiagProfileText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                else
                    DiagProfileText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
        }

        if (DiagVersionText != null)
        {
            if (!Settings.IsZapretInstalled)
            {
                DiagVersionText.Text = "Не установлен";
                DiagVersionText.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            }
            else if (_isUpdateAvailable)
            {
                DiagVersionText.Text = $"zapret {Settings.InstalledZapretVersion ?? "???"}";
                DiagVersionText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            }
            else
            {
                DiagVersionText.Text = $"zapret {Settings.InstalledZapretVersion ?? "???"}";
                DiagVersionText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
            }
        }

        if (DiagServiceStatusText != null)
        {
            if (!Settings.IsZapretInstalled)
            {
                DiagServiceStatusText.Text = "Не установлен";
                DiagServiceStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
            else
            {
                var status = _statusService?.GetStatus();
                if (status != null)
                {
                    DiagServiceStatusText.Text = status.IsRunning ? "Работает" : "Остановлен";
                    DiagServiceStatusText.SetResourceReference(TextBlock.ForegroundProperty, status.IsRunning ? "StatusOnBrush" : "DangerBrush");
                }
                else
                {
                    DiagServiceStatusText.Text = "Неизвестно";
                    DiagServiceStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                }
            }
        }

        // Autopick override: while wizard is running, show in-progress state
        if (_isWizardRunning)
        {
            SetDiagnosticsInProgressForAutopick();
            return;
        }

        bool vpnActive = _lastVpnActive && Settings.ShowVpnWarning;

        if (_youtubeProbeState == DiagnosticProbeState.Checking || _discordProbeState == DiagnosticProbeState.Checking)
        {
            // Anti-flicker: Keep previous stable DiagTitleText value while checking to avoid layout jump
        }
        else if (_youtubeProbeState == DiagnosticProbeState.Available && _discordProbeState == DiagnosticProbeState.Available)
        {
            DiagTitleText.Text = vpnActive ? "VPN влияет" : "Есть связь";
            DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, vpnActive ? "WarningBrush" : "StatusOnBrush");
            DiagDescText.Text = vpnActive
                ? "Проверка успешна, но активный VPN может влиять на результаты."
                : "Текущий профиль отлично подходит для YouTube и Discord.";
        }
        else if ((_youtubeProbeState == DiagnosticProbeState.Unavailable || _youtubeProbeState == DiagnosticProbeState.Error) &&
                 (_discordProbeState == DiagnosticProbeState.Unavailable || _discordProbeState == DiagnosticProbeState.Error))
        {
            DiagTitleText.Text = vpnActive ? "VPN влияет" : "Не удалось проверить";
            DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
            DiagDescText.Text = vpnActive
                ? "YouTube и Discord не отвечают. Возможно, VPN мешает обходу."
                : "Сервисы недоступны. Запустите обход или смените профиль.";
        }
        else if (_youtubeProbeState == DiagnosticProbeState.Available || _discordProbeState == DiagnosticProbeState.Available)
        {
            DiagTitleText.Text = vpnActive ? "VPN влияет" : "Частично";
            DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
            DiagDescText.Text = "Доступен только один из сервисов. Рекомендуется подобрать другой профиль.";
        }
        else
        {
            DiagTitleText.Text = vpnActive ? "VPN влияет" : "Ожидание";
            DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
            DiagDescText.Text = "Запустите проверку, чтобы оценить доступность YouTube и Discord.";

            if (_youtubeProbeState == DiagnosticProbeState.NotChecked)
            {
                YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
            if (_discordProbeState == DiagnosticProbeState.NotChecked)
            {
                DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
            }
        }

        // Update individual card latencies honestly (Anti-flicker: preserve previous text if checked)
        UpdateLatencyBadge(YouTubeLatencyBadge, YouTubeLatencyText, _youtubeProbeState, _lastYouTubeDurationMs);
        UpdateLatencyBadge(DiscordLatencyBadge, DiscordLatencyText, _discordProbeState, _lastDiscordDurationMs);

        UpdateServiceQualityBadge("YouTube", _youtubeProbeState, _lastYouTubeDurationMs, YouTubeQualityBadge, YouTubeQualityText);
        UpdateServiceQualityBadge("Discord", _discordProbeState, _lastDiscordDurationMs, DiscordQualityBadge, DiscordQualityText);

        if (DiagPingText != null && DiagDnsText != null && DiagLossText != null && DiagStabilityText != null)
        {
            if (_youtubeProbeState == DiagnosticProbeState.Checking || _discordProbeState == DiagnosticProbeState.Checking)
            {
                if (DiagPingText.Text == "Нет данных" || string.IsNullOrEmpty(DiagPingText.Text))
                {
                    DiagPingText.Text = "Проверяется…";
                    DiagPingText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    DiagDnsText.Text = "Проверяется…";
                    DiagDnsText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    DiagLossText.Text = "Проверяется…";
                    DiagLossText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                    DiagStabilityText.Text = "Выполняется диагностика…";
                    DiagStabilityText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                }
            }
            else if (_youtubeProbeState == DiagnosticProbeState.NotChecked && _discordProbeState == DiagnosticProbeState.NotChecked)
            {
                DiagPingText.Text = "Нет данных";
                DiagPingText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                DiagDnsText.Text = "Нет данных";
                DiagDnsText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                DiagLossText.Text = "Нет данных";
                DiagLossText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                DiagStabilityText.Text = "Проверка ещё не запускалась";
                DiagStabilityText.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            }
            else
            {
                var durations = new List<int>();
                if (_lastYouTubeDurationMs.HasValue) durations.Add(_lastYouTubeDurationMs.Value);
                if (_lastDiscordDurationMs.HasValue) durations.Add(_lastDiscordDurationMs.Value);

                int responded = 0;
                if (_youtubeProbeState == DiagnosticProbeState.Available) responded++;
                if (_discordProbeState == DiagnosticProbeState.Available) responded++;

                int failures = 0;
                if (_youtubeProbeState == DiagnosticProbeState.Unavailable || _youtubeProbeState == DiagnosticProbeState.Error) failures++;
                if (_discordProbeState == DiagnosticProbeState.Unavailable || _discordProbeState == DiagnosticProbeState.Error) failures++;

                // 1. Set Average Latency with zone-appropriate coloring (Excellent: green, Acceptable: neutral, Slow: yellow/orange, Failure: red)
                if (durations.Count > 0)
                {
                    int avg = (int)durations.Average();
                    DiagPingText.Text = $"~{avg} мс";
                    if (avg < 150)
                        DiagPingText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                    else if (avg <= 300)
                        DiagPingText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                    else
                        DiagPingText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                }
                else
                {
                    DiagPingText.Text = "Сбой сети";
                    DiagPingText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                }

                // 2. Set Availability Nodes
                DiagDnsText.Text = $"{responded} из 2";
                if (responded == 2)
                    DiagDnsText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                else if (responded == 1)
                    DiagDnsText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                else
                    DiagDnsText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

                // 3. Set Failures — 0: green, 1: yellow, 2+: red
                DiagLossText.Text = $"{failures} из 2";
                if (failures == 0)
                    DiagLossText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                else if (failures == 1)
                    DiagLossText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                else
                    DiagLossText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");

                // 4. Set Dynamic Stability (Short value: 'Стабильно' in green / 'Нестабильно' in red)
                if (responded == 2)
                {
                    DiagStabilityText.Text = "Стабильно";
                    DiagStabilityText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                }
                else
                {
                    DiagStabilityText.Text = "Нестабильно";
                    DiagStabilityText.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                }
            }
        }

        if (_youtubeProbeState != DiagnosticProbeState.Checking && _youtubeProbeState != DiagnosticProbeState.NotChecked &&
            _discordProbeState != DiagnosticProbeState.Checking && _discordProbeState != DiagnosticProbeState.NotChecked &&
            _lastCheckTime.HasValue)
        {
            if (_diagnosticHistory.Count == 0 || _diagnosticHistory.Last().Timestamp != _lastCheckTime.Value)
            {
                int res = 0;
                if (_youtubeProbeState == DiagnosticProbeState.Available) res++;
                if (_discordProbeState == DiagnosticProbeState.Available) res++;

                _diagnosticHistory.Add(new DiagnosticSnapshot
                {
                    Timestamp = _lastCheckTime.Value,
                    SuccessfulEndpoints = res,
                    YouTubeDurationMs = _lastYouTubeDurationMs,
                    DiscordDurationMs = _lastDiscordDurationMs,
                    YouTubeState = _youtubeProbeState,
                    DiscordState = _discordProbeState
                });

                if (_diagnosticHistory.Count > 12)
                {
                    _diagnosticHistory.RemoveAt(0);
                }
            }
        }

        UpdateStabilityGraph();

        if (DiagScenarioCardsContainer != null && DiagScenarioNameText != null && DiagScenarioDetailLabel != null && DiagScenarioDetailText != null)
        {
            if (Settings.ShowWorkModesSection)
            {
                DiagScenarioCardsContainer.Visibility = Visibility.Visible;

                // Tile 1: applied scenario name
                if (Settings.AppliedWorkMode == WorkModeGameKey)
                {
                    DiagScenarioNameText.Text = "Включен";
                }
                else
                {
                    DiagScenarioNameText.Text = "Выключен";
                }

                // Tile 2: Label and value
                if (Settings.AppliedWorkMode == WorkModeGameKey)
                {
                    DiagScenarioDetailLabel.Text = "Настройки";

                    string traffic = Settings.AppliedGameFilter switch
                    {
                        "UDP" => "UDP",
                        "TCP" => "TCP",
                        "TCP + UDP" => "TCP + UDP",
                        _ => "UDP"
                    };

                    string scope = Settings.AppliedGameScope switch
                    {
                        "Только нужные адреса" => "По спискам",
                        "Больше адресов" => "Расширенный",
                        "Максимальный охват" => "Весь трафик",
                        _ => "По спискам"
                    };

                    DiagScenarioDetailText.Text = $"{traffic} · {scope}";
                }
                else if (Settings.AppliedWorkMode == WorkModeServicesKey)
                {
                    DiagScenarioDetailLabel.Text = "Охват";
                    DiagScenarioDetailText.Text = "Основные сервисы";
                }
                else
                {
                    DiagScenarioDetailLabel.Text = "Охват";
                    DiagScenarioDetailText.Text = "Обычный охват";
                }
            }
            else
            {
                DiagScenarioCardsContainer.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static readonly System.Windows.Media.Brush YouTubeBrandBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(229, 57, 53)); // Tasteful crimson
    private static readonly System.Windows.Media.Brush DiscordBrandBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(88, 101, 242)); // Discord brand purple (#5865F2)

    private void YouTubeGraphArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateStabilityGraph();
    }

    private void DiscordGraphArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateStabilityGraph();
    }

    private void UpdateQualityBadge(System.Windows.Controls.Border badgeBorder, TextBlock badgeText, string quality, System.Windows.Media.Brush brush)
    {
        if (badgeBorder == null || badgeText == null) return;
        badgeBorder.Visibility = System.Windows.Visibility.Visible;
        badgeText.Text = quality;

        bool isLight = App.GetCurrentResolvedThemeName() == "light";
        if (isLight)
        {
            System.Windows.Media.Color bg;
            System.Windows.Media.Color fg;
            System.Windows.Media.Color border;

            if (quality == "Отлично" || quality == "Норма" || quality == "Успешно" || quality == "Активен" || quality == "Запущен")
            {
                if (quality == "Норма")
                {
                    // Warning / Amber
                    bg = System.Windows.Media.Color.FromRgb(0xFE, 0xF9, 0xC3); // #FEF9C3
                    fg = System.Windows.Media.Color.FromRgb(0x85, 0x4D, 0x0E); // #854D0E
                    border = System.Windows.Media.Color.FromRgb(0xFE, 0xF0, 0x8A); // #FEF08A
                }
                else
                {
                    // Success / Green
                    bg = System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7); // #DCFCE7
                    fg = System.Windows.Media.Color.FromRgb(0x16, 0x65, 0x34); // #166534
                    border = System.Windows.Media.Color.FromRgb(0xBB, 0xF7, 0xD0); // #BBF7D0
                }
            }
            else // "Медленно", "Сбой", "Отключен", "Остановлен"
            {
                bg = System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2); // #FEE2E2
                fg = System.Windows.Media.Color.FromRgb(0x99, 0x1B, 0x1B); // #991B1B
                border = System.Windows.Media.Color.FromRgb(0xFE, 0xCA, 0xCA); // #FECACA
            }

            badgeBorder.Background = new System.Windows.Media.SolidColorBrush(bg);
            badgeText.Foreground = new System.Windows.Media.SolidColorBrush(fg);
            badgeBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(border);
        }
        else
        {
            badgeText.Foreground = brush;
            badgeBorder.BorderBrush = brush;

            if (brush is System.Windows.Media.SolidColorBrush scb)
            {
                badgeBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(20, scb.Color.R, scb.Color.G, scb.Color.B));
            }
            else
            {
                badgeBorder.Background = (System.Windows.Media.Brush)FindResource("BorderSoftBrush");
            }
        }
    }

    private void UpdateLatencyBadge(System.Windows.Controls.Border badgeBorder, TextBlock badgeText, DiagnosticProbeState state, int? durationMs)
    {
        if (badgeBorder == null || badgeText == null) return;

        if (state == DiagnosticProbeState.Checking)
        {
            // Do not update or hide while checking to avoid layout jump
            return;
        }

        if (state == DiagnosticProbeState.NotChecked)
        {
            badgeBorder.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        badgeBorder.Visibility = System.Windows.Visibility.Visible;

        if (state == DiagnosticProbeState.Available && durationMs.HasValue)
        {
            int ms = durationMs.Value;
            badgeText.Text = $"{ms} мс";

            System.Windows.Media.Brush brush;
            if (ms < 150)
                brush = (System.Windows.Media.Brush)FindResource("StatusOnBrush");
            else if (ms <= 350)
                brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7)); // #FFC107 Amber
            else
                brush = (System.Windows.Media.Brush)FindResource("DangerBrush");

            // Custom styling for Light Theme
            bool isLight = App.GetCurrentResolvedThemeName() == "light";
            if (isLight)
            {
                System.Windows.Media.Color bg;
                System.Windows.Media.Color fg;
                System.Windows.Media.Color border;

                if (ms < 150)
                {
                    bg = System.Windows.Media.Color.FromRgb(0xDC, 0xFC, 0xE7); // #DCFCE7
                    fg = System.Windows.Media.Color.FromRgb(0x16, 0x65, 0x34); // #166534
                    border = System.Windows.Media.Color.FromRgb(0xBB, 0xF7, 0xD0); // #BBF7D0
                }
                else if (ms <= 350)
                {
                    bg = System.Windows.Media.Color.FromRgb(0xFE, 0xF9, 0xC3); // #FEF9C3
                    fg = System.Windows.Media.Color.FromRgb(0x85, 0x4D, 0x0E); // #854D0E
                    border = System.Windows.Media.Color.FromRgb(0xFE, 0xF0, 0x8A); // #FEF08A
                }
                else
                {
                    bg = System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2); // #FEE2E2
                    fg = System.Windows.Media.Color.FromRgb(0x99, 0x1B, 0x1B); // #991B1B
                    border = System.Windows.Media.Color.FromRgb(0xFE, 0xCA, 0xCA); // #FECACA
                }

                badgeBorder.Background = new System.Windows.Media.SolidColorBrush(bg);
                badgeText.Foreground = new System.Windows.Media.SolidColorBrush(fg);
                badgeBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(border);
            }
            else
            {
                badgeText.Foreground = brush;
                badgeBorder.BorderBrush = brush;
                if (brush is System.Windows.Media.SolidColorBrush scb)
                {
                    badgeBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(20, scb.Color.R, scb.Color.G, scb.Color.B));
                }
            }
        }
        else
        {
            badgeText.Text = "Сбой";
            var brush = (System.Windows.Media.Brush)FindResource("DangerBrush");

            bool isLight = App.GetCurrentResolvedThemeName() == "light";
            if (isLight)
            {
                var bg = System.Windows.Media.Color.FromRgb(0xFE, 0xE2, 0xE2); // #FEE2E2
                var fg = System.Windows.Media.Color.FromRgb(0x99, 0x1B, 0x1B); // #991B1B
                var border = System.Windows.Media.Color.FromRgb(0xFE, 0xCA, 0xCA); // #FECACA

                badgeBorder.Background = new System.Windows.Media.SolidColorBrush(bg);
                badgeText.Foreground = new System.Windows.Media.SolidColorBrush(fg);
                badgeBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(border);
            }
            else
            {
                badgeText.Foreground = brush;
                badgeBorder.BorderBrush = brush;
                if (brush is System.Windows.Media.SolidColorBrush scb)
                {
                    badgeBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(20, scb.Color.R, scb.Color.G, scb.Color.B));
                }
            }
        }
    }

    private void UpdateServiceQualityBadge(string name, DiagnosticProbeState state, int? durationMs, System.Windows.Controls.Border badgeBorder, TextBlock badgeText)
    {
        if (badgeBorder == null || badgeText == null) return;

        if (state == DiagnosticProbeState.Checking)
        {
            // Anti-flicker: Keep previous stable layout during active check
            return;
        }

        if (state == DiagnosticProbeState.NotChecked)
        {
            badgeBorder.Visibility = System.Windows.Visibility.Collapsed;
            return;
        }

        if (state == DiagnosticProbeState.Available && durationMs.HasValue)
        {
            int ms = durationMs.Value;
            if (ms < 150)
            {
                UpdateQualityBadge(badgeBorder, badgeText, "Отлично", (System.Windows.Media.Brush)FindResource("StatusOnBrush"));
            }
            else if (ms <= 350)
            {
                UpdateQualityBadge(badgeBorder, badgeText, "Норма", new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7))); // #FFC107 Amber
            }
            else if (ms <= 600)
            {
                UpdateQualityBadge(badgeBorder, badgeText, "Медленно", (System.Windows.Media.Brush)FindResource("DangerBrush"));
            }
            else
            {
                UpdateQualityBadge(badgeBorder, badgeText, "Сбой", (System.Windows.Media.Brush)FindResource("DangerBrush"));
            }
        }
        else
        {
            UpdateQualityBadge(badgeBorder, badgeText, "Сбой", (System.Windows.Media.Brush)FindResource("DangerBrush"));
        }
    }

    private string GetPointToolTipText(string serviceName, DateTime timestamp, int? durationMs)
    {
        string timeStr = timestamp.ToString("HH:mm:ss");
        if (!durationMs.HasValue)
        {
            return $"{serviceName}\nВремя: {timeStr}\nHTTPS-отклик: -- мс\nКачество: Сбой";
        }

        int ms = durationMs.Value;
        string zone = "";
        if (ms < 150) zone = "Отлично (≤ 150 мс)";
        else if (ms <= 350) zone = "Норма (150–350 мс)";
        else if (ms <= 600) zone = "Медленно (350–600 мс)";
        else zone = "Сбой (> 600 мс)";

        return $"{serviceName}\nВремя: {timeStr}\nHTTPS-отклик: {ms} мс\nКачество: {zone}";
    }

    private System.Windows.Media.PathGeometry CreateSmoothPathGeometry(System.Windows.Media.PointCollection points)
    {
        var geometry = new System.Windows.Media.PathGeometry();
        if (points == null || points.Count < 2) return geometry;

        var figure = new System.Windows.Media.PathFigure
        {
            StartPoint = points[0],
            IsClosed = false
        };

        for (int i = 0; i < points.Count - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];

            double dx = p2.X - p1.X;
            double cp1X = p1.X + dx / 3.0;
            double cp1Y = p1.Y;

            double cp2X = p2.X - dx / 3.0;
            double cp2Y = p2.Y;

            var segment = new System.Windows.Media.BezierSegment(
                new System.Windows.Point(cp1X, cp1Y),
                new System.Windows.Point(cp2X, cp2Y),
                p2,
                isStroked: true
            );
            figure.Segments.Add(segment);
        }

        geometry.Figures.Add(figure);
        return geometry;
    }

    private System.Windows.Media.PathGeometry CreateSmoothAreaGeometry(System.Windows.Media.PointCollection points, double baselineY)
    {
        var geometry = new System.Windows.Media.PathGeometry();
        if (points == null || points.Count < 2) return geometry;

        var figure = new System.Windows.Media.PathFigure
        {
            StartPoint = points[0],
            IsClosed = true
        };

        for (int i = 0; i < points.Count - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];

            double dx = p2.X - p1.X;
            double cp1X = p1.X + dx / 3.0;
            double cp1Y = p1.Y;

            double cp2X = p2.X - dx / 3.0;
            double cp2Y = p2.Y;

            var segment = new System.Windows.Media.BezierSegment(
                new System.Windows.Point(cp1X, cp1Y),
                new System.Windows.Point(cp2X, cp2Y),
                p2,
                isStroked: false
            );
            figure.Segments.Add(segment);
        }

        // Add line to the baseline at the last X
        figure.Segments.Add(new System.Windows.Media.LineSegment(
            new System.Windows.Point(points[points.Count - 1].X, baselineY),
            isStroked: false
        ));

        // Add line to the baseline at the first X
        figure.Segments.Add(new System.Windows.Media.LineSegment(
            new System.Windows.Point(points[0].X, baselineY),
            isStroked: false
        ));

        geometry.Figures.Add(figure);
        return geometry;
    }

    private void DrawServiceChart(
        string serviceName,
        System.Windows.Controls.Canvas container,
        double areaWidth,
        double areaHeight,
        System.Windows.Controls.TextBlock placeholder,
        System.Windows.Media.Brush lineBrush,
        Func<DiagnosticSnapshot, int?> durationSelector,
        Func<DiagnosticSnapshot, bool> successSelector)
    {
        if (container == null || placeholder == null) return;

        if (_diagnosticHistory.Count == 0)
        {
            placeholder.Visibility = System.Windows.Visibility.Visible;
            container.Children.Clear();
            return;
        }

        placeholder.Visibility = System.Windows.Visibility.Collapsed;
        container.Children.Clear();

        container.Width = areaWidth;
        container.Height = areaHeight;

        // Ensure canvas registers mouse clicks on its empty area and wire the dismissal handler
        container.Background = System.Windows.Media.Brushes.Transparent;
        container.MouseLeftButtonDown -= GraphCanvas_MouseLeftButtonDown;
        container.MouseLeftButtonDown += GraphCanvas_MouseLeftButtonDown;

        double leftMargin = 50;
        double bottomMargin = 35;
        double topMargin = 15;
        double rightMargin = 75; // Expanded to perfectly accommodate bubble label and zone tags

        double chartWidth = areaWidth - leftMargin - rightMargin;
        double chartHeight = areaHeight - topMargin - bottomMargin;

        if (chartWidth <= 0 || chartHeight <= 0) return;

        bool isLightTheme = App.GetCurrentResolvedThemeName() == "light";

        var gridBrush = (System.Windows.Media.Brush)FindResource("BorderSoftBrush");
        var textBrush = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        int recentMax = 0;
        int recentMin = int.MaxValue;
        foreach (var snap in _diagnosticHistory)
        {
            if (successSelector(snap))
            {
                int? dur = durationSelector(snap);
                if (dur.HasValue)
                {
                    if (dur.Value > recentMax) recentMax = dur.Value;
                    if (dur.Value < recentMin) recentMin = dur.Value;
                }
            }
        }
        if (recentMin == int.MaxValue)
        {
            placeholder.Text = "Нет успешных проверок";
            placeholder.Visibility = System.Windows.Visibility.Visible;
            container.Children.Clear();
            return;
        }

        int displayMaxY = 650;
        if (recentMax <= 150)
        {
            displayMaxY = 200;
        }
        else if (recentMax <= 350)
        {
            displayMaxY = 400;
        }
        else
        {
            displayMaxY = 650;
        }

        int displayMinY = 0;
        int[] ySteps;
        if (displayMaxY == 200 && recentMin >= 70)
        {
            displayMinY = 50;
            ySteps = new int[] { 50, 100, 150, 200 };
        }
        else
        {
            displayMinY = 0;
            if (displayMaxY == 200)
            {
                ySteps = new int[] { 0, 50, 100, 150, 200 };
            }
            else if (displayMaxY == 400)
            {
                ySteps = new int[] { 0, 100, 200, 300, 400 };
            }
            else
            {
                ySteps = new int[] { 0, 150, 350, 650 };
            }
        }

        double MapY(double value)
        {
            double range = displayMaxY - displayMinY;
            if (range <= 0) range = 1.0;
            double pct = (value - displayMinY) / range;
            if (pct < 0) pct = 0;
            if (pct > 1) pct = 1;
            return topMargin + chartHeight - pct * chartHeight;
        }

        bool useSeconds = false;
        if (_diagnosticHistory.Count >= 2)
        {
            var timeDiff = _diagnosticHistory.Last().Timestamp - _diagnosticHistory.First().Timestamp;
            if (timeDiff.TotalMinutes < 2)
            {
                useSeconds = true;
            }
        }
        string timeFormat = useSeconds ? "HH:mm:ss" : "HH:mm";

        // Excellent Zone (0-150)
        double excellentMin = Math.Max(0.0, (double)displayMinY);
        double excellentMax = Math.Max(excellentMin, Math.Min(150.0, (double)displayMaxY));
        double excellentHeight = (excellentMax - excellentMin) / (displayMaxY - displayMinY) * chartHeight;
        double excellentTop = MapY(excellentMax);

        if (excellentHeight > 0)
        {
            var rectGood = new System.Windows.Shapes.Rectangle
            {
                Width = chartWidth,
                Height = excellentHeight,
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(46, 204, 113)),
                Opacity = isLightTheme ? 0.12 : 0.05
            };
            System.Windows.Controls.Canvas.SetLeft(rectGood, leftMargin);
            System.Windows.Controls.Canvas.SetTop(rectGood, excellentTop);
            container.Children.Add(rectGood);
        }

        // Normal Zone (150-350)
        double normalMin = Math.Max(150.0, (double)displayMinY);
        double normalMax = Math.Max(normalMin, Math.Min(350.0, (double)displayMaxY));
        double normalHeight = (normalMax - normalMin) / (displayMaxY - displayMinY) * chartHeight;
        double normalTop = MapY(normalMax);

        if (normalHeight > 0)
        {
            var rectNormal = new System.Windows.Shapes.Rectangle
            {
                Width = chartWidth,
                Height = normalHeight,
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(241, 196, 15)),
                Opacity = isLightTheme ? 0.10 : 0.04
            };
            System.Windows.Controls.Canvas.SetLeft(rectNormal, leftMargin);
            System.Windows.Controls.Canvas.SetTop(rectNormal, normalTop);
            container.Children.Add(rectNormal);
        }

        // Slow Zone (350+)
        double slowMin = Math.Max(350.0, (double)displayMinY);
        double slowMax = Math.Max(slowMin, (double)displayMaxY);
        double slowHeight = (slowMax - slowMin) / (displayMaxY - displayMinY) * chartHeight;
        double slowTop = MapY(slowMax);

        if (slowHeight > 0)
        {
            var rectSlow = new System.Windows.Shapes.Rectangle
            {
                Width = chartWidth,
                Height = slowHeight,
                Fill = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(231, 76, 60)),
                Opacity = isLightTheme ? 0.10 : 0.03
            };
            System.Windows.Controls.Canvas.SetLeft(rectSlow, leftMargin);
            System.Windows.Controls.Canvas.SetTop(rectSlow, slowTop);
            container.Children.Add(rectSlow);
        }

        var zoneTextBrush = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        if (excellentHeight >= 16)
        {
            var lblGood = new System.Windows.Controls.TextBlock
            {
                Text = "Отлично",
                FontSize = 9,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = zoneTextBrush,
                Opacity = isLightTheme ? 0.9 : 0.7
            };
            System.Windows.Controls.Canvas.SetLeft(lblGood, leftMargin + chartWidth + 6);
            System.Windows.Controls.Canvas.SetTop(lblGood, MapY(excellentMin + (excellentMax - excellentMin) / 2.0) - 6);
            container.Children.Add(lblGood);
        }

        if (normalHeight >= 16)
        {
            var lblNormal = new System.Windows.Controls.TextBlock
            {
                Text = "Норма",
                FontSize = 9,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = zoneTextBrush,
                Opacity = isLightTheme ? 0.9 : 0.7
            };
            System.Windows.Controls.Canvas.SetLeft(lblNormal, leftMargin + chartWidth + 6);
            System.Windows.Controls.Canvas.SetTop(lblNormal, MapY(normalMin + (normalMax - normalMin) / 2.0) - 6);
            container.Children.Add(lblNormal);
        }

        if (slowHeight >= 16)
        {
            var lblSlow = new System.Windows.Controls.TextBlock
            {
                Text = "Медленно",
                FontSize = 9,
                FontWeight = System.Windows.FontWeights.SemiBold,
                Foreground = zoneTextBrush,
                Opacity = isLightTheme ? 0.9 : 0.7
            };
            System.Windows.Controls.Canvas.SetLeft(lblSlow, leftMargin + chartWidth + 6);
            System.Windows.Controls.Canvas.SetTop(lblSlow, MapY(slowMin + (slowMax - slowMin) / 2.0) - 6);
            container.Children.Add(lblSlow);
        }

        double xStep = _diagnosticHistory.Count > 1 ? chartWidth / (_diagnosticHistory.Count - 1) : chartWidth;

        var segments = new List<System.Windows.Media.PointCollection>();
        var currentSegment = new System.Windows.Media.PointCollection();
        var errBrush = (System.Windows.Media.Brush)FindResource("DangerBrush");

        for (int i = 0; i < _diagnosticHistory.Count; i++)
        {
            var snap = _diagnosticHistory[i];
            double x = leftMargin + i * xStep;

            if (successSelector(snap))
            {
                int? dur = durationSelector(snap);
                if (dur.HasValue)
                {
                    currentSegment.Add(new System.Windows.Point(x, MapY(dur.Value)));
                }
            }
            else
            {
                // Treat failed/timeout points as maximum latency spikes to keep the line continuous
                currentSegment.Add(new System.Windows.Point(x, topMargin));
            }
        }
        if (currentSegment.Count > 0)
        {
            segments.Add(currentSegment);
        }

        // Add soft gradient fill under chart lines
        foreach (var segmentPoints in segments)
        {
            if (segmentPoints.Count >= 2)
            {
                System.Windows.Media.Color baseColor = System.Windows.Media.Colors.Transparent;
                if (lineBrush is System.Windows.Media.SolidColorBrush scb)
                {
                    baseColor = scb.Color;
                }

                double baselineY = topMargin + chartHeight;

                // 2. Neutral area underlay to prevent merging with colored background zones
                System.Windows.Media.Brush underlayBrush;
                double underlayOpacity;
                if (isLightTheme)
                {
                    underlayBrush = System.Windows.Media.Brushes.White;
                    underlayOpacity = 0.30;
                }
                else
                {
                    underlayBrush = new System.Windows.Media.SolidColorBrush(
                        System.Windows.Media.Color.FromRgb(7, 17, 29) // #07111D
                    );
                    underlayOpacity = 0.06; // Muted to 0.06 (0.06-0.10 range) for subtle depth separation
                }

                var underlayPath = new System.Windows.Shapes.Path
                {
                    Data = CreateSmoothAreaGeometry(segmentPoints, baselineY),
                    Fill = underlayBrush,
                    Opacity = underlayOpacity
                };
                container.Children.Add(underlayPath);

                // 3. Service-colored area gradient (premium slower fade gradient stretch)
                var areaBrush = new System.Windows.Media.LinearGradientBrush();
                areaBrush.StartPoint = new System.Windows.Point(0, 0);
                areaBrush.EndPoint = new System.Windows.Point(0, 1);

                if (isLightTheme)
                {
                    var c0 = System.Windows.Media.Color.FromArgb(85, baseColor.R, baseColor.G, baseColor.B);
                    var c1 = System.Windows.Media.Color.FromArgb(45, baseColor.R, baseColor.G, baseColor.B);
                    var c2 = System.Windows.Media.Color.FromArgb(16, baseColor.R, baseColor.G, baseColor.B);
                    var c3 = System.Windows.Media.Color.FromArgb(4, baseColor.R, baseColor.G, baseColor.B);

                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c0, 0.00));
                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c1, 0.35));
                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c2, 0.75));
                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c3, 1.00));
                }
                else
                {
                    var c0 = System.Windows.Media.Color.FromArgb(98, baseColor.R, baseColor.G, baseColor.B);
                    var c1 = System.Windows.Media.Color.FromArgb(70, baseColor.R, baseColor.G, baseColor.B);
                    var c2 = System.Windows.Media.Color.FromArgb(38, baseColor.R, baseColor.G, baseColor.B);
                    var c3 = System.Windows.Media.Color.FromArgb(14, baseColor.R, baseColor.G, baseColor.B);
                    var c4 = System.Windows.Media.Color.FromArgb(4, baseColor.R, baseColor.G, baseColor.B);

                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c0, 0.00));
                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c1, 0.25));
                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c2, 0.55));
                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c3, 0.82));
                    areaBrush.GradientStops.Add(new System.Windows.Media.GradientStop(c4, 1.00));
                }

                var areaPath = new System.Windows.Shapes.Path
                {
                    Data = CreateSmoothAreaGeometry(segmentPoints, baselineY),
                    Fill = areaBrush
                };
                container.Children.Add(areaPath);
            }
        }

        foreach (var ms in ySteps)
        {
            double y = MapY(ms);

            var line = new System.Windows.Shapes.Line
            {
                X1 = leftMargin,
                Y1 = y,
                X2 = leftMargin + chartWidth,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = 0.7,
                Opacity = 0.9
            };
            container.Children.Add(line);

            var lbl = new System.Windows.Controls.TextBlock
            {
                Text = $"{ms}",
                FontSize = 10,
                Foreground = textBrush,
                Width = leftMargin - 5,
                TextAlignment = System.Windows.TextAlignment.Right
            };
            System.Windows.Controls.Canvas.SetLeft(lbl, 0);
            System.Windows.Controls.Canvas.SetTop(lbl, y - 7);
            container.Children.Add(lbl);
        }

        for (int i = 0; i < _diagnosticHistory.Count; i++)
        {
            var snap = _diagnosticHistory[i];
            double x = leftMargin + i * xStep;

            if (i % 3 == 0 || i == _diagnosticHistory.Count - 1)
            {
                var vLine = new System.Windows.Shapes.Line
                {
                    X1 = x,
                    Y1 = topMargin,
                    X2 = x,
                    Y2 = topMargin + chartHeight,
                    Stroke = gridBrush,
                    StrokeThickness = 0.7,
                    Opacity = 0.9
                };
                container.Children.Add(vLine);
            }

            // Print text labels only for Start, Middle, and End to keep X-axis clean
            bool shouldLabel = (i == 0) || 
                              (i == _diagnosticHistory.Count - 1) || 
                              (_diagnosticHistory.Count >= 3 && i == _diagnosticHistory.Count / 2);

            if (shouldLabel)
            {
                var xLbl = new System.Windows.Controls.TextBlock
                {
                    Text = snap.Timestamp.ToString(timeFormat),
                    FontSize = 9,
                    Foreground = textBrush
                };
                System.Windows.Controls.Canvas.SetLeft(xLbl, x - 15);
                System.Windows.Controls.Canvas.SetTop(xLbl, areaHeight - bottomMargin + 12);
                container.Children.Add(xLbl);
            }
        }

        foreach (var segmentPoints in segments)
        {
            if (segmentPoints.Count >= 2)
            {
                double glowThickness = isLightTheme ? 5.0 : 4.5;
                double glowOpacity = isLightTheme ? 0.11 : 0.07;

                // 4. Soft line glow / band (duplicate thicker stroke underneath)
                var glowPath = new System.Windows.Shapes.Path
                {
                    Data = CreateSmoothPathGeometry(segmentPoints),
                    Stroke = lineBrush,
                    StrokeThickness = glowThickness, 
                    Opacity = glowOpacity,        
                    StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round
                };
                container.Children.Add(glowPath);

                // 5. Main service line
                var linePath = new System.Windows.Shapes.Path
                {
                    Data = CreateSmoothPathGeometry(segmentPoints),
                    Stroke = lineBrush,
                    StrokeThickness = 2.5,
                    StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
                    StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
                    StrokeEndLineCap = System.Windows.Media.PenLineCap.Round
                };
                container.Children.Add(linePath);
            }
        }

        var surfaceBrush = (System.Windows.Media.Brush)FindResource("SurfaceBrush");

        for (int i = 0; i < _diagnosticHistory.Count; i++)
        {
            var snap = _diagnosticHistory[i];
            double x = leftMargin + i * xStep;
            int? dur = durationSelector(snap);

            bool isLast = (i == _diagnosticHistory.Count - 1);
            bool isPinned = (serviceName == _pinnedServiceName && snap.Timestamp == _pinnedTimestamp);
            double normalSize = isPinned ? 16.0 : (isLast ? 11.0 : 8.0);
            double hoverSize = normalSize + 4.0;

            double normalOffset = normalSize / 2.0;
            double hoverOffset = hoverSize / 2.0;

            double y;
            System.Windows.Media.Brush dotFill;

            bool isSuccess = successSelector(snap);
            
            if (isSuccess && dur.HasValue)
            {
                y = MapY(dur.Value);
                dotFill = lineBrush;
            }
            else
            {
                // Put failed markers at the top to match the continuous high-latency spike line
                y = topMargin;
                dotFill = errBrush;
                // Make them smaller to distinguish from normal data points
                normalSize = isPinned ? 12.0 : 6.0;
                normalOffset = normalSize / 2.0;
            }

            // 1. Create the visible dot
            var markerStroke = isPinned
                ? (isLightTheme ? (System.Windows.Media.Brush)FindResource("TextMutedBrush") : System.Windows.Media.Brushes.White)
                : dotFill;

            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = normalSize,
                Height = normalSize,
                Fill = isSuccess ? System.Windows.Media.Brushes.White : dotFill,
                Stroke = markerStroke,
                StrokeThickness = isPinned ? 2.0 : (isLast ? 2.5 : 1.5),
                Opacity = isSuccess ? 1.0 : 0.7
            };
            System.Windows.Controls.Canvas.SetLeft(dot, x - normalOffset);
            System.Windows.Controls.Canvas.SetTop(dot, y - normalOffset);
            System.Windows.Controls.Panel.SetZIndex(dot, isPinned ? 99 : (isLast ? 10 : 1));
            container.Children.Add(dot);

            // 1b. If pinned, draw a secondary target ring around the dot
            if (isPinned)
            {
                var ringStroke = isLightTheme
                    ? (System.Windows.Media.Brush)FindResource("TextMutedBrush")
                    : System.Windows.Media.Brushes.White;

                var ring = new System.Windows.Shapes.Ellipse
                {
                    Width = 26.0,
                    Height = 26.0,
                    Stroke = ringStroke,
                    StrokeThickness = 2.0,
                    Opacity = isLightTheme ? 0.5 : 0.4
                };
                System.Windows.Controls.Canvas.SetLeft(ring, x - 13.0);
                System.Windows.Controls.Canvas.SetTop(ring, y - 13.0);
                System.Windows.Controls.Panel.SetZIndex(ring, 98);
                container.Children.Add(ring);
            }

            // 2. Create the transparent hit circle
            var hitCircle = new System.Windows.Shapes.Ellipse
            {
                Width = 28.0,
                Height = 28.0,
                Fill = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            System.Windows.Controls.Canvas.SetLeft(hitCircle, x - 14.0);
            System.Windows.Controls.Canvas.SetTop(hitCircle, y - 14.0);
            System.Windows.Controls.Panel.SetZIndex(hitCircle, isPinned ? 100 : (isLast ? 11 : 2));
            container.Children.Add(hitCircle);

            // 3. Attach hover event handlers to the hit circle to scale the dot
            double finalX = x;
            double finalY = y;
            bool finalIsLast = isLast;
            bool finalIsPinned = isPinned;

            hitCircle.MouseEnter += (s, e) =>
            {
                dot.Width = hoverSize;
                dot.Height = hoverSize;
                System.Windows.Controls.Canvas.SetLeft(dot, finalX - hoverOffset);
                System.Windows.Controls.Canvas.SetTop(dot, finalY - hoverOffset);
                dot.StrokeThickness = finalIsPinned ? 3.0 : (finalIsLast ? 3.5 : 2.5);
                System.Windows.Controls.Panel.SetZIndex(dot, 999);
                System.Windows.Controls.Panel.SetZIndex(hitCircle, 1000);
            };

            hitCircle.MouseLeave += (s, e) =>
            {
                dot.Width = normalSize;
                dot.Height = normalSize;
                System.Windows.Controls.Canvas.SetLeft(dot, finalX - normalOffset);
                System.Windows.Controls.Canvas.SetTop(dot, finalY - normalOffset);
                dot.StrokeThickness = finalIsPinned ? 2.0 : (finalIsLast ? 2.5 : 1.5);
                System.Windows.Controls.Panel.SetZIndex(dot, finalIsPinned ? 99 : (finalIsLast ? 10 : 1));
                System.Windows.Controls.Panel.SetZIndex(hitCircle, finalIsPinned ? 100 : (finalIsLast ? 11 : 2));
            };

            // 4. Attach click handler directly to hitCircle
            hitCircle.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                _pinnedServiceName = serviceName;
                _pinnedTimestamp = snap.Timestamp;
                UpdateStabilityGraph();
            };

            // 5. Render Pinned Info Card
            if (isPinned)
            {
                var infoCard = new System.Windows.Controls.Border
                {
                    Background = (System.Windows.Media.Brush)FindResource("SurfaceBrush"),
                    BorderBrush = (System.Windows.Media.Brush)FindResource("BorderSoftBrush"),
                    BorderThickness = new System.Windows.Thickness(1.5),
                    CornerRadius = new System.Windows.CornerRadius(6),
                    Padding = new System.Windows.Thickness(10, 8, 10, 8),
                    Effect = new System.Windows.Media.Effects.DropShadowEffect
                    {
                        Color = System.Windows.Media.Colors.Black,
                        Opacity = (App.GetCurrentResolvedThemeName() == "light") ? 0.12 : 0.45,
                        BlurRadius = 10,
                        ShadowDepth = 2,
                        Direction = 270
                    }
                };

                var panel = new System.Windows.Controls.StackPanel();

                // Format Header Text: e.g. "YouTube — 14:32:05"
                var headerText = new System.Windows.Controls.TextBlock
                {
                    Text = $"{serviceName} — {snap.Timestamp:HH:mm:ss}",
                    FontWeight = System.Windows.FontWeights.Bold,
                    FontSize = 10.5,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush"),
                    Margin = new System.Windows.Thickness(0, 0, 0, 4)
                };
                panel.Children.Add(headerText);

                // Format Response time
                string responseTime = dur.HasValue ? $"{dur.Value} мс" : "-- мс";
                string quality = "";
                System.Windows.Media.Brush qualColor;
                if (dur.HasValue)
                {
                    int ms = dur.Value;
                    if (ms < 150)
                    {
                        quality = "Отлично";
                        qualColor = (System.Windows.Media.Brush)FindResource("StatusOnBrush");
                    }
                    else if (ms <= 350)
                    {
                        quality = "Норма";
                        qualColor = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255, 193, 7)); // #FFC107 Amber
                    }
                    else if (ms <= 600)
                    {
                        quality = "Медленно";
                        qualColor = (System.Windows.Media.Brush)FindResource("DangerBrush");
                    }
                    else
                    {
                        quality = "Сбой";
                        qualColor = (System.Windows.Media.Brush)FindResource("DangerBrush");
                    }
                }
                else
                {
                    quality = "Сбой";
                    qualColor = (System.Windows.Media.Brush)FindResource("DangerBrush");
                }

                var detailsText = new System.Windows.Controls.TextBlock
                {
                    FontSize = 10,
                    Foreground = (System.Windows.Media.Brush)FindResource("TextSecondaryBrush")
                };
                detailsText.Inlines.Add(new System.Windows.Documents.Run("HTTPS-отклик: ") { Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush") });
                detailsText.Inlines.Add(new System.Windows.Documents.Run(responseTime) { FontWeight = System.Windows.FontWeights.Bold, Foreground = (System.Windows.Media.Brush)FindResource("TextPrimaryBrush") });
                detailsText.Inlines.Add("  |  ");
                detailsText.Inlines.Add(new System.Windows.Documents.Run("Качество: ") { Foreground = (System.Windows.Media.Brush)FindResource("TextMutedBrush") });
                detailsText.Inlines.Add(new System.Windows.Documents.Run(quality) { FontWeight = System.Windows.FontWeights.Bold, Foreground = qualColor });

                panel.Children.Add(detailsText);
                infoCard.Child = panel;

                // Smart bounds-checking placement logic
                double cardWidth = 180.0;
                double cardHeight = 55.0;

                // Align center horizontally with clamping
                double cardLeft = x - (cardWidth / 2.0);
                if (cardLeft < leftMargin) cardLeft = leftMargin + 5;
                if (cardLeft + cardWidth > areaWidth) cardLeft = areaWidth - cardWidth - 5;

                // Put above the point with a comfortable 20px gap, if too high, flip below with 20px gap
                double cardTop = y - cardHeight - 20.0;
                if (cardTop < topMargin)
                {
                    cardTop = y + 20.0;
                }

                System.Windows.Controls.Canvas.SetLeft(infoCard, cardLeft);
                System.Windows.Controls.Canvas.SetTop(infoCard, cardTop);
                System.Windows.Controls.Panel.SetZIndex(infoCard, 500);
                container.Children.Add(infoCard);
            }
        }
    }

    private void GraphCanvas_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_pinnedServiceName != null || _pinnedTimestamp != null)
        {
            _pinnedServiceName = null;
            _pinnedTimestamp = null;
            UpdateStabilityGraph();
        }
    }

    private void UpdateStabilityGraph()
    {
        if (YouTubeGraphContainer == null || YouTubeGraphPlaceholderText == null ||
            DiscordGraphContainer == null || DiscordGraphPlaceholderText == null) return;

        double ytAreaWidth = YouTubeGraphArea.ActualWidth;
        double ytAreaHeight = YouTubeGraphArea.ActualHeight;

        double dsAreaWidth = DiscordGraphArea.ActualWidth;
        double dsAreaHeight = DiscordGraphArea.ActualHeight;

        if (ytAreaWidth <= 0 || ytAreaHeight <= 0 || dsAreaWidth <= 0 || dsAreaHeight <= 0) return;

        DrawServiceChart("YouTube", YouTubeGraphContainer, ytAreaWidth, ytAreaHeight, YouTubeGraphPlaceholderText, YouTubeBrandBrush, 
            snap => snap.YouTubeDurationMs,
            snap => snap.YouTubeState == DiagnosticProbeState.Available && snap.YouTubeDurationMs.HasValue && snap.YouTubeDurationMs.Value > 0);
        DrawServiceChart("Discord", DiscordGraphContainer, dsAreaWidth, dsAreaHeight, DiscordGraphPlaceholderText, DiscordBrandBrush, 
            snap => snap.DiscordDurationMs,
            snap => snap.DiscordState == DiagnosticProbeState.Available && snap.DiscordDurationMs.HasValue && snap.DiscordDurationMs.Value > 0);
    }

    private void SetDiagnosticsInProgressForAutopick()
    {
        DiagTitleText.Text = "Проверяем подключение";
        DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        DiagDescText.Text = "Подбираем профиль для YouTube и Discord.";

        if (YouTubeStatusText != null) YouTubeStatusText.Visibility = Visibility.Collapsed;
        if (DiscordStatusText != null) DiscordStatusText.Visibility = Visibility.Collapsed;

        if (YouTubeQualityBadge != null) YouTubeQualityBadge.Visibility = Visibility.Collapsed;
        if (DiscordQualityBadge != null) DiscordQualityBadge.Visibility = Visibility.Collapsed;
    }

    private void UninstallZapretButton_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Info("=== Удаление zapret ===");

        if (!EnsureAdmin())
        {
            AppLogger.Warning("Удаление отменено: нет прав администратора.");
            return;
        }

        string targetPath = AppPaths.ZapretDirectory;

        ShowOverlay(
            "Удалить zapret?",
            "Будет удалён только установленный движок zapret.\n\nПриложение и настройки останутся на месте.\nУстановить zapret можно снова в любой момент.",
            "Удалить zapret",
            "Назад",
            async () =>
            {
                AppLogger.Info("Пользователь подтвердил удаление zapret.");

                try
                {
                    // 3. Cleanup
                    AppLogger.Info("Остановка служб и процессов...");
                    await _serviceManager.UninstallServiceAsync();
                    await _serviceManager.PrepareFlowsealLikeEnvironmentAsync("удаление");

                    // 4. Path validation
                    if (!IsSafeZapretPath(targetPath))
                    {
                        AppLogger.Error($"Путь zapret выглядит небезопасно: {targetPath}");
                        ShowOverlay(
                            "Удаление остановлено",
                            "Приложение не стало удалять файлы, потому что путь zapret выглядит небезопасно. Это защита от случайного удаления важных папок.",
                            "Понятно",
                            "",
                            () => { },
                            closeBehavesAsPrimary: true
                        );
                        return;
                    }

                    // 5. Deletion
                    if (Directory.Exists(targetPath))
                    {
                        AppLogger.Info($"Удаление папки: {targetPath}");
                        Directory.Delete(targetPath, true);
                        AppLogger.Info("Папка zapret успешно удалена.");
                    }
                    else
                    {
                        AppLogger.Info("Папка zapret уже отсутствует.");
                    }

                    // 6. Reset settings
                    Settings.IsZapretInstalled = false;
                    Settings.InstalledZapretVersion = "";
                    Settings.ZapretPath = "";

                    // Clear profile if file is gone
                    if (!string.IsNullOrEmpty(Settings.SelectedProfile))
                    {
                         string profilePath = Path.Combine(targetPath, Settings.SelectedProfile);
                         if (!File.Exists(profilePath))
                         {
                             Settings.SelectedProfile = "";
                         }
                    }

                    SafeSaveSettings();
                    AppLogger.Info("Настройки приложения сброшены.");

                    // 7. Refresh UI
                    UpdateInstallCard();
                    RefreshExpertPage();
                    await CheckStatusOnStartup();
                    UpdateUpdateStatusUi();
                }
                catch (IOException ioEx)
                {
                    AppLogger.Error($"Ошибка ввода-вывода при удалении: {ioEx.Message}");
                    ShowOverlay(
                        "Файлы заняты",
                        "Не удалось удалить zapret, потому что некоторые файлы ещё используются Windows или другим процессом. Перезагрузите компьютер и попробуйте удалить снова.",
                        "Понятно",
                        "",
                        () => { },
                        closeBehavesAsPrimary: true
                    );
                }
                catch (Exception ex)
                {
                    AppLogger.Error($"Критическая ошибка при удалении: {ex.Message}");
                    ShowOverlay(
                        "Не удалось удалить zapret",
                        "Удаление не завершилось. Попробуйте ещё раз.",
                        "Понятно",
                        "",
                        () => { },
                        closeBehavesAsPrimary: true
                    );
                }
            },
            null,
            "DangerButtonStyle"
        );
    }

    private bool IsSafeZapretPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // 1. Must be inside ProgramData\Zapret Kmestu
            string allowedParent = Path.GetFullPath(AppPaths.ProgramDataDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!fullPath.StartsWith(allowedParent, StringComparison.OrdinalIgnoreCase)) return false;

            // 2. Must not be the parent itself
            if (fullPath.Equals(allowedParent, StringComparison.OrdinalIgnoreCase)) return false;

            // 3. Must not be drive root
            if (Path.GetPathRoot(fullPath) == fullPath) return false;

            // 4. Must not be app directory (where exe is)
            string appDir = System.AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.StartsWith(appDir, StringComparison.OrdinalIgnoreCase)) return false;

            // 5. Must not be current application base or working directory
            string baseDir = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string currentDir = Path.GetFullPath(Environment.CurrentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (fullPath.Equals(baseDir, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            if (fullPath.Equals(currentDir, StringComparison.OrdinalIgnoreCase) || fullPath.StartsWith(currentDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;

            // 6. Must not be AppData folders
            if (fullPath.StartsWith(AppPaths.RoamingAppDataDirectory, StringComparison.OrdinalIgnoreCase)) return false;
            if (fullPath.StartsWith(AppPaths.LocalAppDataDirectory, StringComparison.OrdinalIgnoreCase)) return false;

            // 7. Should contain markers (or be empty if we're cleaning up half-installed stuff, but user said "containing expected markers")
            bool hasWinws = File.Exists(Path.Combine(fullPath, "bin", "winws.exe"));
            bool hasServiceBat = File.Exists(Path.Combine(fullPath, "service.bat"));
            // We allow deletion if either exists, or if it's the exact expected ZapretDirectory path
            if (!hasWinws && !hasServiceBat && !fullPath.Equals(Path.GetFullPath(AppPaths.ZapretDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool EnsureAdmin()
    {
        if (_adminService.IsRunningAsAdministrator())
            return true;

        ShowAdminAlert();
        return false;
    }

    private void ShowAdminAlert()
    {
        ShowOverlay(
            "Нужны права администратора",
            "Для установки, ремонта и управления службой запустите приложение от имени администратора.",
            "Запустить",
            "Отмена",
            () => {
                if (_adminService.TryRestartAsAdministrator())
                {
                    _isReallyClosing = true;
                    System.Windows.Application.Current.Shutdown();
                }
                else
                {
                    SetFooterMessage("Запуск от администратора отменён", FooterMessageKind.Info, highlight: true);
                }
            },
            () => {
                _isReallyClosing = true;
                System.Windows.Application.Current.Shutdown();
            }
        );
    }

    private async Task HandleAutoStartBypassAsync()
    {
        // 1. Permanent blockers (return immediately)
        if (!Settings.AutoStartBypass) return;
        if (!_adminService.IsRunningAsAdministrator()) return;
        if (!Settings.IsZapretInstalled) return;
        if (string.IsNullOrWhiteSpace(Settings.SelectedProfile)) return;
        if (_autoStartBypassAttempted) return;

        string profilePath = System.IO.Path.Combine(AppPaths.ZapretDirectory, Settings.SelectedProfile);
        if (!System.IO.File.Exists(profilePath))
        {
            AppLogger.Warning($"Автозапуск обхода пропущен: профиль '{Settings.SelectedProfile}' не найден ({profilePath}).");
            SetFooterMessage("Автозапуск обхода пропущен: профиль не найден", FooterMessageKind.Warning);
            _autoStartBypassAttempted = true;
            return;
        }

        // 2. Initial delay to allow UI and initial status check to settle
        await Task.Delay(1200);

        // 3. Retry loop for temporary busy states
        int maxRetries = 15;
        int retryCount = 0;
        while (retryCount < maxRetries)
        {
            bool isBusy = _isCheckingUpdates || _installCts != null || _isWizardRunning || _isNetworkCheckRunning || _isTrayBypassToggleRunning || _isTrayProfileApplyRunning || OperationProgressCard.Visibility == Visibility.Visible;
            if (!isBusy) break;

            retryCount++;
            if (retryCount == maxRetries)
            {
                AppLogger.Warning("Автозапуск обхода пропущен: приложение было занято слишком долго.");
                SetFooterMessage("Автозапуск пропущен: приложение занято", FooterMessageKind.Warning);
                _autoStartBypassAttempted = true;
                return;
            }

            await Task.Delay(1000);
        }

        // 4. Final check: is it already running?
        var status = await Task.Run(() => _statusService.GetStatus());
        if (status.IsRunning)
        {
            AppLogger.Info("Автозапуск обхода: уже запущен.");
            _autoStartBypassAttempted = true;
            return;
        }

        // 5. Execute auto-start
        _autoStartBypassAttempted = true;
        AppLogger.Info("Выполнение автоматического запуска обхода...");

        try
        {
            var result = await _serviceManager.StartAsync();
            if (result.Success)
            {
                AppLogger.Info("Автоматический запуск обхода успешно завершён.");
                SetFooterMessage("Обход запущен автоматически", FooterMessageKind.Success);

                // Refresh UI and start quiet diagnostic
                _ = CheckStatusOnStartup();
                _ = ExecuteNetworkDiagnosticAsync(false);
            }
            else
            {
                AppLogger.Warning($"Автоматический запуск обхода не удался: {result.Message}");
                SetFooterMessage("Не удалось автоматически запустить обход", FooterMessageKind.Error);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Критическая ошибка при автоматическом запуске обхода: {ex.Message}");
            SetFooterMessage("Ошибка автозапуска обхода", FooterMessageKind.Error);
        }
    }

    // ─── System Tray ──────────────────────────────────────────────────────────

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(System.Drawing.Point pt, uint dwFlags);

    [System.Runtime.InteropServices.DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;

    private double GetDpiScaleForCursor()
    {
        try
        {
            var pt = WinForms.Cursor.Position;
            IntPtr monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out uint _) == 0)
                {
                    return dpiX / 96.0;
                }
            }
        }
        catch { }
        return 1.0;
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _notifyIcon = new WinForms.NotifyIcon
            {
                Text = "Zapret Kmestu",
                Visible = true
            };

            // Set initial icon based on current status if possible,
            // otherwise use a default one until the first status refresh.
            UpdateTrayIcon(HeroIconKind.Stopped);

            _notifyIcon.DoubleClick += (s, e) => RestoreWindow();
            _notifyIcon.MouseUp += (s, e) => {
                if (e.Button == WinForms.MouseButtons.Left)
                {
                    RestoreWindow();
                }
                else if (e.Button == WinForms.MouseButtons.Right)
                {
                    ShowTrayFlyout();
                }
            };
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка инициализации системного трея: {ex.Message}");
        }
    }

    private HeroIconKind _lastTrayState = (HeroIconKind)(-1);

    private void UpdateTrayIcon(HeroIconKind state)
    {
        if (_notifyIcon == null) return;

        System.Drawing.Icon? targetIcon = null;
        string fileName = "";

        switch (state)
        {
            case HeroIconKind.Running:
                fileName = "tray_on.ico";
                targetIcon = GetTrayIcon(ref _iconOn, fileName);
                break;
            case HeroIconKind.Stopped:
            case HeroIconKind.NotInstalled:
                fileName = "tray_off.ico";
                targetIcon = GetTrayIcon(ref _iconOff, fileName);
                break;
            case HeroIconKind.Wizard:
            case HeroIconKind.Installing:
                fileName = "tray_autopick.ico";
                targetIcon = GetTrayIcon(ref _iconAutopick, fileName);
                break;
            case HeroIconKind.Warning:
                fileName = "tray_vpn.ico";
                targetIcon = GetTrayIcon(ref _iconVpn, fileName);
                break;
            default:
                fileName = "tray_off.ico";
                targetIcon = GetTrayIcon(ref _iconOff, fileName);
                break;
        }

        if (targetIcon != null && _notifyIcon.Icon != targetIcon)
        {
            if (state != _lastTrayState)
            {
                AppLogger.Info($"Tray icon changed: state={state}, file={fileName}");
                _lastTrayState = state;
            }
            _notifyIcon.Icon = targetIcon;
        }
    }

    private System.Drawing.Icon? GetTrayIcon(ref System.Drawing.Icon? cache, string fileName)
    {
        if (cache != null) return cache;
        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Icons/{fileName}");
            var streamInfo = System.Windows.Application.GetResourceStream(uri);
            if (streamInfo != null)
            {
                cache = new System.Drawing.Icon(streamInfo.Stream);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка загрузки иконки {fileName}: {ex.Message}");
        }
        return cache;
    }

    private void ShowTrayFlyout()
    {
        Dispatcher.Invoke(() =>
        {
            if (_trayFlyout != null)
            {
                _trayFlyout.Close();
                _trayFlyout = null;
            }

            bool isDark = App.GetCurrentResolvedThemeName() == "dark";
            var bgBrush = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Color.FromRgb(43, 43, 43) : System.Windows.Media.Color.FromRgb(240, 240, 240));
            var fgBrush = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Colors.White : System.Windows.Media.Colors.Black);
            var hoverBrush = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Color.FromRgb(70, 70, 70) : System.Windows.Media.Color.FromRgb(220, 220, 220));
            var borderBrush = new System.Windows.Media.SolidColorBrush(isDark ? System.Windows.Media.Color.FromRgb(80, 80, 80) : System.Windows.Media.Color.FromRgb(200, 200, 200));

            var flyout = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                ShowActivated = false,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                SizeToContent = SizeToContent.WidthAndHeight,
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI")
            };

            _trayFlyout = flyout;

            flyout.Deactivated += (s, e) => { flyout.Close(); _trayFlyout = null; };
            flyout.PreviewKeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Escape) { flyout.Close(); _trayFlyout = null; } };

            var pt = WinForms.Cursor.Position;
            var scr = WinForms.Screen.FromPoint(pt);
            
            var monitor = MonitorFromPoint(pt, 2);
            uint dpiX = 96, dpiY = 96;
            GetDpiForMonitor(monitor, 0, out dpiX, out dpiY);
            double dpiScaleX = dpiX / 96.0;
            double dpiScaleY = dpiY / 96.0;

            var mainGrid = new System.Windows.Controls.Grid();
            mainGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Auto) });
            mainGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Auto) });

            var rootBorder = new System.Windows.Controls.Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8),
                Margin = new System.Windows.Thickness(0),
                Width = 240
            };
            
            rootBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10, ShadowDepth = 2, Opacity = 0.3
            };

            var rootStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Margin = new System.Windows.Thickness(0, 4, 0, 4) };
            rootBorder.Child = rootStack;

            var profilesBorder = new System.Windows.Controls.Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new System.Windows.Thickness(1),
                CornerRadius = new System.Windows.CornerRadius(8),
                Margin = new System.Windows.Thickness(0),
                Width = 300,
                Visibility = Visibility.Hidden
            };
            
            profilesBorder.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 10, ShadowDepth = 2, Opacity = 0.3
            };

            var profilesScroll = new System.Windows.Controls.ScrollViewer
            {
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                MaxHeight = (scr.WorkingArea.Height / dpiScaleY) - 24
            };
            var profilesStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical, Margin = new System.Windows.Thickness(0, 4, 0, 4) };
            profilesScroll.Content = profilesStack;
            profilesBorder.Child = profilesScroll;

            double rootPhysW = 240 * dpiScaleX;
            double profPhysW = 300 * dpiScaleX;
            double totalPhysW = rootPhysW + profPhysW;

            double idealRootLeft = pt.X - (rootPhysW / 2);
            bool placeProfilesRight = (idealRootLeft + totalPhysW <= scr.WorkingArea.Right);
            
            if (placeProfilesRight)
            {
                System.Windows.Controls.Grid.SetColumn(rootBorder, 0);
                System.Windows.Controls.Grid.SetColumn(profilesBorder, 1);
            }
            else
            {
                System.Windows.Controls.Grid.SetColumn(profilesBorder, 0);
                System.Windows.Controls.Grid.SetColumn(rootBorder, 1);
            }

            mainGrid.Children.Add(rootBorder);
            mainGrid.Children.Add(profilesBorder);
            flyout.Content = mainGrid;

            System.Windows.Controls.Border CreateMenuItem(string text, Action onClick, bool isBold = false, bool isChecked = false, bool hasChevron = false, double height = 32)
            {
                var itemBorder = new System.Windows.Controls.Border { Background = System.Windows.Media.Brushes.Transparent, Height = height, Padding = new System.Windows.Thickness(14, 0, 14, 0) };
                var grid = new System.Windows.Controls.Grid();
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(22) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Auto) });

                var checkTb = new System.Windows.Controls.TextBlock
                {
                    Text = isChecked ? "✓" : "",
                    Foreground = fgBrush,
                    FontSize = 13,
                    FontWeight = System.Windows.FontWeights.Bold,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                System.Windows.Controls.Grid.SetColumn(checkTb, 0);
                grid.Children.Add(checkTb);

                var tb = new System.Windows.Controls.TextBlock
                {
                    Text = text,
                    Foreground = fgBrush,
                    FontSize = 13,
                    FontWeight = isBold ? System.Windows.FontWeights.Bold : System.Windows.FontWeights.Normal,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                System.Windows.Controls.Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);

                if (hasChevron)
                {
                    var chevron = new System.Windows.Controls.TextBlock
                    {
                        Text = placeProfilesRight ? "›" : "‹",
                        Foreground = fgBrush,
                        FontSize = 15,
                        VerticalAlignment = System.Windows.VerticalAlignment.Center,
                        Opacity = 0.6,
                        Margin = new System.Windows.Thickness(0, -2, 0, 0)
                    };
                    System.Windows.Controls.Grid.SetColumn(chevron, 2);
                    grid.Children.Add(chevron);
                }

                itemBorder.Child = grid;

                itemBorder.MouseEnter += (s, e) => itemBorder.Background = hoverBrush;
                itemBorder.MouseLeave += (s, e) => itemBorder.Background = System.Windows.Media.Brushes.Transparent;
                itemBorder.MouseLeftButtonUp += (s, e) => { onClick(); flyout.Close(); _trayFlyout = null; };

                return itemBorder;
            }

            void AddSeparator(System.Windows.Controls.StackPanel panel)
            {
                panel.Children.Add(new System.Windows.Controls.Border
                {
                    Height = 1, Background = borderBrush, Margin = new System.Windows.Thickness(0, 2, 0, 2)
                });
            }

            rootStack.Children.Add(CreateMenuItem("Открыть Zapret Kmestu", () => RestoreWindow(), true));
            
            bool isAdmin = _adminService.IsRunningAsAdministrator();
            bool isInstalled = Settings.IsZapretInstalled;
            bool isBusy = _isWizardRunning || _installCts != null || _isTrayBypassToggleRunning || _isTrayProfileApplyRunning || OperationProgressCard.Visibility == Visibility.Visible;

            var status = _statusService.GetStatus();
            string toggleText = status.IsRunning ? "Выключить обход" : "Включить обход";

            var toggleItem = CreateMenuItem(toggleText, () => _ = ToggleBypassFromTrayAsync());
            toggleItem.IsEnabled = isAdmin && isInstalled && !isBusy;
            toggleItem.Opacity = toggleItem.IsEnabled ? 1.0 : 0.5;
            rootStack.Children.Add(toggleItem);

            var profileToggleItem = CreateMenuItem("Выбрать профиль", () => { }, false, false, true);
            profileToggleItem.IsEnabled = isAdmin && isInstalled && !isBusy;
            profileToggleItem.Opacity = profileToggleItem.IsEnabled ? 1.0 : 0.5;
            
            profileToggleItem.MouseEnter += (s, e) => {
                if (profileToggleItem.IsEnabled) profilesBorder.Visibility = Visibility.Visible;
            };

            foreach (var child in rootStack.Children)
            {
                if (child is System.Windows.Controls.Border b && b != profileToggleItem)
                {
                    b.MouseEnter += (s, e) => { profilesBorder.Visibility = Visibility.Hidden; };
                }
            }

            rootStack.Children.Add(profileToggleItem);
            AddSeparator(rootStack);

            rootStack.Children.Add(CreateMenuItem("Настройки", () =>
            {
                RestoreWindow();
                ShowPage("settings");
            }));

            AddSeparator(rootStack);
            rootStack.Children.Add(CreateMenuItem("Выход", () => { _ = ExitApplicationAsync(); }));

            try
            {
                var profiles = _profileService.GetAvailableProfiles()
                    .OrderBy(p => GetProfileSortKey(p.FileName).family)
                    .ThenBy(p => GetProfileSortKey(p.FileName).altNum)
                    .ThenBy(p => GetProfileSortKey(p.FileName).name)
                    .ToList();
                if (profiles.Count == 0)
                {
                    var noneItem = CreateMenuItem("Профили не найдены", () => { }, height: 28);
                    noneItem.IsEnabled = false;
                    noneItem.Opacity = 0.5;
                    profilesStack.Children.Add(noneItem);
                }
                else
                {
                    foreach (var p in profiles)
                    {
                        bool isSelected = p.FileName == Settings.SelectedProfile;
                        var pItem = CreateMenuItem(p.DisplayName, () => _ = ApplyProfileFromTrayAsync(p.FileName), isSelected, isSelected, height: 28);
                        if (isSelected) pItem.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(30, 128, 128, 128));
                        profilesStack.Children.Add(pItem);
                    }
                }
            }
            catch (Exception)
            {
                var errItem = CreateMenuItem("Ошибка загрузки профилей", () => { }, height: 28);
                errItem.IsEnabled = false;
                errItem.Opacity = 0.5;
                profilesStack.Children.Add(errItem);
            }

            rootBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            profilesBorder.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));

            double rootLogW = rootBorder.DesiredSize.Width;
            double rootLogH = rootBorder.DesiredSize.Height;
            double profLogW = profilesBorder.DesiredSize.Width;
            double profLogH = profilesBorder.DesiredSize.Height;

            double fullLogicalWidth = rootLogW + profLogW;
            double fullLogicalHeight = Math.Max(rootLogH, profLogH);

            double fullPhysH = fullLogicalHeight * dpiScaleY;
            double fullPhysW = fullLogicalWidth * dpiScaleX;

            bool placeAbove = (pt.Y + fullPhysH > scr.WorkingArea.Bottom);

            if (placeAbove)
            {
                rootBorder.VerticalAlignment = VerticalAlignment.Bottom;
                profilesBorder.VerticalAlignment = VerticalAlignment.Bottom;
            }
            else
            {
                rootBorder.VerticalAlignment = VerticalAlignment.Top;
                profilesBorder.VerticalAlignment = VerticalAlignment.Top;
            }

            double physicalLeft;
            if (placeProfilesRight)
            {
                physicalLeft = pt.X - (rootPhysW / 2);
            }
            else
            {
                physicalLeft = pt.X - (rootPhysW / 2) - profPhysW;
            }

            double physicalTop = placeAbove ? pt.Y - fullPhysH : pt.Y;

            if (physicalLeft < scr.WorkingArea.Left) physicalLeft = scr.WorkingArea.Left;
            if (physicalLeft + fullPhysW > scr.WorkingArea.Right) physicalLeft = scr.WorkingArea.Right - fullPhysW;
            if (physicalTop < scr.WorkingArea.Top) physicalTop = scr.WorkingArea.Top;
            if (physicalTop + fullPhysH > scr.WorkingArea.Bottom) physicalTop = scr.WorkingArea.Bottom - fullPhysH;

            flyout.Left = physicalLeft / dpiScaleX;
            flyout.Top = physicalTop / dpiScaleY;
            
            flyout.Width = fullLogicalWidth;
            flyout.Height = fullLogicalHeight;
            flyout.SizeToContent = SizeToContent.Manual;

            flyout.Show();
            
            var wih2 = new System.Windows.Interop.WindowInteropHelper(flyout);
            SetForegroundWindow(wih2.Handle);
            flyout.Focus();
        });
    }



    private async Task ApplyProfileFromTrayAsync(string profileName)
    {
        if (_isTrayProfileApplyRunning) return;

        bool isAdmin = _adminService.IsRunningAsAdministrator();
        bool isInstalled = Settings.IsZapretInstalled;
        bool isBusy = _isWizardRunning || _installCts != null || _isTrayBypassToggleRunning || _isTrayProfileApplyRunning || OperationProgressCard.Visibility == Visibility.Visible;

        if (!isAdmin || !isInstalled || isBusy) return;

        // Skip if same profile
        if (Settings.SelectedProfile == profileName) return;

        _isTrayProfileApplyRunning = true;

        try
        {
            var status = await Task.Run(() => _statusService.GetStatus());
            bool wasRunning = status.IsRunning;

            if (wasRunning)
            {
                AppLogger.Info("Остановка службы перед сменой профиля через трей...");
                await _serviceManager.StopAsync();
            }

            AppLogger.Info($"Применение профиля '{profileName}' через трей...");
            Settings.SelectedProfile = profileName;
            SafeSaveSettings();

            var result = await _serviceManager.ReinstallAsync();
            if (result.Success)
            {
                AppLogger.Info($"Профиль '{profileName}' успешно применён через трей.");
                if (wasRunning)
                {
                    AppLogger.Info("Запуск службы с новым профилем...");
                    await _serviceManager.StartAsync();
                }

                // Sync UI
                Dispatcher.Invoke(() => {
                    RefreshExpertPage();
                });

                if (wasRunning)
                {
                    _ = ExecuteNetworkDiagnosticAsync(false);
                }
            }
            else
            {
                AppLogger.Warning($"Не удалось применить профиль через трей: {result.Message}");
            }

            await CheckStatusOnStartup();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при смене профиля через трей: {ex.Message}");
        }
        finally
        {
            _isTrayProfileApplyRunning = false;
        }
    }

    private async Task ToggleBypassFromTrayAsync()
    {
        if (_isTrayBypassToggleRunning) return;

        bool isAdmin = _adminService.IsRunningAsAdministrator();
        bool isInstalled = Settings.IsZapretInstalled;
        bool isBusy = _isWizardRunning || _installCts != null || _isTrayBypassToggleRunning || _isTrayProfileApplyRunning || OperationProgressCard.Visibility == Visibility.Visible;

        if (!isAdmin || !isInstalled || isBusy) return;

        _isTrayBypassToggleRunning = true;

        try
        {
            var status = await Task.Run(() => _statusService.GetStatus());
            bool turningOn = !status.IsRunning;

            ZapretServiceActionResult result;
            if (turningOn)
            {
                result = await _serviceManager.StartAsync();
            }
            else
            {
                result = await _serviceManager.StopAsync();
            }

            if (result.Success && turningOn)
            {
                _ = ExecuteNetworkDiagnosticAsync(false);
            }

            // Refresh UI state
            await CheckStatusOnStartup();
        }
        catch (Exception ex)
        {
            AppLogger.Error($"Ошибка при переключении обхода из трея: {ex.Message}");
        }
        finally
        {
            _isTrayBypassToggleRunning = false;
        }
    }

    private void RestoreWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (!this.ShowInTaskbar)
            {
                this.ShowInTaskbar = true;
            }

            if (!this.IsVisible)
            {
                this.Show();
            }

            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }

            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
            this.Focus();
        });
    }

#if DEBUG
    private bool _isDebugMenuOpen = false;
    private bool _isDebugPreviewMode = false;

    // Preserved real UI states
    private bool _hasPreservedRealState = false;
    private HeroIconKind? _savedHeroIcon;
    private string _savedHelperText = "";
    private string _savedMainStatusText = "";
    private System.Windows.Media.Brush? _savedMainStatusForeground;
    private string _savedHeroHelperText = "";
    private string _savedProfileText = "";
    private Visibility _savedProfileVisibility;

    private string _savedYouTubeStatusText = "";
    private System.Windows.Media.Brush? _savedYouTubeStatusForeground;
    private string _savedDiscordStatusText = "";
    private System.Windows.Media.Brush? _savedDiscordStatusForeground;
    private string _savedDiagTitleText = "";
    private System.Windows.Media.Brush? _savedDiagTitleForeground;
    private string _savedDiagDescText = "";
    private string _savedLastCheckText = "";

    private ProfileCheckResult? _savedLastWizardResult;
    private List<ProfileCheckResult>? _savedLastWizardResults;

    private Visibility _savedOperationProgressCardVisibility;
    private Visibility _savedInstallProgressPanelVisibility;
    private Visibility _savedCancelOperationButtonVisibility;
    private double _savedInstallProgressBarValue;
    private bool _savedInstallProgressBarIsIndeterminate;
    private string _savedInstallStepText = "";
    private string _savedInstallPercentText = "";

    protected override void OnPreviewKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        TryHandleDebugShortcut(e);
    }

    private void TryHandleDebugShortcut(System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.D)
        {
            var modifiers = System.Windows.Input.Keyboard.Modifiers;
            if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) &&
                modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift) &&
                modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt))
            {
                e.Handled = true;
                ToggleDebugPopup();
            }
        }
    }

    private System.Windows.Controls.Primitives.Popup? _debugPopup;
    private System.Windows.Controls.StackPanel? _debugRightColumn;

    private void ToggleDebugPopup()
    {
        if (_debugPopup != null && _debugPopup.IsOpen)
        {
            CloseDebugPopup();
            return;
        }

        BuildAndShowDebugPopup();
    }

    private void CloseDebugPopup()
    {
        if (_debugPopup != null)
        {
            _debugPopup.IsOpen = false;
        }
    }

    private void BuildAndShowDebugPopup()
    {
        if (_debugPopup == null)
        {
            _debugPopup = new System.Windows.Controls.Primitives.Popup
            {
                PlacementTarget = this,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Center,
                StaysOpen = false,
                AllowsTransparency = true
            };
            
            _debugPopup.Closed += (s, e) => { _isDebugMenuOpen = false; };
        }

        _isDebugMenuOpen = true;

        var mainBorder = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 25, 25)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60, 60, 60)),
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(8),
            Padding = new System.Windows.Thickness(16),
            Width = 620,
            Height = 460
        };

        var rootStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };

        // Header
        var title = new System.Windows.Controls.TextBlock
        {
            Text = "ОТЛАДКА",
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 20,
            FontWeight = System.Windows.FontWeights.Bold
        };
        var subtitle = new System.Windows.Controls.TextBlock
        {
            Text = "Только превью интерфейса — без изменений служб и настроек",
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
            TextWrapping = System.Windows.TextWrapping.Wrap
        };
        var easterEgg = new System.Windows.Controls.TextBlock
        {
            Text = "Ты нашел секретное меню, здесь лучше ничего не трогать.",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 140, 40)),
            FontSize = 11,
            FontStyle = System.Windows.FontStyles.Italic,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
            TextWrapping = System.Windows.TextWrapping.Wrap
        };
        rootStack.Children.Add(title);
        rootStack.Children.Add(subtitle);
        rootStack.Children.Add(easterEgg);

        // Info Rows
        var infoWrap = new System.Windows.Controls.WrapPanel { Margin = new System.Windows.Thickness(0, 0, 0, 16) };
        infoWrap.Children.Add(CreateDebugChip($"Превью: {(_isDebugPreviewMode ? "вкл" : "выкл")}"));
        infoWrap.Children.Add(CreateDebugChip($"zapret: {(Settings.IsZapretInstalled ? "установлен" : "не установлен")}"));
        infoWrap.Children.Add(CreateDebugChip($"профиль: {(string.IsNullOrEmpty(Settings.SelectedProfile) ? "нет" : Settings.SelectedProfile)}"));
        infoWrap.Children.Add(CreateDebugChip($"проверка: {(_lastCheckTime == null ? "нет" : _lastCheckTime.Value.ToString("HH:mm:ss"))}"));
        infoWrap.Children.Add(CreateDebugChip($"результаты: {(_lastWizardResults?.Count.ToString() ?? "нет")}"));
        infoWrap.Children.Add(CreateDebugChip($"мастер: {(_isWizardRunning ? "да" : "нет")}"));
        infoWrap.Children.Add(CreateDebugChip($"операция: {(_installCts != null ? "да" : "нет")}"));
        rootStack.Children.Add(infoWrap);

        rootStack.Children.Add(new System.Windows.Controls.Border { Height = 1, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 50, 50)), Margin = new System.Windows.Thickness(0, 0, 0, 16) });

        // Columns
        var columnsGrid = new System.Windows.Controls.Grid();
        columnsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(200) });
        columnsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(16) });
        columnsGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) });

        var leftStack = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
        _debugRightColumn = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };

        columnsGrid.Children.Add(leftStack);
        System.Windows.Controls.Grid.SetColumn(leftStack, 0);
        
        var separator = new System.Windows.Controls.Border { Width = 1, Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 50, 50)), HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
        columnsGrid.Children.Add(separator);
        System.Windows.Controls.Grid.SetColumn(separator, 1);

        columnsGrid.Children.Add(_debugRightColumn);
        System.Windows.Controls.Grid.SetColumn(_debugRightColumn, 2);

        rootStack.Children.Add(columnsGrid);
        mainBorder.Child = rootStack;
        _debugPopup.Child = mainBorder;

        // Populate sections
        var sections = new System.Collections.Generic.Dictionary<string, Action>
        {
            { "Сравнение профилей", PopulateDebugComparisonSection },
            { "Главный экран", PopulateDebugMainScreenSection },
            { "Диагностика", PopulateDebugDiagnosticsSection },
            { "Автоподбор", PopulateDebugAutopickSection },
            { "Оверлеи", PopulateDebugOverlaysSection },
            { "Сброс", PopulateDebugResetSection }
        };

        System.Windows.Controls.Border? selectedSectionBorder = null;

        foreach (var kvp in sections)
        {
            var sectionBorder = new System.Windows.Controls.Border
            {
                Padding = new System.Windows.Thickness(12, 8, 12, 8),
                CornerRadius = new System.Windows.CornerRadius(4),
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Background = System.Windows.Media.Brushes.Transparent,
                Cursor = System.Windows.Input.Cursors.Hand
            };

            var sectionText = new System.Windows.Controls.TextBlock
            {
                Text = kvp.Key,
                Foreground = System.Windows.Media.Brushes.White,
                FontSize = 14,
                TextWrapping = System.Windows.TextWrapping.Wrap
            };
            sectionBorder.Child = sectionText;

            sectionBorder.MouseLeftButtonUp += (s, e) =>
            {
                if (selectedSectionBorder != null)
                    selectedSectionBorder.Background = System.Windows.Media.Brushes.Transparent;
                
                selectedSectionBorder = sectionBorder;
                selectedSectionBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(45, 55, 70));
                
                kvp.Value.Invoke();
            };

            sectionBorder.MouseEnter += (s, e) => { if (selectedSectionBorder != sectionBorder) sectionBorder.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 40)); };
            sectionBorder.MouseLeave += (s, e) => { if (selectedSectionBorder != sectionBorder) sectionBorder.Background = System.Windows.Media.Brushes.Transparent; };

            leftStack.Children.Add(sectionBorder);
        }

        // Select first by default
        if (leftStack.Children.Count > 0 && leftStack.Children[0] is System.Windows.Controls.Border firstBorder)
        {
            firstBorder.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left) { RoutedEvent = System.Windows.UIElement.MouseLeftButtonUpEvent });
        }

        _debugPopup.IsOpen = true;
    }

    private System.Windows.Controls.Border CreateDebugChip(string text)
    {
        var border = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(40, 40, 40)),
            CornerRadius = new System.Windows.CornerRadius(4),
            Padding = new System.Windows.Thickness(6, 2, 6, 2),
            Margin = new System.Windows.Thickness(0, 0, 6, 6)
        };
        border.Child = new System.Windows.Controls.TextBlock { Text = text, Foreground = System.Windows.Media.Brushes.LightGray, FontSize = 11 };
        return border;
    }

    private void AddDebugCommand(string text, Action? action)
    {
        if (_debugRightColumn == null) return;

        var border = new System.Windows.Controls.Border
        {
            Padding = new System.Windows.Thickness(12, 8, 12, 8),
            CornerRadius = new System.Windows.CornerRadius(4),
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
            Background = System.Windows.Media.Brushes.Transparent,
            Cursor = action != null ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow
        };

        border.Child = new System.Windows.Controls.TextBlock
        {
            Text = text,
            Foreground = action != null ? System.Windows.Media.Brushes.White : System.Windows.Media.Brushes.Gray,
            FontSize = 14,
            TextWrapping = System.Windows.TextWrapping.Wrap
        };

        if (action != null)
        {
            border.MouseEnter += (s, e) => border.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(50, 50, 50));
            border.MouseLeave += (s, e) => border.Background = System.Windows.Media.Brushes.Transparent;
            border.MouseLeftButtonUp += (s, e) =>
            {
                CloseDebugPopup();
                action.Invoke();
            };
        }

        _debugRightColumn.Children.Add(border);
    }

    private void PopulateDebugComparisonSection()
    {
        if (_debugRightColumn == null) return;
        _debugRightColumn.Children.Clear();
        
        AddDebugCommand("Шаблон: 6 профилей", () => ShowDebugMockComparison(6));
        AddDebugCommand("Шаблон: 8 профилей", () => ShowDebugMockComparison(8));
        AddDebugCommand("Шаблон: 12 профилей", () => ShowDebugMockComparison(12));
        AddDebugCommand("Стресс-тест: длинные имена и сбои", () => ShowDebugMockComparisonStress());
        AddDebugCommand("Сбой: все проверки провалены", () => ShowDebugMockComparisonAllFailed());
        AddDebugCommand("Шаблон: результаты отсутствуют", () => ShowDebugMockComparisonEmpty());
        
        if (_lastWizardResults != null && _lastWizardResults.Count > 0)
            AddDebugCommand("Реальные последние результаты", () => ShowRealLastWizardResultsFromDebug());
        else
            AddDebugCommand("Нет реальных результатов", null);
    }

    private void PopulateDebugMainScreenSection()
    {
        if (_debugRightColumn == null) return;
        _debugRightColumn.Children.Clear();

        AddDebugCommand("Не установлен", () => PreviewDebugMainState("NotInstalled"));
        AddDebugCommand("Обход выключен", () => PreviewDebugMainState("Stopped"));
        AddDebugCommand("Обход включён", () => PreviewDebugMainState("Running"));
        AddDebugCommand("VPN мешает", () => PreviewDebugMainState("VpnWarning"));
        AddDebugCommand("Автоподбор идёт", () => PreviewDebugAutopickInProgress());
        AddDebugCommand("Операция выполняется", () => PreviewDebugMainState("Installing"));
        AddDebugCommand("Вернуть реальное состояние", () => { CloseDebugPopup(); ResetDebugPreview(); });
    }

    private void PopulateDebugDiagnosticsSection()
    {
        if (_debugRightColumn == null) return;
        _debugRightColumn.Children.Clear();

        AddDebugCommand("Не проверено", () => PreviewDebugDiagnosticState("NotChecked"));
        AddDebugCommand("Проверка идёт", () => PreviewDebugDiagnosticState("Checking"));
        AddDebugCommand("YouTube + Discord в порядке", () => PreviewDebugDiagnosticState("Ok"));
        AddDebugCommand("Сервисы недоступны", () => PreviewDebugDiagnosticState("Unavailable"));
        AddDebugCommand("Частичный доступ", () => PreviewDebugDiagnosticState("Partial"));
        AddDebugCommand("Предупреждение о VPN", () => PreviewDebugDiagnosticState("VpnWarning"));
        AddDebugCommand("Очистить preview диагностики", () => { CloseDebugPopup(); ResetDebugPreview(); });
    }

    private void PopulateDebugAutopickSection()
    {
        if (_debugRightColumn == null) return;
        _debugRightColumn.Children.Clear();

        AddDebugCommand("Превью: автоподбор идёт", () => PreviewDebugAutopickInProgress());
        AddDebugCommand("Финал: быстрый", () => _ = PreviewDebugFinalFastResult(false));
        AddDebugCommand("Финал: быстрый + VPN", () => _ = PreviewDebugFinalFastResult(true));
        AddDebugCommand("Финал: точный", () => _ = PreviewDebugFinalAccurateResult());
        AddDebugCommand("Шаблон результатов: 8 профилей", () => PreviewDebugAutopickResult());
    }

    private async Task PreviewDebugFinalFastResult(bool vpn)
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;

        var mockResults = CreateDebugMockComparisonResults(4);
        var best = mockResults.FirstOrDefault(r => r.IsWinner) ?? mockResults.First();
        
        await ShowFinalWizardResultsOverlayAsync(mockResults, best, ProfileCheckMode.Fast, vpn);
    }

    private async Task PreviewDebugFinalAccurateResult()
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;

        var mockResults = CreateDebugMockComparisonResults(12);
        var best = mockResults.FirstOrDefault(r => r.IsWinner) ?? mockResults.First();
        
        await ShowFinalWizardResultsOverlayAsync(mockResults, best, ProfileCheckMode.Accurate, false);
    }

    private void PopulateDebugOverlaysSection()
    {
        if (_debugRightColumn == null) return;
        _debugRightColumn.Children.Clear();

        AddDebugCommand("Нужны права администратора", () => ShowDebugSafeOverlayPreview("Admin"));
        AddDebugCommand("Починить всё?", () => ShowDebugSafeOverlayPreview("FixAll"));
        AddDebugCommand("Ошибка проверки", () => ShowDebugSafeOverlayPreview("CheckError"));
        AddDebugCommand("Обновление доступно", () => ShowDebugSafeOverlayPreview("UpdateAvailable"));
    }

    private void PopulateDebugResetSection()
    {
        if (_debugRightColumn == null) return;
        _debugRightColumn.Children.Clear();

        AddDebugCommand("Сбросить превью отладки", () => { CloseDebugPopup(); ResetDebugPreview(); });
        AddDebugCommand("Закрыть меню", () => CloseDebugPopup());
    }

    private void PreviewDebugFastResult()
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;
        var mockResults = CreateDebugMockComparisonResults(4);
        ShowWizardResultsOverlay(mockResults, DateTime.Now, null);
    }

    private void PreviewDebugAccurateResult()
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;
        var mockResults = CreateDebugMockComparisonResults(12);
        ShowWizardResultsOverlay(mockResults, DateTime.Now, null);
    }

    private void ShowRealLastWizardResultsFromDebug()
    {
        if (_lastWizardResults != null)
        {
            ShowWizardResultsOverlay(_lastWizardResults, _lastWizardCompletedAt, null);
        }
    }

    private void ShowDebugSafeOverlayPreview(string type)
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;

        switch (type)
        {
            case "Admin":
                ShowOverlay(
                    "Нужны права администратора",
                    "Для установки, ремонта и управления службой запустите приложение от имени администратора. (PREVIEW)",
                    "Запустить",
                    "Отмена",
                    () => { },
                    () => { }
                );
                break;
            case "FixAll":
                ShowOverlay(
                    "Починить всё?",
                    "Приложение переустановит службу и применит рабочий профиль. Это может решить большинство проблем. (PREVIEW)",
                    "Починить",
                    "Отмена",
                    () => { },
                    () => { }
                );
                break;
            case "CheckError":
                ShowOverlay(
                    "Ошибка проверки",
                    "Приложение не смогло подключиться к сервисам проверки. Проверьте интернет. (PREVIEW)",
                    "Повторить",
                    "Закрыть",
                    () => { },
                    () => { }
                );
                break;
            case "UpdateAvailable":
                ShowOverlay(
                    "Доступно обновление zapret",
                    "Можно обновить движок zapret до новой версии. Обход будет остановлен на время обновления. (PREVIEW)",
                    "Обновить",
                    "Позже",
                    () => { },
                    () => { }
                );
                break;
        }
    }

    private void PreserveRealState()
    {
        if (_hasPreservedRealState) return;

        _savedHeroIcon = _lastHeroIcon;
        _savedHelperText = _lastHelperText;
        _savedMainStatusText = MainStatusText.Text;
        _savedMainStatusForeground = MainStatusText.Foreground;
        _savedHeroHelperText = HeroHelperText.Text;
        _savedProfileText = ProfileText.Text;
        _savedProfileVisibility = ProfileText.Visibility;

        _savedYouTubeStatusText = YouTubeStatusText.Text;
        _savedYouTubeStatusForeground = YouTubeStatusText.Foreground;
        _savedDiscordStatusText = DiscordStatusText.Text;
        _savedDiscordStatusForeground = DiscordStatusText.Foreground;
        _savedDiagTitleText = DiagTitleText.Text;
        _savedDiagTitleForeground = DiagTitleText.Foreground;
        _savedDiagDescText = DiagDescText.Text;
        _savedLastCheckText = LastCheckText.Text;

        _savedLastWizardResult = _lastWizardResult;
        _savedLastWizardResults = _lastWizardResults != null ? _lastWizardResults.ToList() : null;

        _savedOperationProgressCardVisibility = OperationProgressCard.Visibility;
        _savedInstallProgressPanelVisibility = InstallProgressPanel.Visibility;
        _savedCancelOperationButtonVisibility = CancelOperationButton.Visibility;
        _savedInstallProgressBarValue = InstallProgressBar.Value;
        _savedInstallProgressBarIsIndeterminate = InstallProgressBar.IsIndeterminate;
        _savedInstallStepText = InstallStepText.Text;
        _savedInstallPercentText = InstallPercentText.Text;

        _hasPreservedRealState = true;
    }





    private void ShowDebugMockComparison(int count)
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;

        var mockResults = CreateDebugMockComparisonResults(count);
        ShowWizardResultsOverlay(mockResults, DateTime.Now, null);
    }

    private void ShowDebugMockComparisonStress()
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;

        var mockResults = CreateDebugMockStressResults();
        ShowWizardResultsOverlay(mockResults, DateTime.Now, null);
    }

    private void ShowDebugMockComparisonAllFailed()
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;

        var mockResults = CreateDebugMockAllFailedResults();
        ShowWizardResultsOverlay(mockResults, DateTime.Now, null);
    }

    private void ShowDebugMockComparisonEmpty()
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;

        var mockResults = new System.Collections.Generic.List<ProfileCheckResult>();
        ShowWizardResultsOverlay(mockResults, DateTime.Now, null);
    }

    private void PreviewDebugMainState(string state)
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;
        ClearOverlayState();

        string circleBrushKey = "PrimaryBrush";
        string glowColorKey = "PrimaryColor";
        string titleBrushKey = "PrimaryBrush";
        HeroIconKind iconKind = HeroIconKind.Stopped;

        switch (state)
        {
            case "NotInstalled":
                MainStatusText.Text = "Не установлен";
                titleBrushKey = "IndigoBrush";
                HeroHelperText.Text = "Сначала установите zapret";
                iconKind = HeroIconKind.NotInstalled;
                circleBrushKey = "IndigoBrush";
                glowColorKey = "IndigoColor";
                ProfileText.Visibility = Visibility.Hidden;
                break;

            case "Stopped":
                MainStatusText.Text = "Обход выключен";
                titleBrushKey = "StatusOffBrush";
                HeroHelperText.Text = "Всё готово";
                iconKind = HeroIconKind.Stopped;
                circleBrushKey = "StatusOffBrush";
                glowColorKey = "StatusOffColor";
                ProfileText.Text = "zapret (preview)";
                ProfileText.Visibility = Visibility.Visible;
                break;

            case "Running":
                MainStatusText.Text = "Обход включён";
                titleBrushKey = "StatusOnBrush";
                HeroHelperText.Text = "Профиль активен, сервисы проверяются";
                iconKind = HeroIconKind.Running;
                circleBrushKey = "StatusOnBrush";
                glowColorKey = "StatusOnGlowColor";
                ProfileText.Text = "zapret (preview)";
                ProfileText.Visibility = Visibility.Visible;
                break;

            case "VpnWarning":
                MainStatusText.Text = "VPN мешает";
                titleBrushKey = "WarningBrush";
                HeroHelperText.Text = "Отключите VPN перед запуском";
                iconKind = HeroIconKind.Warning;
                circleBrushKey = "WarningBrush";
                glowColorKey = "WarningColor";
                ProfileText.Text = "zapret (preview)";
                ProfileText.Visibility = Visibility.Visible;
                break;

            case "Installing":
                MainStatusText.Text = "Операция...";
                titleBrushKey = "PrimaryBrush";
                HeroHelperText.Text = "Выполняем действия...";
                iconKind = HeroIconKind.Installing;
                circleBrushKey = "PrimaryBrush";
                glowColorKey = "PrimaryColor";
                ProfileText.Text = "zapret (preview)";
                ProfileText.Visibility = Visibility.Visible;
                break;
        }

        MainStatusText.SetResourceReference(TextBlock.ForegroundProperty, titleBrushKey);
        UpdateHeroIcon(iconKind, circleBrushKey, glowColorKey);
        UpdateTrayIcon(iconKind);
    }

    private void PreviewDebugDiagnosticState(string state)
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;
        ClearOverlayState();

        switch (state)
        {
            case "NotChecked":
                YouTubeStatusText.Text = "Не проверено";
                YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                DiscordStatusText.Text = "Не проверено";
                DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");
                DiagTitleText.Text = "Проверка ещё не запускалась";
                DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
                DiagDescText.Text = "Проверьте YouTube и Discord через текущий профиль.";
                LastCheckText.Text = "Обновлено: --:--:--";
                if (YouTubeQualityBadge != null) YouTubeQualityBadge.Visibility = Visibility.Collapsed;
                if (DiscordQualityBadge != null) DiscordQualityBadge.Visibility = Visibility.Collapsed;
                break;

            case "Checking":
                YouTubeStatusText.Text = "Проверяется…";
                YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                DiscordStatusText.Text = "Проверяется…";
                DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                DiagTitleText.Text = "Проверяем подключение...";
                DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
                DiagDescText.Text = "Это займёт несколько секунд.";
                LastCheckText.Text = "Обновление...";
                if (YouTubeQualityBadge != null) YouTubeQualityBadge.Visibility = Visibility.Collapsed;
                if (DiscordQualityBadge != null) DiscordQualityBadge.Visibility = Visibility.Collapsed;
                break;

            case "Ok":
                YouTubeStatusText.Text = "Доступен";
                YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                DiscordStatusText.Text = "Доступен";
                DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                DiagTitleText.Text = "Подключение работает";
                DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                DiagDescText.Text = "Текущий профиль подходит для YouTube и Discord.";
                LastCheckText.Text = $"Обновлено {DateTime.Now:HH:mm:ss}";
                UpdateServiceQualityBadge("YouTube", DiagnosticProbeState.Available, 120, YouTubeQualityBadge, YouTubeQualityText);
                UpdateServiceQualityBadge("Discord", DiagnosticProbeState.Available, 90, DiscordQualityBadge, DiscordQualityText);
                break;

            case "Unavailable":
                YouTubeStatusText.Text = "Недоступен";
                YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOffBrush");
                DiscordStatusText.Text = "Недоступен";
                DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOffBrush");
                DiagTitleText.Text = "Сервисы недоступны";
                DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOffBrush");
                DiagDescText.Text = "Запустите обход или попробуйте автоподбор профиля.";
                LastCheckText.Text = $"Обновлено {DateTime.Now:HH:mm:ss}";
                UpdateServiceQualityBadge("YouTube", DiagnosticProbeState.Unavailable, null, YouTubeQualityBadge, YouTubeQualityText);
                UpdateServiceQualityBadge("Discord", DiagnosticProbeState.Unavailable, null, DiscordQualityBadge, DiscordQualityText);
                break;

            case "Partial":
                YouTubeStatusText.Text = "Доступен";
                YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOnBrush");
                DiscordStatusText.Text = "Недоступен";
                DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "StatusOffBrush");
                DiagTitleText.Text = "Частичный доступ";
                DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                DiagDescText.Text = "Один сервис работает, для второго может понадобиться другой профиль.";
                LastCheckText.Text = $"Обновлено {DateTime.Now:HH:mm:ss}";
                UpdateServiceQualityBadge("YouTube", DiagnosticProbeState.Available, 120, YouTubeQualityBadge, YouTubeQualityText);
                UpdateServiceQualityBadge("Discord", DiagnosticProbeState.Unavailable, null, DiscordQualityBadge, DiscordQualityText);
                break;

            case "VpnWarning":
                YouTubeStatusText.Text = "Не проверено";
                YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                DiscordStatusText.Text = "Не проверено";
                DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                DiagTitleText.Text = "VPN мешает проверке";
                DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");
                DiagDescText.Text = "Обнаружено активное VPN-подключение. Отключите его для точной проверки.";
                LastCheckText.Text = "VPN активен";
                if (YouTubeQualityBadge != null) YouTubeQualityBadge.Visibility = Visibility.Collapsed;
                if (DiscordQualityBadge != null) DiscordQualityBadge.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void PreviewDebugAutopickInProgress()
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;
        ClearOverlayState();

        // 1. Diagnostic state
        YouTubeStatusText.Text = "идёт проверка";
        YouTubeStatusText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        DiscordStatusText.Text = "идёт проверка";
        DiscordStatusText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        DiagTitleText.Text = "Проверяем подключение";
        DiagTitleText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        DiagDescText.Text = "Подбираем профиль для YouTube и Discord.";
        LastCheckText.Text = "Подбор профиля...";

        // 2. Progress card
        OperationProgressCard.Visibility = Visibility.Visible;
        InstallProgressPanel.Visibility = Visibility.Visible;
        CancelOperationButton.Visibility = Visibility.Collapsed;
        InstallProgressBar.Value = 50;
        InstallProgressBar.IsIndeterminate = false;
        InstallStepText.Text = "Проверка general (ALT6).bat (6/12)...";
        InstallPercentText.Text = "50%";

        // 3. Hero UI
        MainStatusText.Text = "Подбор профиля";
        MainStatusText.SetResourceReference(TextBlock.ForegroundProperty, "PrimaryBrush");
        HeroHelperText.Text = "Ищем лучший профиль";
        UpdateHeroIcon(HeroIconKind.Wizard, "PrimaryBrush", "PrimaryColor");
        UpdateTrayIcon(HeroIconKind.Wizard);
        ProfileText.Text = "zapret (preview)";
        ProfileText.Visibility = Visibility.Visible;
    }

    private void PreviewDebugAutopickResult()
    {
        _isDebugMenuOpen = false;
        PreserveRealState();
        _isDebugPreviewMode = true;

        var mockResults = CreateDebugMockComparisonResults(8);
        ShowWizardResultsOverlay(mockResults, DateTime.Now, null);
    }

    private void ResetDebugPreview()
    {
        _isDebugMenuOpen = false;
        _isDebugPreviewMode = false;

        if (_hasPreservedRealState)
        {
            // Restore wizard results
            _lastWizardResult = _savedLastWizardResult;
            _lastWizardResults = _savedLastWizardResults;
            UpdateLastWizardResultsButtonState();

            // Restore Main UI
            _lastHeroIcon = _savedHeroIcon;
            _lastHelperText = _savedHelperText;
            MainStatusText.Text = _savedMainStatusText;
            MainStatusText.Foreground = _savedMainStatusForeground;
            HeroHelperText.Text = _savedHeroHelperText;
            ProfileText.Text = _savedProfileText;
            ProfileText.Visibility = _savedProfileVisibility;

            // Restore Diagnostics UI
            YouTubeStatusText.Text = _savedYouTubeStatusText;
            YouTubeStatusText.Foreground = _savedYouTubeStatusForeground;
            DiscordStatusText.Text = _savedDiscordStatusText;
            DiscordStatusText.Foreground = _savedDiscordStatusForeground;
            DiagTitleText.Text = _savedDiagTitleText;
            DiagTitleText.Foreground = _savedDiagTitleForeground;
            DiagDescText.Text = _savedDiagDescText;
            LastCheckText.Text = _savedLastCheckText;

            // Restore Progress Card
            OperationProgressCard.Visibility = _savedOperationProgressCardVisibility;
            InstallProgressPanel.Visibility = _savedInstallProgressPanelVisibility;
            CancelOperationButton.Visibility = _savedCancelOperationButtonVisibility;
            InstallProgressBar.Value = _savedInstallProgressBarValue;
            InstallProgressBar.IsIndeterminate = _savedInstallProgressBarIsIndeterminate;
            InstallStepText.Text = _savedInstallStepText;
            InstallPercentText.Text = _savedInstallPercentText;

            // Update physical components if needed
            if (_lastHeroIcon.HasValue)
            {
                UpdateHeroIcon(_lastHeroIcon.Value, GetHeroCircleBrushKey(_lastHeroIcon.Value), GetHeroGlowColorKey(_lastHeroIcon.Value));
                UpdateTrayIcon(_lastHeroIcon.Value);
            }

            _hasPreservedRealState = false;
        }

        ClearOverlayState();
        ShowOverlay("Отладка сброшена", "Реальное состояние интерфейса успешно восстановлено.", "ОК", "", () => {});
    }

    private string GetHeroCircleBrushKey(HeroIconKind kind)
    {
        switch (kind)
        {
            case HeroIconKind.Running: return "StatusOnBrush";
            case HeroIconKind.Stopped: return "StatusOffBrush";
            case HeroIconKind.NotInstalled: return "IndigoBrush";
            case HeroIconKind.Warning: return "WarningBrush";
            case HeroIconKind.Wizard:
            case HeroIconKind.Installing:
            default:
                return "PrimaryBrush";
        }
    }

    private string GetHeroGlowColorKey(HeroIconKind kind)
    {
        switch (kind)
        {
            case HeroIconKind.Running: return "StatusOnGlowColor";
            case HeroIconKind.Stopped: return "StatusOffColor";
            case HeroIconKind.NotInstalled: return "IndigoColor";
            case HeroIconKind.Warning: return "WarningColor";
            case HeroIconKind.Wizard:
            case HeroIconKind.Installing:
            default:
                return "PrimaryColor";
        }
    }

    private System.Collections.Generic.List<ProfileCheckResult> CreateDebugMockComparisonResults(int count)
    {
        var list = new System.Collections.Generic.List<ProfileCheckResult>();

        // Winner profile (1 winner only, best row first by score)
        list.Add(new ProfileCheckResult
        {
            ProfileName = "general.bat",
            YouTubeAvailable = true,
            DiscordAvailable = true,
            YouTubeScore = 10,
            DiscordScore = 10,
            SuccessCount = 10,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(1.2),
            IsWinner = true
        });

        // Other options
        for (int i = 1; i < count; i++)
        {
            string profileName = $"general (ALT{i}).bat";
            
            // Varying/mixed states
            bool yt = false;
            bool ds = false;
            int ytScore = 0;
            int dsScore = 0;
            int successCount = 0;
            int totalProbes = 10;
            
            if (i == 1)
            {
                // Perfect but slightly slower than winner (not winner)
                yt = true;
                ds = true;
                ytScore = 9;
                dsScore = 9;
                successCount = 9;
            }
            else if (i % 3 == 0)
            {
                // YouTube only
                yt = true;
                ytScore = 8;
                successCount = 5;
            }
            else if (i % 3 == 1)
            {
                // Discord only
                ds = true;
                dsScore = 8;
                successCount = 4;
            }
            else
            {
                // Failed completely but non-zero probes
                successCount = 1;
            }

            list.Add(new ProfileCheckResult
            {
                ProfileName = profileName,
                YouTubeAvailable = yt,
                DiscordAvailable = ds,
                YouTubeScore = ytScore,
                DiscordScore = dsScore,
                SuccessCount = successCount,
                TotalProbes = totalProbes,
                CheckDuration = TimeSpan.FromSeconds(0.8 + 0.1 * i),
                IsWinner = false
            });
        }

        // Sort descending by score, so winner with score 20 is first
        return list.OrderByDescending(r => r.TotalScore).ToList();
    }

    private System.Collections.Generic.List<ProfileCheckResult> CreateDebugMockStressResults()
    {
        var list = new System.Collections.Generic.List<ProfileCheckResult>();

        // 1. Winner with extremely long name
        list.Add(new ProfileCheckResult
        {
            ProfileName = "extremely_long_optimized_profile_configuration_winner_with_custom_parameters_that_wraps.bat",
            YouTubeAvailable = true,
            DiscordAvailable = true,
            YouTubeScore = 10,
            DiscordScore = 10,
            SuccessCount = 10,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(1.5),
            IsWinner = true,
            Errors = string.Empty
        });

        // 2. Partial (YouTube only, very long name)
        list.Add(new ProfileCheckResult
        {
            ProfileName = "highly_experimental_discord_failing_youtube_success_profile_under_stress_test_conditions.bat",
            YouTubeAvailable = true,
            DiscordAvailable = false,
            YouTubeScore = 8,
            DiscordScore = 0,
            SuccessCount = 6,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(1.8),
            IsWinner = false,
            Errors = "Discord: HTTP 403 Forbidden\nDiscord API request timed out after 3000ms"
        });

        // 3. Partial (Discord only, very long name)
        list.Add(new ProfileCheckResult
        {
            ProfileName = "alternative_routing_profile_for_restricted_networks_discord_only_working.bat",
            YouTubeAvailable = false,
            DiscordAvailable = true,
            YouTubeScore = 0,
            DiscordScore = 7,
            SuccessCount = 5,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(2.1),
            IsWinner = false,
            Errors = "YouTube: SSL Connection Reset by Peer (10054)\nFailed to handshake TLS ClientHello"
        });

        // 4. Failed completely (Both dead, very long name)
        list.Add(new ProfileCheckResult
        {
            ProfileName = "broken_legacy_compatibility_profile_with_obsolete_parameters_failing_completely.bat",
            YouTubeAvailable = false,
            DiscordAvailable = false,
            YouTubeScore = 0,
            DiscordScore = 0,
            SuccessCount = 0,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(2.5),
            IsWinner = false,
            Errors = "YouTube: Connection timed out\nDiscord: Name or service not known (DNS failure)"
        });

        // 5. Partial (YouTube only, normal name)
        list.Add(new ProfileCheckResult
        {
            ProfileName = "general (ALT3).bat",
            YouTubeAvailable = true,
            DiscordAvailable = false,
            YouTubeScore = 6,
            DiscordScore = 0,
            SuccessCount = 4,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(1.1),
            IsWinner = false,
            Errors = "Discord: Connection timed out"
        });

        // 6. Partial (Discord only, normal name)
        list.Add(new ProfileCheckResult
        {
            ProfileName = "general (ALT4).bat",
            YouTubeAvailable = false,
            DiscordAvailable = true,
            YouTubeScore = 0,
            DiscordScore = 6,
            SuccessCount = 4,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(1.2),
            IsWinner = false,
            Errors = "YouTube: HTTP 502 Bad Gateway"
        });

        // 7. Failed completely (Both dead, normal name)
        list.Add(new ProfileCheckResult
        {
            ProfileName = "general (ALT5).bat",
            YouTubeAvailable = false,
            DiscordAvailable = false,
            YouTubeScore = 0,
            DiscordScore = 0,
            SuccessCount = 0,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(1.3),
            IsWinner = false,
            Errors = "YouTube: Timeout\nDiscord: Timeout"
        });

        // 8. Both working but low scores (not winner, long name)
        list.Add(new ProfileCheckResult
        {
            ProfileName = "standard_backup_profile_that_works_but_has_poor_overall_latency_and_stability.bat",
            YouTubeAvailable = true,
            DiscordAvailable = true,
            YouTubeScore = 5,
            DiscordScore = 5,
            SuccessCount = 7,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(2.9),
            IsWinner = false,
            Errors = string.Empty
        });

        // Sort descending by score, so winner is first
        return list.OrderByDescending(r => r.TotalScore).ToList();
    }

    private System.Collections.Generic.List<ProfileCheckResult> CreateDebugMockAllFailedResults()
    {
        var list = new System.Collections.Generic.List<ProfileCheckResult>();

        list.Add(new ProfileCheckResult
        {
            ProfileName = "general.bat",
            YouTubeAvailable = false,
            DiscordAvailable = false,
            YouTubeScore = 0,
            DiscordScore = 0,
            SuccessCount = 0,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(1.5),
            IsWinner = false,
            Errors = "Connection reset by peer"
        });

        list.Add(new ProfileCheckResult
        {
            ProfileName = "general (ALT1).bat",
            YouTubeAvailable = false,
            DiscordAvailable = false,
            YouTubeScore = 0,
            DiscordScore = 0,
            SuccessCount = 0,
            TotalProbes = 10,
            CheckDuration = TimeSpan.FromSeconds(1.8),
            IsWinner = false,
            Errors = "Timed out"
        });

        list.Add(new ProfileCheckResult
        {
            ProfileName = "general (ALT2).bat",
            YouTubeAvailable = false,
            DiscordAvailable = false,
            YouTubeScore = 0,
            DiscordScore = 0,
            SuccessCount = 0,
            TotalProbes = 0,
            CheckDuration = TimeSpan.FromSeconds(0.1),
            IsWinner = false,
            Errors = "Skipped check"
        });

        return list;
    }
#endif



    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        if (parent == null) yield break;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T tChild)
            {
                yield return tChild;
            }
            foreach (var childOfChild in FindVisualChildren<T>(child))
            {
                yield return childOfChild;
            }
        }
    }

    private void NormalizeScenarioSettings()
    {
        bool changed = false;

        // Normalize Selected
        if (Settings.SelectedWorkMode != WorkModeStandardKey &&
            Settings.SelectedWorkMode != WorkModeServicesKey &&
            Settings.SelectedWorkMode != WorkModeGameKey)
        {
            Settings.SelectedWorkMode = WorkModeStandardKey;
            changed = true;
        }

        if (Settings.SelectedGameFilter != "UDP" &&
            Settings.SelectedGameFilter != "TCP" &&
            Settings.SelectedGameFilter != "TCP + UDP")
        {
            Settings.SelectedGameFilter = "UDP";
            changed = true;
        }

        if (Settings.SelectedGameScope != "Только нужные адреса" &&
            Settings.SelectedGameScope != "Больше адресов" &&
            Settings.SelectedGameScope != "Максимальный охват")
        {
            Settings.SelectedGameScope = "Только нужные адреса";
            changed = true;
        }

        // Normalize Applied
        if (Settings.AppliedWorkMode != WorkModeStandardKey &&
            Settings.AppliedWorkMode != WorkModeServicesKey &&
            Settings.AppliedWorkMode != WorkModeGameKey)
        {
            Settings.AppliedWorkMode = WorkModeStandardKey;
            changed = true;
        }

        if (Settings.AppliedGameFilter != "UDP" &&
            Settings.AppliedGameFilter != "TCP" &&
            Settings.AppliedGameFilter != "TCP + UDP")
        {
            Settings.AppliedGameFilter = "UDP";
            changed = true;
        }

        if (Settings.AppliedGameScope != "Только нужные адреса" &&
            Settings.AppliedGameScope != "Больше адресов" &&
            Settings.AppliedGameScope != "Максимальный охват")
        {
            Settings.AppliedGameScope = "Только нужные адреса";
            changed = true;
        }

        if (changed)
        {
            SafeSaveSettings();
        }
    }

    private void RestoreScenarioSelections()
    {
        NormalizeScenarioSettings();

        _selectedWorkMode = Settings.SelectedWorkMode;
        _selectedGameFilter = Settings.SelectedGameFilter;
        _selectedGameScope = Settings.SelectedGameScope;
    }

    private bool IsSelectedScenarioSameAsApplied()
    {
        NormalizeScenarioSettings();
        if (_selectedWorkMode == WorkModeStandardKey)
        {
            return Settings.AppliedWorkMode == WorkModeStandardKey;
        }
        if (_selectedWorkMode == WorkModeServicesKey)
        {
            return Settings.AppliedWorkMode == WorkModeServicesKey;
        }
        if (_selectedWorkMode == WorkModeGameKey)
        {
            return Settings.AppliedWorkMode == WorkModeGameKey &&
                   _selectedGameFilter == Settings.AppliedGameFilter &&
                   _selectedGameScope == Settings.AppliedGameScope;
        }
        return false;
    }

    private void SaveAppliedScenarioSettings()
    {
        Settings.AppliedWorkMode = _selectedWorkMode;
        Settings.AppliedGameFilter = _selectedGameFilter;
        Settings.AppliedGameScope = _selectedGameScope;
        SafeSaveSettings();
    }

    private void UpdateGameSettingsVisuals()
    {
        if (GameFilterUdpButton == null || GameFilterTcpButton == null || GameFilterTcpUdpButton == null ||
            GameScopeListsButton == null || GameScopeExtendedButton == null || GameScopeAllButton == null)
            return;

        // Reset all filter buttons
        GameFilterUdpButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameFilterUdpButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
        GameFilterTcpButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameFilterTcpButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
        GameFilterTcpUdpButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameFilterTcpUdpButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");

        // Set active filter button styling
        System.Windows.Controls.Border? activeFilter = null;
        if (_selectedGameFilter == "UDP") activeFilter = GameFilterUdpButton;
        else if (_selectedGameFilter == "TCP") activeFilter = GameFilterTcpButton;
        else if (_selectedGameFilter == "TCP + UDP") activeFilter = GameFilterTcpUdpButton;

        if (activeFilter != null)
        {
            string borderBrush = activeFilter == GameFilterUdpButton ? "PrimaryBrush" : "IndigoBrush";
            string bgBrush = activeFilter == GameFilterUdpButton ? "PrimaryTintBrush" : "IndigoTintBrush";
            activeFilter.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, borderBrush);
            activeFilter.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, bgBrush);
        }

        // Reset all scope buttons
        GameScopeListsButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameScopeListsButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
        GameScopeExtendedButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameScopeExtendedButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");
        GameScopeAllButton.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "BorderBrush");
        GameScopeAllButton.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "BorderSoftBrush");

        // Set active scope button styling
        System.Windows.Controls.Border? activeScope = null;
        if (_selectedGameScope == "Только нужные адреса") activeScope = GameScopeListsButton;
        else if (_selectedGameScope == "Больше адресов") activeScope = GameScopeExtendedButton;
        else if (_selectedGameScope == "Максимальный охват") activeScope = GameScopeAllButton;

        if (activeScope != null)
        {
            string borderBrush = activeScope == GameScopeListsButton ? "SuccessBrush" : "WarningBrush";
            string bgBrush = activeScope == GameScopeListsButton ? "SuccessTintBrush" : "WarningTintBrush";
            activeScope.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, borderBrush);
            activeScope.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, bgBrush);
        }
    }

    private class ModernTrayMenuRenderer : WinForms.ToolStripProfessionalRenderer
    {
        private readonly bool _isDark;
        public ModernTrayMenuRenderer(bool isDark) : base(new ModernColorTable(isDark))
        {
            _isDark = isDark;
        }

        protected override void OnRenderItemText(WinForms.ToolStripItemTextRenderEventArgs e)
        {
            bool isSelectedProfile = e.Item.Tag as string == "SelectedProfile";

            if (isSelectedProfile)
            {
                e.TextColor = _isDark ? System.Drawing.Color.FromArgb(120, 180, 255) : System.Drawing.Color.FromArgb(0, 100, 210);
            }
            else
            {
                e.TextColor = _isDark ? System.Drawing.Color.FromArgb(240, 240, 240) : System.Drawing.Color.FromArgb(20, 20, 20);
            }

            if (!e.Item.Enabled)
            {
                e.TextColor = _isDark ? System.Drawing.Color.FromArgb(120, 120, 120) : System.Drawing.Color.FromArgb(160, 160, 160);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(WinForms.ToolStripItemRenderEventArgs e)
        {
            bool isSelectedProfile = e.Item.Tag as string == "SelectedProfile";

            if (e.Item.Selected && e.Item.Enabled)
            {
                if (isSelectedProfile)
                {
                    using var brush = new System.Drawing.SolidBrush(_isDark ? System.Drawing.Color.FromArgb(45, 55, 75) : System.Drawing.Color.FromArgb(220, 230, 250));
                    e.Graphics.FillRectangle(brush, e.Item.ContentRectangle);
                }
                else
                {
                    using var brush = new System.Drawing.SolidBrush(_isDark ? System.Drawing.Color.FromArgb(50, 50, 50) : System.Drawing.Color.FromArgb(235, 235, 235));
                    e.Graphics.FillRectangle(brush, e.Item.ContentRectangle);
                }
            }
            else
            {
                if (isSelectedProfile)
                {
                    using var brush = new System.Drawing.SolidBrush(_isDark ? System.Drawing.Color.FromArgb(35, 42, 55) : System.Drawing.Color.FromArgb(235, 242, 255));
                    e.Graphics.FillRectangle(brush, e.Item.ContentRectangle);
                }
                else
                {
                    using var brush = new System.Drawing.SolidBrush(_isDark ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(250, 250, 250));
                    e.Graphics.FillRectangle(brush, e.Item.ContentRectangle);
                }
            }
        }

        protected override void OnRenderToolStripBackground(WinForms.ToolStripRenderEventArgs e)
        {
            using var brush = new System.Drawing.SolidBrush(_isDark ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(250, 250, 250));
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(WinForms.ToolStripRenderEventArgs e)
        {
            using var pen = new System.Drawing.Pen(_isDark ? System.Drawing.Color.FromArgb(55, 55, 55) : System.Drawing.Color.FromArgb(215, 215, 215));
            var rect = new System.Drawing.Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }

        protected override void OnRenderSeparator(WinForms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.ContentRectangle.Height / 2;
            using var pen = new System.Drawing.Pen(_isDark ? System.Drawing.Color.FromArgb(55, 55, 55) : System.Drawing.Color.FromArgb(220, 220, 220));
            e.Graphics.DrawLine(pen, 12, y, e.Item.ContentRectangle.Right - 12, y);
        }
    }

    private class ModernColorTable : WinForms.ProfessionalColorTable
    {
        private readonly bool _isDark;
        public ModernColorTable(bool isDark) { _isDark = isDark; }
        public override System.Drawing.Color ToolStripDropDownBackground => _isDark ? System.Drawing.Color.FromArgb(32, 32, 32) : System.Drawing.Color.FromArgb(250, 250, 250);
        public override System.Drawing.Color MenuBorder => _isDark ? System.Drawing.Color.FromArgb(55, 55, 55) : System.Drawing.Color.FromArgb(215, 215, 215);
        public override System.Drawing.Color MenuItemSelected => _isDark ? System.Drawing.Color.FromArgb(50, 50, 50) : System.Drawing.Color.FromArgb(235, 235, 235);
        public override System.Drawing.Color MenuItemBorder => System.Drawing.Color.Transparent;
        public override System.Drawing.Color ImageMarginGradientBegin => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientMiddle => ToolStripDropDownBackground;
        public override System.Drawing.Color ImageMarginGradientEnd => ToolStripDropDownBackground;
    }
}


