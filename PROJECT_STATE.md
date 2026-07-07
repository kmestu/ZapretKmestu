# PROJECT_STATE — Zapret Kmestu

Last updated: 2026-06-04

Purpose: main context anchor for the ChatGPT Project "Zapret Kmestu".

Status: active WPF/.NET 8 desktop app, stabilized after DEBUG preview hub.

This file helps a new ChatGPT chat understand the current project state, workflow, safety rules, stable commits, and next steps without rereading the full conversation.

---

## 1. Current stable state

Project path: C:\Dev\ZapretKmestu

Solution: C:\Dev\ZapretKmestu\ZapretKmestu.sln

App project: C:\Dev\ZapretKmestu\src\ZapretKmestu.App

Current latest commits:

- 74d4996 Add UI-only work mode selection
- f221e71 Refine diagnostics VPN context
- 6e79bb0 Update project state after diagnostics dashboard
- 93f342b Add compact diagnostics dashboard
- c366798 Run startup diagnostics when bypass is active
- a08344b Clarify VPN diagnostics wording
- 2c8d0fb Add debug all-failed comparison preview
- 73714b2 Update project state after debug stress preview
- dded44e Add debug comparison stress preview
- 9bf8d18 Update project state after scrollbar polish
- 672846e Hide comparison scrollbar when not needed
- 3a155e1 Update project state after status badge polish
- e3da25d Polish comparison status badge alignment
- 72d57da Update project state after auto-pick persistence
- 743cefa Persist last auto-pick results
- e4b4db1 Show auto-pick final results table
- 963763a Update project state after DEBUG hub
- 5872d00 Add DEBUG preview hub
- c8e7a00 Fix project state markdown formatting
- bb0d500 Add current project state
- 693156e Add dark tray menu styling
- e471f22 Limit manual profile dropdown height
- 67f93ae Polish auto-pick layout and results access

Expected repository state:

- git status --short is empty.

Expected build state:

- dotnet build C:\Dev\ZapretKmestu\ZapretKmestu.sln = 0 warnings / 0 errors.
- dotnet build C:\Dev\ZapretKmestu\ZapretKmestu.sln -c Release = 0 warnings / 0 errors.
- git diff --check is clean.

Current stable HEAD:

- 74d4996 Add UI-only work mode selection


---

## 2. Project identity

App name: Zapret Kmestu

Type: Windows GUI wrapper for Flowseal zapret-discord-youtube

Stack: .NET 8, WPF, C#

Requires admin: yes

Zapret Kmestu is a GUI shell for Flowseal zapret-discord-youtube.

It must not:

- replace zapret;
- invent its own network bypass engine;
- diverge from Flowseal service/profile logic without explicit audit and approval.

The app requires administrator privileges because it manages Windows services and zapret/network-related behavior.

The app manifest should require administrator execution:

- requestedExecutionLevel level="requireAdministrator" uiAccess="false"

---

## 3. Current implemented state

Stable UI/features already implemented:

- Comparison overlay is implemented.
- Comparison overlay sorting is implemented:
  - YouTube;
  - Discord;
  - Status.
- "Последний подбор" button is placed near manual profile selection.
- "Последний подбор" is always visible:
  - disabled before auto-pick results exist;
  - enabled after auto-pick results exist (including after app restart if a real auto-pick completed).
- "Проверки" column should no longer show false 0/0 when real probes exist.
- Auto-pick layout no longer clips the bottom block.
- Progress card styling is aligned with other cards.
- "?" help marker near "Профиль обхода" is added.
- Manual profile dropdown height is limited and shows 8 full rows.
- Tray menu in dark theme is styled and readable.
- Dark tray menu selected/highlighted items remain readable.
- DEBUG preview hub is implemented and committed.
- DEBUG hub opens by Ctrl+Shift+Alt+D.
- DEBUG hub is a custom dark popup/palette, not nested overlay dialogs.
- DEBUG hub has fixed size and does not jump when switching sections.
- DEBUG hub text is in Russian.
- DEBUG hub includes easter egg warning:
  - "Ты нашел секретное меню, здесь лучше ничего не трогать."
