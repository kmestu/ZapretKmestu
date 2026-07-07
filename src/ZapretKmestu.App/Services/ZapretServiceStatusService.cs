using System;
using System.Runtime.InteropServices;
using ZapretKmestu.Models;

namespace ZapretKmestu.Services;

/// <summary>
/// Service for read-only detection of the "zapret" Windows service status.
/// Uses native Windows API (advapi32.dll) to query status without extra NuGet packages.
/// </summary>
public class ZapretServiceStatusService
{
    private const string ServiceName = "zapret";

    // P/Invoke constants for service querying
    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const int SERVICE_RUNNING = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public int dwServiceType;
        public int dwCurrentState;
        public int dwControlsAccepted;
        public int dwWin32ExitCode;
        public int dwServiceSpecificExitCode;
        public int dwCheckPoint;
        public int dwWaitHint;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    /// <summary>
    /// Reads the current status of the "zapret" service.
    /// Handles missing service and permission errors safely.
    /// Does NOT modify the service state.
    /// </summary>
    public ZapretServiceStatusInfo GetStatus()
    {
        IntPtr scm = IntPtr.Zero;
        IntPtr svc = IntPtr.Zero;

        try
        {
            // 1. Connect to Service Control Manager
            scm = OpenSCManager(null!, null!, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero)
            {
                throw new Exception($"Не удалось открыть SCM (Error: {Marshal.GetLastWin32Error()})");
            }

            // 2. Open the service
            svc = OpenService(scm, ServiceName, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 1060) // ERROR_SERVICE_DOES_NOT_EXIST
                {
                    return new ZapretServiceStatusInfo
                    {
                        Exists = false,
                        IsRunning = false,
                        StatusText = "Служба не установлена"
                    };
                }
                throw new Exception($"Не удалось открыть службу (Error: {error})");
            }

            // 3. Query status
            var status = new SERVICE_STATUS();
            if (!QueryServiceStatus(svc, ref status))
            {
                throw new Exception($"Не удалось получить статус (Error: {Marshal.GetLastWin32Error()})");
            }

            bool isRunning = status.dwCurrentState == SERVICE_RUNNING;
            
            return new ZapretServiceStatusInfo
            {
                Exists = true,
                IsRunning = isRunning,
                StatusText = isRunning ? "Обход включён" : "Обход выключен"
            };
        }
        catch (Exception ex)
        {
            AppLogger.Warning($"Ошибка при получении статуса службы: {ex.Message}");
            return new ZapretServiceStatusInfo
            {
                Exists = false,
                IsRunning = false,
                StatusText = "Ошибка службы",
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }
}
