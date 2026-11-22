# CTRL+Z roadmap

What is planned, in the order it is being built. Sections are worked through one at a time, and
each one ships on its own so the app is never half-finished.

The checklist below is the single source of truth: the README's "What's coming" block is generated
from it by `build/sync-readme-roadmap.ps1`, and CI fails if the two drift apart. Nothing here is a
promise of a date, and a section can be reordered if a bug matters more.

| Status | Meaning |
| --- | --- |
| done | Shipped in a release, or complete and in review |
| next | Being worked on now |
| planned | Agreed, not started |

## Sections

- [x] **0. Self-update and tag-driven releases** - done, shipped in v1.2.0
- [x] **1. Safety checks before a wipe** - done, shipped in v1.3.0
- [x] **2. Install improvements** - done, shipping in v1.4.0
- [ ] **3. App updates through winget** - **next**
- [ ] **4. Reliability and security** - planned
- [ ] **5. Foundation for optional extras** - planned
- [ ] **6. UI polish and motion** - planned
- [ ] **7. Onboarding and sharing profiles** - planned
- [ ] **8. Release and distribution** - planned
- [ ] **9. Optional extra: scheduled and local backups** - planned
- [ ] **10. Optional extra: settings backup** - planned
- [ ] **11. Optional extras: availability** - planned
- [ ] **12. Optional extra: encrypted backup off this PC** - later
- [ ] **13. Post-install tweaks** - later

Optional extras planned (sections 9 to 12). The free app stays local, complete and useful on its
own, and everything free today stays free.

## Section detail

### 0. Self-update and tag-driven releases

Shipped in v1.2.0. The app checks GitHub releases on a stable or pre-release channel, downloads the
right asset, verifies its SHA-256 against `SHA256SUMS.txt`, applies it with a rollback copy, and
prompts only when the main window is idle. Releases are produced by pushing a version tag.

### 1. Safety checks before a wipe

Pre-wipe checklist, profile drift detection and an install preview with a dry run. Described in the
README under "Before you wipe". Shipped in v1.3.0.

### 2. Install improvements

- [x] **A. Dependency ordering.** Known dependencies (for example the Visual C++ runtimes) are added
  automatically and install first; "install after" links are honoured; an install-order loop is
  reported; an app whose dependency fails is not attempted. Added dependencies are marked in My
  Apps and can be turned off.
- [x] **B. Post-install commands.** One optional command per app, run without a shell, shown in full
  in the preview. Commands from imported profiles stay blocked until each one is reviewed and
  approved on this PC. See the README for the full safety model.
- [x] **C. Notes.** A private note per app, shown when an app fails or needs manual action, and
  never logged; exports include notes only when you opt in.
- [x] **D. Command line.** `ctrlz install`, `ctrlz dry-run` and `ctrlz list` from a terminal on a
  fresh Windows install, with the same safety rules as the app. See the README.

### 3. App updates through winget

See which installed apps have updates and apply them from one place.

- [x] **A. Updates page.** Lists the apps winget can update, updates the chosen ones or all of them
  one after another, keeps pinned apps out of "Update all", and can ignore one version until a
  newer one appears.
- [ ] **B. Portable apps.** Start menu shortcuts for portable apps and a check for newer versions
  of apps installed from GitHub releases.
- [ ] **C. Command line.** `ctrlz updates` to list and apply app updates from a terminal.

### 4. Reliability and security

- Installer verification beyond SHA-256 where a source publishes more.
- Backup verification and restore drills, so a backup is proven readable before it is needed.
- A local diagnostics bundle, created only on request, with counts and versions and no personal
  file names.
- Import app lists from winget, Scoop and Chocolatey.
- ARM64 builds.
- Download resumption and mirror fallback.
- Redact personal paths and names from logs and saved state. The startup, profile and install-log
  lines currently record a drive-qualified data folder path (which includes the Windows account
  name), and `state/install-state.json` stores paths for every app in the profile. Tracked in
  [#8](https://github.com/IAM-GROOT1/ctrlz/issues/8).

### 5. Foundation for optional extras

A clean boundary so optional extras can be added without changing the free core. The app was
relicensed to AGPL-3.0-only for this (versions up to v1.1.3 remain MIT).

### 6. UI polish and motion

Calm, Windows 11 style polish and motion across every page, full keyboard operation, sensible focus
order and screen-reader labels, and translations later.

### 7. Onboarding and sharing profiles

A first-run guide, multiple profiles, moving a profile between PCs without hand-editing JSON, and
restoring a browser profile from an existing backup on a machine that never had that browser.

### 8. Release and distribution

Code signing so SmartScreen stops warning about an unsigned download, a winget listing for CTRL+Z
itself, smaller artifacts, a proper uninstall path, and a short demo.

### 9 to 12. Optional extras

Scheduled and local backups, settings backup, and later an optional encrypted backup that leaves
this PC. Details will be published when each one is ready.

### 13. Post-install tweaks

Small, reversible Windows tweaks after a reinstall. Later.

## Later improvements

### Mark an update healthy only after the main window has loaded, and keep the backup for 2 launches

The updater treats a new build as healthy once it has stayed alive for a fixed 12-second window, and
it deletes the previous exe (`CTRL+Z.exe.old`) as soon as that window passes. That catches a build
which crashes on launch, but not one that starts and then fails shortly afterwards, and the backup is
gone by then.

A stronger design would:

- mark the update healthy only once the **main window has loaded**, which is a real signal that the
  new build initialized its UI and services, not just that the process survived; and
- keep the backup for **2 launches**, so a build that fails on its second start can still be rolled
  back automatically instead of leaving the user on a broken version.

Not implemented yet. The current behaviour lives in `src/CtrlZ.Core/Updates/UpdateApplier.cs`
(health window and `.old` cleanup).

## Keeping this in sync

`build/sync-readme-roadmap.ps1` regenerates the block between `<!-- roadmap:start -->` and
`<!-- roadmap:end -->` in `README.md` from the "Sections" list above. Run it after changing a status
line here:

```powershell
pwsh -File build/sync-readme-roadmap.ps1
```

CI runs the same script with `-Check` on every pull request, so a stale README fails the build.