- DEBUG hub shows real current indicators:
  - preview state;
  - zapret installed state;
  - selected profile;
  - last check time;
  - last result count;
  - wizard running state;
  - operation running state.
- DEBUG hub sections:
  - Сравнение профилей;
  - Главный экран;
  - Диагностика;
  - Автоподбор;
  - Оверлеи;
  - Сброс.
- DEBUG hub can preview mock comparison results for 6, 8, and 12 profiles.
- DEBUG hub can preview main screen states.
- DEBUG hub can preview diagnostics states.
- DEBUG hub can preview auto-pick states.
- DEBUG hub can show safe no-op overlay previews.
- DEBUG hub can reset preview back to real state.
- DEBUG hub was implemented without XAML changes.
- Final auto-pick result overlay is reworked to show the real comparison table directly at the end of auto-pick with clear actions without clipping.
- Last real auto-pick results are persisted across app restarts.
- Status badge text vertical alignment in the comparison overlay was polished:
  - Visual-only fix by adding top `Margin` to the dynamic `statusText` `TextBlock` in `MainWindow.xaml.cs`.
  - No XAML files were changed.
  - No sorting, service logic, persistence logic, or scoring logic was changed.
  - Passed Debug and Release builds with 0 warnings / 0 errors.
  - Visual check passed through the DEBUG hub comparison preview.
- The comparison overlay no longer shows the white/native scrollbar when results fit without scrolling:
  - Scrollbar is hidden dynamically only when all rows fit in the current visible viewport (7 profiles or fewer).
  - Scrollbar remains visible dynamically for overflow cases (such as 8 or 12 profiles) to avoid hidden scrollable content without visual affordance.
  - Column alignment remains perfectly correct in both cases.
  - White/native scrollbar for longer lists is accepted for now as a safe compromise; full custom dark thin scrollbar remains postponed because it requires ScrollBar/ScrollViewer ControlTemplates.
  - Safe implementation in `MainWindow.xaml.cs` (toggling `VerticalScrollBarVisibility` and adjusting the header `scrollComp` grid column width dynamically) without any XAML changes.
  - No custom ScrollBar/ScrollViewer ControlTemplates were added or edited.
  - Visual check passed through the DEBUG hub comparison previews:
    - 6/7 profiles: no white scrollbar.
    - 8/12 profiles: scrollbar remains and table alignment is OK.
  - Debug and Release builds successfully passed with 0 warnings / 0 errors.
- DEBUG hub now includes a comparison stress preview ("Стресс-тест: длинные имена и сбои"):
  - The new preview is DEBUG-only, implemented safely in C# inside `#if DEBUG` in `MainWindow.xaml.cs`.
  - The stress preview uses long profile names, partial results, failing results, and exactly one winner (8 results total).
  - Visual check passed: long names are wrapped/ellipsized and do not break columns; status badges remain aligned; bottom Back button is not clipped; row layout stays stable.
  - Completely safe: uses existing comparison overlay rendering, does not start/stop/install/repair zapret, does not write settings, does not save mock results to disk, and does not change real scoring, sorting, persistence, service logic, or XAML.
- DEBUG hub now includes an all-failed comparison preview ("Сбой: все проверки провалены"):
  - The new preview is DEBUG-only, implemented safely in C# inside `#if DEBUG` in `MainWindow.xaml.cs`.
  - The preview generates 3 failed profiles where all YouTube and Discord availability flags are false and scores are 0, with no row marked as a winner.
  - It includes a visual edge case with `TotalProbes = 0` to verify fallback stability text behavior safely without touching model code or changing StabilityText.
  - Visual check passed: exactly 3 rows are visible and fit without a scrollbar; no row is marked "Лучший" (all show "Не работает" in red status badges); TotalProbes = 0 displays safely and does not break the layout; bottom Back button is fully visible and not clipped; table columns remain perfectly aligned.
  - Completely safe: no XAML changes, no changes to ProfileCheckResult.cs, no settings files written, and no service/diagnostic/scoring logic touched.
