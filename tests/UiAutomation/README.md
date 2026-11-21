# UI automation checks

These scripts drive the real CTRL+Z window with UI Automation. They are **test tooling only**: the
project is not in `CTRL+Z.sln`, no `src` project or build script references `tests/`, and nothing here
reaches the installer or the portable build.

## What each script covers

| Script | Covers |
| --- | --- |
| `lib.ps1` | Shared helpers: window discovery through `EnumWindows` (owned modal windows are invisible to a top-level UIA search), every wait with a timeout, progress every 10 s, and `Invoke-FailureDump` (window pids + titles, redacted log tail, screenshot). `Activate-Element` prefers invoke/toggle/selection and falls back to a real mouse click. |
| `check1-preview.ps1` | Install preview appears before any installer process and before the elevation prompt, blocks the run, cancel runs nothing, Settings toggle disables it |
| `check2-dryrun.ps1` | Dry run over a bogus winget id, a dead URL, a missing local file and a stale GitHub asset; report export and its privacy |
| `check3-drift.ps1` | Drift groups and row actions, scoped to the drift window |
| `check4-readiness.ps1` | Ready-to-wipe checklist on this machine: summary text, responsiveness, Cancel |
| `check5a-installed.ps1` | Installed build updates through the UI (`-Phase Main`, `-Phase Skip`, `-Phase Remind`), parameterised start and end versions |
| `check5b-portable.ps1` | Portable build updates itself in place; exact log lines, no leftovers after the health window, rate-limit gate |
| `check5c-deferral.ps1` | An update check is deferred while a long install queue runs, then prompts |
| `check5d-channels.ps1` | Channel comparison through Settings |
| `check5e-channel-diag.ps1` | Diagnosis of the Settings channel switch: does the setting save, does the next check use it |
| `diag-*.ps1`, `probe-*.ps1` | Throwaway diagnostics kept because they document how each control is exposed |
| `fixtures/FakeInstaller` | Console exe that records START/END timestamps and sleeps, so an install queue can be observed |
| `fixtures/SafetyStress` | Engine-level stress: large folder, junction loop, access-denied, locked file, missing drive, tree hash unchanged |

## How to run

```powershell
dotnet build "CTRL+Z.sln" -c Release

# whole-app checks: pass the built exe
powershell -ExecutionPolicy Bypass -File tests\UiAutomation\check1-preview.ps1 `
  -AppExe src\CtrlZ.App\bin\Release\net8.0-windows\CTRL+Z.exe -DataDir <temp> -Marker <temp> -ShotDir <temp>

# engine-level stress, no desktop needed
dotnet run --project tests\UiAutomation\fixtures\SafetyStress -c Release
```

### Backup rule

Before any run that installs, updates or scans, copy `%LOCALAPPDATA%\CTRL+Z` to a temp folder. Restore
it afterwards and confirm the file count. Several scripts use an isolated `CTRLZ_DATA_DIR` instead, but
the installed-build checks do not.

### Debug-only test variables

- `CTRLZ_UPDATE_REPO` — points the updater at a different GitHub repository. **Debug builds only.**
- `CTRLZ_TEST_GITHUB_TOKEN` — optional Bearer token for `https://api.github.com` so automated runs are
  not limited by the anonymous 60/hour quota. **Debug builds only**, and only for that host; Release
  builds compile the lookup out.

Set them in the environment of the **launched test process**, never in a script, settings file, log or
screenshot. Take the value from `gh auth token` without printing it:

```powershell
$env:CTRLZ_TEST_GITHUB_TOKEN = (gh auth token)
```

Afterwards, grep the app log and the screenshot folder for the token value itself, and for any
string that looks like a GitHub token, then confirm nothing matches.

## Known gaps — covered by code-level tests instead

Driving these through the UI proved unreliable, so they are verified in `CtrlZ.Core.Tests`:

| Gap | Real coverage |
| --- | --- |
| Settings channel toggle (TogglePattern only flips the visual state; the control could not be found reliably) | `UpdateCheckTests.A_channel_switch_applies_to_the_very_next_check` and `The_update_channel_survives_a_settings_round_trip` |
| Deferral of a manual check during an install, backup or restore | `ActivityMonitorTests` and the `ActivityWaiter` tests in `UpdatePromptPolicyTests` |
| Light-theme screenshot of the update prompt | not covered; only the dark prompt was captured |
| "Remind me later" followed by an aged reminder prompting again | `UpdatePromptPolicyTests.Remind_me_later_suppresses_until_the_delay_expires` and `An_expired_reminder_prompts_again` |

`check5d-channels.ps1` and `check5e-channel-diag.ps1` stay for reference even though the UI path is not
trusted; their findings are in PR #6.
