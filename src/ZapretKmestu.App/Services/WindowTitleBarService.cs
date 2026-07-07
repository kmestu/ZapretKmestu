using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ZapretKmestu.Services
{
    /// <summary>
    /// Helper service to apply native Windows title bar theming (Immersive Dark Mode, Windows 11 colors).
    /// </summary>
    public static class WindowTitleBarService
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        // Attributes for DwmSetWindowAttribute
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        // Constants for DWM colors
        private const int DWM_COLOR_DEFAULT = -2;

        /// <summary>
        /// Applies the theme to the window's native title bar.
        /// </summary>
        /// <param name="window">The WPF window.</param>
        /// <param name="isDark">True for dark theme, false for light theme.</param>
        public static void ApplyTheme(Window window, bool isDark)
        {
            if (window == null) return;

            try
            {
                var helper = new WindowInteropHelper(window);
                var hwnd = helper.Handle;

                if (hwnd == IntPtr.Zero)
                {
                    // Handle not yet created. Attach to SourceInitialized to apply when ready.
                    window.SourceInitialized += (s, e) => ApplyTheme(window, isDark);
                    return;
                }

                ApplyNativeTheme(hwnd, isDark);
            }
            catch (Exception ex)
            {
                // Non-critical: log and continue
                AppLogger.Warning($"Не удалось применить тему к заголовку окна: {ex.Message}");
            }
        }

        private static void ApplyNativeTheme(IntPtr hwnd, bool isDark)
        {
            // 1. Immersive Dark Mode (Windows 10 1809+)
            int useImmersiveDarkMode = isDark ? 1 : 0;
            int attr = GetImmersiveDarkModeAttribute();
            
            if (attr != -1)
            {
                int hr = DwmSetWindowAttribute(hwnd, attr, ref useImmersiveDarkMode, sizeof(int));
                if (hr != 0)
                {
                    AppLogger.Warning($"DwmSetWindowAttribute(USE_IMMERSIVE_DARK_MODE) failed with HRESULT 0x{hr:X}");
                }
            }

            // 2. Windows 11 Custom Colors (Build 22000+)
            if (IsWindows11OrGreater())
            {
                if (isDark)
                {
                    // Dark theme: #1C2633 (SurfaceColor), #F5F8FC (TextPrimaryColor), #2C3746 (BorderColor)
                    SetColor(hwnd, DWMWA_CAPTION_COLOR, System.Windows.Media.Color.FromRgb(0x1C, 0x26, 0x33));
                    SetColor(hwnd, DWMWA_TEXT_COLOR, System.Windows.Media.Color.FromRgb(0xF5, 0xF8, 0xFC));
                    SetColor(hwnd, DWMWA_BORDER_COLOR, System.Windows.Media.Color.FromRgb(0x2C, 0x37, 0x46));
                }
                else
                {
                    // Light theme: #FFFFFF (NavBgColor), #111827 (TextPrimaryColor), #D3DEEB (BorderColor)
                    SetColor(hwnd, DWMWA_CAPTION_COLOR, System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF));
                    SetColor(hwnd, DWMWA_TEXT_COLOR, System.Windows.Media.Color.FromRgb(0x11, 0x18, 0x27));
                    SetColor(hwnd, DWMWA_BORDER_COLOR, System.Windows.Media.Color.FromRgb(0xD3, 0xDE, 0xEB));
                }
            }
        }

        private static void SetColor(IntPtr hwnd, int attr, System.Windows.Media.Color color)
        {
            // DWM uses 0x00BBGGRR format (not standard 0x00RRGGBB)
            int colorValue = color.R | (color.G << 8) | (color.B << 16);
            DwmSetWindowAttribute(hwnd, attr, ref colorValue, sizeof(int));
        }

        private static int GetImmersiveDarkModeAttribute()
        {
            // Windows 10 build 18985 is when attribute 20 was introduced
            if (IsWindows10BuildOrGreater(18985)) return DWMWA_USE_IMMERSIVE_DARK_MODE;
            // Windows 10 build 17763 (1809) introduced attribute 19
            if (IsWindows10BuildOrGreater(17763)) return DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1;
            return -1;
        }

        private static bool IsWindows11OrGreater() => IsWindows10BuildOrGreater(22000);

        private static bool IsWindows10BuildOrGreater(int build)
        {
            return Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= build;
        }
    }
}