- VPN status wording and diagnostics contradiction on Home (Stage A1) was resolved:
  - VPN wording contradiction on Home was fixed.
  - Hero no longer says "VPN мешает".
  - When bypass is running and VPN-like adapter is detected, Hero keeps "Обход включён" and uses "VPN может менять маршрут".
  - Home diagnostics avoids claiming that zapret definitely helped when VPN is active.
  - Accepted diagnostic wording: "Сервисы отвечают", "Проверка успешна.", "С VPN нельзя понять, помог zapret или VPN."
  - No XAML changes.
  - No service logic changes.
  - No auto-pick scoring/candidate order/tie-breaking changes.
  - Debug and Release builds passed with 0 warnings / 0 errors.
  - Forbidden string checks passed.
- Diagnostics Stage A2 was completed:
  - If the app starts while bypass is already running, Home diagnostics now runs a one-time delayed silent check.
  - This avoids staying up to 60 seconds in "Проверка ещё не запускалась".
  - The "Проверить сейчас" button remains available for manual refresh.
  - _autoCheckTimer interval and tick logic were not changed.
  - No XAML changes.
  - No service logic changes.
  - No auto-pick scoring/candidate order/tie-breaking changes.
  - Debug and Release builds passed with 0 warnings / 0 errors.
  - Forbidden string checks passed.
- Reworked Diagnostics into a separate, dedicated compact Diagnostics page (Stage A3/Dashboard):
  - Added separate Diagnostics navigation page, removing visible diagnostics from Home.
  - Designed the page as a premium, compact dashboard that fits on one screen without vertical scrolling.
  - Included elements: top summary card, compact YouTube/Discord status cards, context panel, network metrics placeholders, and an enlarged stability graph placeholder.
  - Visual clutter was removed: no visible bottom result/action card, and no visible manual refresh/action button.
  - Smooth refresh behavior: automatic diagnostics updates no longer cause layout jitter or text movement.
  - Technical compatibility preserved in XAML: hidden/collapsed container keeps required compatibility elements (`CheckConnectionButton`, `CheckConnectionButtonText`) active for code-behind logic.
  - Restored `UninstallAppButton` in Settings / Maintenance after resolving XAML tag nesting issues.
  - Kept all necessary XAML control names intact: `PageDiag`, `NavDiagButton`, `DiagTitleText`, `DiagDescText`, `YouTubeStatusText`, `DiscordStatusText`, `LastCheckText`, `CheckConnectionButton`, `CheckConnectionButtonText`.
  - Work Modes UI-only selection is implemented in Settings:
    - The approved Work Modes card design/layout is preserved.
    - Work Mode cards are clickable.
    - Selected card has visual highlight.
    - Selection is in-memory only and is not saved after restart.
    - No AppSettings.WorkMode was added.
    - No persistence was added.
    - No Flowseal/zapret service/profile behavior was changed.
    - No auto-pick scoring/candidate order/tie-breaking was changed.
    - Anti-jitter rule: selected-state must not change layout-affecting properties: Width, Height, Margin, Padding, BorderThickness, FontSize, ScaleTransform, position.
    - BorderThickness must remain constant for all Work Mode cards in all states.
    - Highlight may use only non-layout visual properties such as BorderBrush, background/overlay opacity, and subtle glow.
