# CTRL+Z architecture

## Goals

1. Rebuild a Windows app setup from a portable file.
2. Back up and restore Chromium profiles without relying on browser sync.
3. Keep every long-running operation resumable, logged, and isolated.
4. Stay fully local. No account or network service is required for core features.

## Layers

### `CtrlZ.Core` (`net8.0-windows`)

The core is a normal class library. It has no WPF types and is covered by xUnit tests.

| Area | Responsibility |
| --- | --- |
| `Models` | Serializable profile/install/browser models and enums |
| `Infrastructure` | App data paths, atomic writes, JSON defaults, process runner, file helpers |
| `Logging` | Thread-safe rolling file log with a line event for the UI |
| `Services` | Scan, winget CLI/table parser/matcher, WinUtil catalog, GitHub releases, downloads, browser detection, backup/restore |
| `Install` | Installer strategies and the sequential resumable `InstallEngine` |

#### Scanning

`AppScanner` runs three independent inventories:

1. `WingetCli list` (parsed by `WingetTableParser`)
2. `RegistryAppScanner` (HKLM/HKCU, 32/64-bit Uninstall keys)
3. `StoreAppScanner` (PowerShell `Get-AppxPackage` + `Get-StartApps`)

`WingetMatcher` assigns a package id only above a conservative score threshold. Weak
matches are flagged for review; unmatched entries stay unmatched rather than being
silently installed from the wrong package. `AppScanner` merges duplicate sources and
retains one representative per app/version.

#### Installation

`InstallEngine` is the only component that owns:

- queue order,
- per-item state transitions,
- retry/resume behavior,
- state-file persistence after every transition,
- the install session log.

Installers implement `ISourceInstaller`:

- `WingetSourceInstaller` (winget + Microsoft Store)
- `DirectUrlSourceInstaller`
- `GitHubSourceInstaller`
- `LocalFileSourceInstaller`

All file-based sources share `FileInstallService`, which detects the installer type,
applies custom or recommended silent switches, runs the process without a shell, handles
exit code 3010/1641 as reboot-required, and extracts portable ZIPs.

`InstallState.ProfileSnapshot` freezes the profile at queue creation. A reboot, app crash,
or changing the profile file cannot break a resume.

#### Downloads and verification

`DownloadService` streams to a `.partial` file, reports progress, computes SHA-256, and
deletes the artifact on mismatch. Winget verifies its own package manifests.
`GitHubReleaseService` reads the asset `digest` field when GitHub supplies one, and resolves
the latest release again at install time.

#### Browser module

The browser code is a self-contained service group:

- `ChromiumBrowserCatalog` — per-fork configuration (paths, process names, registry names)
- `ChromiumBrowserDetector` — User Data + registry detection
- `ChromiumProfileReader` — Local State profile names, directory fallback, validation
- `BrowserDataRules` / `BrowserBackupPlanner` — auditable whitelist/exclusions and size estimate
- `BrowserBackupService` — sanitized Local State, sanitized Web Data, ZIP creation/encryption
- `BrowserArchiveReader` — manifest validation, encrypted archive handling
- `BrowserRestoreService` — conflict detection, restore-as-new, safety copy, Local State merge
- `BrowserProcessService` — path-aware running detection and graceful/forced close
- `VolumeShadowCopySession` — best-effort locked-file fallback

The browser data policy is whitelist-first: only known profile files and directories are
enumerated. Passwords and cookies are not just filtered at archive time; they are never
planned in the first place, and restore refuses those paths if a foreign archive contains
them.

## UI (`CtrlZ.App`)

WPF with a hand-written resource system:

- `Themes/Colors.Light.xaml`, `Themes/Colors.Dark.xaml` — the only theme-specific brushes
- `Themes/Typography.xaml` — Segoe UI Variable type scale
- `Themes/Controls.xaml` — buttons, inputs, lists, scrollbars, cards, status pills
- `Themes/Icons.xaml` — coherent Segoe Fluent Icons glyph set
- `Controls/AppIcon.xaml` — shell-extracted real icons + deterministic monogram fallback
- `Services/AppState.cs` — single source of profile truth for all pages
- `Services/AppServices.cs` — composition root
- `Services/ThemeService.cs` — system/light/dark resource swapping + DWM title bar
- `MainWindow.xaml` — custom chrome, sidebar, content host, subtle page fade
- `Views/*` — page views and modal dialogs
- `ViewModels/*` — page state and commands (CommunityToolkit.Mvvm)

Pages are constructed once and activated on navigation. Long operations run in services
and report through `IProgress<T>` or `IInstallLogSink`. State changes are marshalled to the
dispatcher for installation logs.

## Data locations

| Data | Default |
| --- | --- |
| Profiles | `%LOCALAPPDATA%\CTRL+Z\profiles` |
| Settings | `%LOCALAPPDATA%\CTRL+Z\settings.json` |
| Install state | `%LOCALAPPDATA%\CTRL+Z\state\install-state.json` |
| Downloads/cache | `%LOCALAPPDATA%\CTRL+Z\downloads`, `cache` |
| Logs | `%LOCALAPPDATA%\CTRL+Z\logs` |
| Browser backups | `%USERPROFILE%\Documents\CTRL+Z Backups` (configurable) |
| Portable mode | `.\Data` next to the exe when `portable.flag` exists |

## Failure model

- A failed app never stops the queue.
- Each app's exception is captured in its `InstallItemState` and shown in the UI.
- Session state is saved before and after every app transition.
- Browser backup writes to a `.partial` archive and only moves it into place on success.
- Restore validates every archive path against the target User Data root to prevent ZIP-slip
  and refuses credential/cookie paths.

## Extension points

- **New install source**: implement `ISourceInstaller`, register it in `AppServices`.
- **New Chromium fork**: add a `BrowserAppDefinition` entry.
- **Settings backup module**: add a planner/service pair beside the browser planner, keep
  manifests compatible, and register a page in `MainViewModel`. No installer or profile
  schema rewrite is needed; add fields to `AppProfile` only if the new module needs
  per-profile configuration.
- **New catalog source**: implement a service returning a catalog list; the catalog page is
  source-agnostic at the view-model level.
