# Flowseal Zapret Integration Analysis

## Summary
This document analyzes the release structure and scripts of `Flowseal/zapret-discord-youtube` to facilitate its integration into the planned Windows WPF application **Zapret Kmestu**. The analysis is strictly read-only and details how the official scripts operate, identifying integration points and risks.

## Latest release
- **Tag**: 1.9.8c
- **Release Name**: 1.9.8c
- **Publication Date**: 2026-05-07T11:21:05Z

## Downloaded asset
- **Name**: `zapret-discord-youtube-1.9.8c.zip`
- **Size**: 1,433,813 bytes
- **SHA256**: `49c2901d329c9ef3747c48a9999e73c3fa2fb050aed126b91cac02c6bbea8618`
- **URL**: `https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.9.8c/zapret-discord-youtube-1.9.8c.zip`

## Archive structure
The extracted `.zip` contains no parent folder (files are extracted at the root of the archive).
- **Subdirectories**: `bin`, `lists`, `utils`
- **Root**: `service.bat` and multiple `general*.bat` files representing strategies.

## Important files
- `service.bat`: The main menu and script manager for installing/removing the service.
- `bin\winws.exe`: The core Zapret bypass executable.
- `bin\WinDivert.dll` / `WinDivert64.sys`: Network packet interception driver used by `winws.exe`.
- `lists\ipset-all.txt`, `list-general.txt`: Default route/domain lists for bypassing.
- `utils\test zapret.ps1`: A diagnostic tool to test configurations.
- `bin\*.bin`: Pre-compiled TLS/QUIC ClientHello payloads.

## Available .bat/.cmd scripts
- `service.bat`
- `general.bat`
- `general (ALT).bat`, `general (ALT2).bat` to `general (ALT11).bat`
- `general (FAKE TLS AUTO).bat`, `general (FAKE TLS AUTO ALT).bat` to `general (FAKE TLS AUTO ALT3).bat`
- `general (SIMPLE FAKE).bat`, `general (SIMPLE FAKE ALT).bat`, `general (SIMPLE FAKE ALT2).bat`

## Available profiles/strategies
The batch scripts in the root directory serve as different user-facing strategies. Based on filenames:
- **General / Alternatives**: Standard DPI bypass strategies (`general.bat`, `general (ALT*).bat`). 
- **Fake TLS Auto**: Strategies that utilize dynamic fake TLS tactics (`general (FAKE TLS AUTO*).bat`).
- **Simple Fake**: Lighter or more basic fake packet strategies (`general (SIMPLE FAKE*).bat`).

## service.bat behavior
- **Service Creation**: Reads the selected `.bat` strategy file, parses the arguments intended for `winws.exe`, replaces placeholders (like `%BIN%`, `%LISTS%`), and executes `sc create zapret binPath= "...\winws.exe <ARGS>"` with start=auto.
- **Start/Stop/Remove**: Uses `net stop zapret`, `sc delete zapret` to remove the service. Uses `taskkill /IM winws.exe` and stops/removes the `WinDivert` and `WinDivert14` services.
- **Strategy Storage**: The selected strategy's filename is stored in the registry under `HKLM\System\CurrentControlSet\Services\zapret` -> `zapret-discord-youtube` (REG_SZ).
- **Status Check**: Uses `sc query "zapret"`, queries the registry for the saved strategy, and checks if `winws.exe` is running using `tasklist`.
- **Registry**: It queries and writes to `HKLM\System\CurrentControlSet\Services\zapret` and reads from `HKCU\Software\Microsoft\Windows\CurrentVersion\Internet Settings` for Proxy status.
- **sc.exe & WinDivert**: Uses `sc.exe` heavily. It tests the `WinDivert` service but relies on `winws.exe` to load the driver natively.

## Diagnostics behavior
Triggered via `:service_diagnostics`.
- Validates the existence of the Base Filtering Engine (BFE).
- Checks if a system proxy is enabled (warns if true).
- Ensures TCP timestamps are enabled, otherwise enables them via `netsh`.
- Scans running processes and services for known conflicting software (AdguardSvc, Killer, Intel Connectivity Network Service, Check Point, SmartByte, VPNs).
- Validates secure DNS presence in the registry.
- Scans `%SystemRoot%\System32\drivers\etc\hosts` for entries related to `youtube.com` or `youtu.be`.
- Prompts the user to clear Discord caches and forcibly kills Discord if approved.

## Update behavior
- Triggered via `:service_check_updates`.
- Fetches `https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/main/.service/version.txt` via PowerShell/Invoke-WebRequest.
- Compares the remote version with the internal variable `LOCAL_VERSION=1.9.8c`.
- If a new version exists, it automatically opens the default browser to the GitHub releases page.
- Note: It also updates `ipset-all.txt` by downloading it from the repo (using curl or powershell) and checks the `hosts` file via a downloaded temp file but requires manual notepad-based copying for hosts.

## Service behavior
The Windows service named `zapret` runs as `Auto`. It is essentially `winws.exe` running in the background with the specific arguments extracted from the strategy `.bat` file. 

## Files Zapret Kmestu must preserve during updates
- `lists\ipset-exclude-user.txt`
- `lists\list-general-user.txt`
- `lists\list-exclude-user.txt`
- State files: `utils\game_filter.enabled`, `utils\check_updates.enabled`
- The `HKLM\System\CurrentControlSet\Services\zapret` registry key specifying the chosen profile.

## Safe integration recommendations for Zapret Kmestu
1. **Direct Service Management**: Do not invoke `service.bat`. Use native C# `.NET` `ServiceController` and P/Invoke or process calls to `sc.exe` to manage the `zapret` service.
2. **Strategy Parsing**: Re-implement the argument extraction logic from `service.bat` in C# to safely read the `.bat` files and format the arguments for `winws.exe`.
3. **Registry Awareness**: Read the active strategy from the registry to show it in the UI and update the registry when the user changes strategies.
4. **Custom Diagnostics**: Implement the diagnostic checks natively in C# for a cleaner UI experience (e.g., checking conflicting services, proxy settings, hosts file).

## Risks / unknowns
- **Argument Parsing Accuracy**: The batch file variable substitution inside `service.bat` is very complex (using `mergeargs` logic). If the C# parser misses a subtle edge case, the `winws.exe` service might fail to start or operate incorrectly.
- **WinDivert Conflicts**: `winws.exe` will fail silently or visibly if WinDivert is blocked by antivirus or conflicts with other packet-filtering drivers.
- **Permissions**: Creating services and editing `HKLM` requires the WPF app to run as Administrator.

## Suggested next development step
Create the basic C# WPF project skeleton (.NET 8.0/9.0) and begin implementing the C# class responsible for parsing the `general*.bat` files into `winws.exe` argument strings.