- Help page implementation:
  - Help page added to the left navigation panel as "Помощь" using the existing document/log icon style for visual consistency.
  - Old left navigation "Журнал" button has been removed from the main sidebar; the journal remains fully accessible inside the Diagnostics page.
  - Help page features a compact segmented mode selector:
    - **Новичок** (Beginner mode)
    - **Эксперт** (Expert mode)
  - Beginner content is grouped into thematic sections:
    - Что это такое
    - Первый запуск
    - Профили и автоподбор
    - Понять результат
  - Expert content is grouped into thematic sections:
    - Диагностика
    - Настройки
    - Обновления и восстановление
    - Журнал и отчёт
  - Category badges (`WrapPanel` with styled, rounded tag borders) are displayed under the selector to show categories included in the active mode.
  - FAQ list uses a scoped, local `HelpFaqExpanderStyle` for clean, animated expansion transitions.
  - Accordion logic ensures only one FAQ question/expander can be open at any given time.
  - Build compiles cleanly with 0 warnings and 0 errors (`dotnet build C:\Dev\ZapretKmestu\ZapretKmestu.sln`).


---

## 4. DEBUG hub safety contract

DEBUG hub is for visual preview only.

It must not:

- start zapret;
- stop zapret;
- install zapret;
- repair zapret;
- reinstall service;
- change selected profile;
- write settings;
- call SettingsService.Save;
- call _serviceManager.StartAsync;
- call _serviceManager.StopAsync;
- call _serviceManager.ReinstallAsync;
- call _serviceManager.PrepareFlowsealLikeEnvironmentAsync;
- call installer/update/download logic;
- call ExecuteNetworkDiagnosticAsync;
- call CheckServiceAvailabilityAsync;
- touch scoring/candidate order/tie-breaking;
- permanently overwrite real _lastWizardResults;
- permanently overwrite real _lastWizardResult.

Allowed DEBUG behavior:

- change visible UI state in memory;
- show mock comparison results;
- show safe overlay previews with no-op callbacks;
- restore real preview state through reset;
- read current settings fields for display;
- read current in-memory fields for display.

DEBUG hub should remain DEBUG-only.

No visible DEBUG button should be added.

Shortcut:

- Ctrl+Shift+Alt+D

---

## 5. Auto-pick stored results

- Last real auto-pick results are now persisted across app restarts.
- Only one last real result set is stored.
- No history is created.
- DEBUG/mock results are not saved.
- Results are stored in:
  %AppData%\Zapret Kmestu\last_autopick.json
- After restart, “Последний подбор” is active if a real auto-pick was completed before.
- The button opens the existing comparison overlay using the persisted results.
- Opening last results does not rerun checks.
- Opening last results does not start/stop/reinstall zapret.
- Opening last results does not change selected profile.
- _lastWizardResult exists for compatibility.
- _lastWizardResults stores the last successful set of auto-pick results in memory and on disk.
- IsWinner is display metadata only.
- Stored results must not influence scoring or profile selection.
- Manual runtime test passed.

Comparison overlay accepted behavior:

- Best row first.
- Only one "Лучший".
- Full profile names.
- YouTube/Discord statuses.
- Colored statuses.
- Row click opens details.
- Bottom button must not be clipped.
- No duplicate bottom buttons.
- Existing MainOverlay dialogs must not break.

---

## 6. Remaining backlog

Priority 1 — Expand DEBUG hub gradually

The base DEBUG hub exists and works.

Future additions should be small and separate:

- more main screen states;
- more diagnostics edge cases;
- more safe overlay previews;
- more comparison data shapes;
- visual stress cases;
- long profile names;
- empty/error states;
- light theme check if needed;
- compact/overflow behavior if needed.

Do not expand DEBUG hub with service-changing actions.

---

Priority 2 — Thin scrollbar

Thin scrollbar styling is postponed.

The unnecessary native/white scrollbar appearing for short lists (7 profiles or fewer, where all rows fit visibly) has been successfully resolved using safe, template-free, dynamic visibility logic in MainWindow.xaml.cs.

Do not attempt full thin scrollbar styling without separate read-only audit because it requires ScrollBar/ScrollViewer ControlTemplates.

Past custom ScrollBar/ScrollViewer templates caused app launch failures.

---

## 7. ChatGPT / Antigravity workflow

Antigravity version: 2.0.1

Core rule:

- Every implementation prompt must include: DO NOT COMMIT.

Do not rely on Accept all. Antigravity may apply changes directly to the working tree.

Division of labor:

ChatGPT:

- acts as technical lead;
- writes precise Antigravity prompts;
- reviews Antigravity reports, diffs, screenshots, build output;
- decides whether to continue, fix, commit, or reset.

Antigravity:

- used for focused code changes;
- used for read-only audits;
- should not be used for routine Git/build/commit when the user can run them manually;
- must not commit.

User:

- runs Git/build/commit manually when practical;
- sends screenshots/reports if Antigravity asks for unexpected permissions;
- does not use Codex.

---

## 8. Normal manual workflow

Before any implementation:

- cd C:\Dev\ZapretKmestu
- git status --short

Expected:

- empty output.

After Antigravity implementation:

- cd C:\Dev\ZapretKmestu
- git status --short
- dotnet build C:\Dev\ZapretKmestu\ZapretKmestu.sln
- git diff --stat

If good, also check Release when UI/state work is meaningful:

- dotnet build C:\Dev\ZapretKmestu\ZapretKmestu.sln -c Release

If good, commit specific files only:

- git add <specific files only>
- git commit -m "<short clear message>"
- git status --short
- git log -1 --oneline

Do not use:

- git add .
- git add -A

unless there is a direct explicit reason.

---

## 9. PROJECT_STATE.md update workflow

If PROJECT_STATE.md or another markdown file breaks due to escaping, symbols, or diff confusion, do not repair it with scripts.

Safe workflow:

- ChatGPT generates the full ready file.
- User opens it with Notepad:
  - notepad .\PROJECT_STATE.md
- User does:
  - Ctrl+A
  - Delete
  - paste full generated text
  - Ctrl+S
- Verify only:
  - Get-Content .\PROJECT_STATE.md -TotalCount 12
  - git status --short
- Then commit the file specifically.

Do not use scripted markdown cleanup unless explicitly necessary.

---

## 10. Antigravity model strategy

Default model choices:

- Gemini 3.5 Flash Medium — read-only audit, text/doc updates, cheap checks.
- Gemini 3.5 Flash High — small implementation changes.
- Gemini 3.1 Pro High — risky WPF/XAML/layout/resources/startup/tray/autostart tasks.
- Gemini 3.1 Pro Low — backup for audit.
- Claude Sonnet 4.6 Thinking — difficult WPF/XAML or second opinion.
- Claude Opus 4.6 Thinking — avoid unless there is a serious issue.
- GPT-OSS 120B Medium — backup for read-only reasoning.

Do not use old "Gemini 3 Flash" naming.

Do not enable AI Credit Overages by default.

Use Local.

Do not use New Worktree unless explicitly requested.

Do not use Browser, Media, Actions, Scheduled Tasks, or background tasks unless the current task explicitly needs them.

---

## 11. Antigravity safety settings

Recommended project settings:

- Security Preset: Custom
- Outside folders file access: Always Ask
- Terminal Command Auto Execution: Require Review
- Artifact Review Policy: Always Ask
- File Access Rules: empty
- Commands Outside Sandbox: empty

Safe terminal commands that may be allowed case-by-case:

- git status
- git diff
- git log
- git ls-files
- dir
- findstr
- Select-String
- Get-ChildItem
- Get-Content
- Test-Path

Do not broadly allow:

- cmd
- powershell
- git
- dotnet

Dangerous commands should stay denied unless explicitly needed for a narrow task:

- git rm
- git reset
- git clean
- git checkout
- del
- Remove-Item
- rmdir
- rd
- sc
- schtasks
- taskkill
- Stop-Process
- dotnet publish

If Antigravity asks for an unexpected command, stop and send a screenshot to ChatGPT.

If Antigravity asks to run a broad command like:

- dotnet build

prefer denying and asking it to use the exact solution build:

- dotnet build C:\Dev\ZapretKmestu\ZapretKmestu.sln

---

## 12. Main commands

Build Debug:

- dotnet build C:\Dev\ZapretKmestu\ZapretKmestu.sln

Expected:

- 0 Warning(s)
- 0 Error(s)

Build Release:

- dotnet build C:\Dev\ZapretKmestu\ZapretKmestu.sln -c Release

Expected:

- 0 Warning(s)
- 0 Error(s)

Run normally:

- dotnet run --project C:\Dev\ZapretKmestu\src\ZapretKmestu.App\ZapretKmestu.App.csproj

Run tray mode:

- dotnet run --project C:\Dev\ZapretKmestu\src\ZapretKmestu.App\ZapretKmestu.App.csproj -- --tray

Stop existing app process:

- Get-Process ZapretKmestu* -ErrorAction SilentlyContinue | Stop-Process -Force

Publish portable:

Do not run unless the user explicitly asks to work on portable/release.

- dotnet publish C:\Dev\ZapretKmestu\src\ZapretKmestu.App\ZapretKmestu.App.csproj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -o C:\Dev\ZapretKmestu\release\portable

---

## 13. Critical safety rules

Do not add or reintroduce:

- MessageBox
- HomeAlert
- StartupUri
- ProgressShimmer
- HeroAlertRing
- HeroStatusSheen
- HeroGlossCap
- HeroGlossDepthShade
- HeroGlossOverlay

Do not touch without explicit task and usually read-only audit first:

- App.xaml.cs theme dictionary logic
- App.xaml
- Themes/*.xaml
- ScrollBar/ScrollViewer ControlTemplates
- HeroGlint
- tray lifecycle
- Task Scheduler/autostart
- portable/release/publish metadata
- zapret install/update/download logic
- service start/stop/reinstall logic
- auto-pick scoring/candidate order/tie-breaking

Reject or reset changes if Antigravity unexpectedly modifies:

- App.xaml.cs
- App.xaml
- Themes/*.xaml
- ScrollBar/ScrollViewer templates
- HeroGlint
- service start/stop logic
- tray/autostart
- portable/release
- .csproj without direct reason

Build must remain:

- 0 warnings / 0 errors

---

## 14. Standard safety checks

Run after meaningful changes:

- Select-String -Path "C:\Dev\ZapretKmestu\src\ZapretKmestu.App\*.cs", "C:\Dev\ZapretKmestu\src\ZapretKmestu.App\*.xaml" -Pattern "MessageBox", "HomeAlert"
- Select-String -Path "C:\Dev\ZapretKmestu\src\ZapretKmestu.App\App.xaml" -Pattern "StartupUri"
- Select-String -Path "C:\Dev\ZapretKmestu\src\ZapretKmestu.App\*.cs", "C:\Dev\ZapretKmestu\src\ZapretKmestu.App\*.xaml" -Pattern "HeroAlertRing", "HeroStatusSheen", "HeroGlossCap", "HeroGlossDepthShade", "HeroGlossOverlay", "ProgressShimmer"

Expected:

- no MessageBox;
- no HomeAlert;
- no StartupUri;
- no old forbidden hero/progress animation names.

---

## 15. Key files to keep updated in ChatGPT sources

Upload current versions after important commits:

- C:\Dev\ZapretKmestu\PROJECT_STATE.md
- C:\Dev\ZapretKmestu\.gitignore
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\MainWindow.xaml
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\MainWindow.xaml.cs
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\App.xaml
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\App.xaml.cs
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\ZapretKmestu.App.csproj
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\app.manifest
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\Models\ProfileCheckResult.cs
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\Services\AutostartService.cs
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\Services\AppLogger.cs
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\Services\ZapretServiceManager.cs
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\Services\WindowTitleBarService.cs
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\Themes\DarkTheme.xaml
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\Themes\LightTheme.xaml
- C:\Dev\ZapretKmestu\src\ZapretKmestu.App\Themes\Icons.xaml

Do not upload generated folders unless needed for a specific issue:

- bin
- obj
- release
- portable
- .vs
- .git
- *.zip

If uploaded code conflicts with this file, trust the actual code.

---

## 16. General app state

- Base WPF app exists and works.
- Modern light/dark UI exists.
- App requires administrator rights.
- Build should be 0 warnings / 0 errors.
- MessageBox was removed.
- Old HomeAlert was removed.
- StartupUri was removed from App.xaml.
- MainWindow is created manually in App.xaml.cs.
- Single Instance Guard exists.
- --tray launch should not show the main window.

---

## 17. Startup, tray, autostart

Tray behavior exists:

- X hides window to tray when enabled.
- Tray exit closes app.
- Optional setting can stop bypass on app exit.
- Duplicate tray icons were fixed previously.
- Tray icon changes by state.
- Dark tray menu is styled and readable.

Do not touch tray lifecycle without direct task and usually audit first.

Autostart:

- GUI autostart through Task Scheduler exists.
- Do not touch Task Scheduler/autostart without direct task.
- Old startup folder shortcut is not used.

---

## 18. Diagnostics

- Diagnostics is redesigned into a separate, dedicated premium page, simplifying the Home layout.
- The page is a compact minimal dashboard fitting entirely on one screen without vertical scrolling.
- Elements: top summary card, compact YouTube/Discord status cards, context panel, network metrics placeholders, and an enlarged stability graph placeholder.
- No visible bottom result/action card, and no visible manual check/refresh button (refresh runs silently/automatically).
- Automatic diagnostics updates run without layout jitter or text-movement reflow.
- Technical controls required by code-behind (such as `CheckConnectionButton` and `CheckConnectionButtonText`) are preserved inside a hidden/collapsed container.
- Auto-check timer exists and network checks do not overlap.
- Auto-pick shows in-progress diagnostics, not red unavailable state during checks.
- DEBUG hub can preview diagnostics states without running network checks.

Diagnostic states should remain understandable:

- not checked;
- checking;
- connection works;
- services unavailable;
- partial access;
- VPN warning.

---

## 19. Journal

- Journal is user-friendly.
- Full log remains available.
- Technical noise is filtered from visible UI.
- Do not change journal without direct task.

---

## 20. Hero status and animations

Do not change HeroGlint without direct task.

Accepted states:

- Не установлен
- Обход выключен
- Обход включён
- VPN мешает
- Подбор профиля
- Установка
- Обновление
- Ремонт
- Отмена

Accepted effects:

- Wizard/installing: blue pulse.
- Running: green breathing/glow.
- Not installed: purple/indigo state.
- Stopped/warning: HeroGlint.

Known accepted elements/methods:

- HeroGlintHost
- HeroGlintSoftLayer
- HeroGlintCoreLayer
- HeroGlintSoftBrushTransform
- HeroGlintCoreBrushTransform
- StartHeroGlint
- StopHeroGlint

Do not reintroduce white blinking icon transitions or progress shimmer.

---

## 21. Auto-pick profile selection

Current known behavior:

- Fast mode checks recommended profiles first:
  - ALT11;
  - ALT10;
  - ALT9;
  - ALT8;
  - ALT7;
  - ALT6;
  - then others.
- Fast mode uses shorter waits/probes and stops early on a perfect 100 score.
- Accurate mode checks all profiles and does stronger probes.
- Winner selection uses strict "greater than" comparison:
  - if bestResult is null or currentResult.TotalScore > bestResult.TotalScore, currentResult becomes bestResult.

Important:

- Equal score keeps the first candidate.
- Do not change scoring, candidate order, or tie-breaking without separate read-only audit and explicit approval.

---

## 22. Overlay button rule

Global overlay rule:

- Any overlay button with null/empty/whitespace text should be collapsed, not visible.

Do not break existing overlays:

- quick/accurate mode selection;
- repair confirmation;
- update/install prompts;
- generic info/error overlays;
- comparison overlay;
- last auto-pick results overlay/details;
- safe DEBUG overlay previews.

---

## 23. Known major past failures

MainWindow.xaml breakage:

- Project was recovered from backup: C:\Dev\ZapretKmestu_BACKUP_hero_glint_ok

Lessons:

- no broad XAML changes;
- small stages only;
- no weak model for wide XAML edits.

XamlParseException for IconLightning:

Cause:

- theme switching cleared Icons.xaml.

Lesson:

- preserve base resource dictionaries;
- do not casually edit App.xaml.cs theme logic.

ScrollBar/ScrollViewer template crash:

Cause:

- custom ScrollBar/ScrollViewer styles/templates broke app launch.

Lesson:

- do not create/edit ScrollBar/ScrollViewer ControlTemplates without read-only audit and strong model.

Hero icon blinking:

Cause:

- fade/scale icon transition.

Lesson:

- do not reintroduce icon transition without separate discussion.

Progress shimmer crash:

Cause:

- shimmer/progress XAML caused broken state.

Lesson:

- do not add ProgressShimmer.

PROJECT_STATE.md formatting breakage:

Cause:

- markdown escaping / symbol cleanup through scripts.

Lesson:

- replace full file through Notepad with generated plain text;
- do not patch markdown state files with cleanup scripts unless necessary.

Diagnostics stage layout / build breaks:

Cause:

- Broad XAML replacement and mismatched tags caused build failures (MC3000, MC3074, CS0103) and duplicate/missing control names during the PageDiag and PageSettings rework.

Lesson:

- Do not run git checkout/restore/reset from Antigravity.
- Do not allow full MainWindow.xaml diffs in Antigravity unless absolutely needed; they flood the UI.
- Avoid broad XAML replacements; prefer small editor-based edits.
- Keep PROJECT_STATE.md edits separate from UI/code commits.

---

## 24. Antigravity duplicated project entries

Antigravity may show many ZapretKmestu N project records.

Current rule:

- Work only in ZapretKmestu MAIN.
- Treat duplicated entries as Antigravity app metadata, not project backups.
- Do not manually edit/delete Antigravity AppData without a separate backup and explicit plan.
- Do not create New Worktree unless explicitly requested.

---

## 25. Next recommended work

Next work item:

- The next likely direction is Work Modes backend integration or VPN compatibility research.

Recommended first step:

- Do not start service/autostart/ScrollViewer template work without audit.
- Completed: Stage A1 & A2 (VPN diagnostics / Delayed startup check), separate compact Diagnostics Dashboard (Stage A3), and UI-only in-memory Work Modes selection with visual highlight and anti-jitter constraints (Stage B1).
- Current status: Stable HEAD (commit 74d4996 / 744d996 Add UI-only work mode selection).
- Next work item: Work Modes backend integration or VPN compatibility research.

---

## 26. One-line current summary

Zapret Kmestu is a stabilized .NET 8 WPF GUI wrapper for Flowseal zapret with a newly designed Help page ("Помощь" in left nav, old "Журнал" button removed, compact segmented "Новичок / Эксперт" control, local animated accordion `HelpFaqExpanderStyle` ensuring only one item is open at a time, category badges under the switcher), in-memory UI-only Work Modes selection in Settings (clickable cards, non-layout highlight overlay, constant BorderThickness to avoid jitter, no persistence, no AppSettings.WorkMode), a separate premium compact Diagnostics page (fits on one screen, zero layout jitter, no visible manual refresh/action card, tech compatibility buttons hidden, simplified Home page), comparison overlay, sorted results, persistent last auto-pick results, dark tray menu, template-free comparison scrollbar hiding, honest VPN status/diagnostics, and 0 warnings / 0 errors.