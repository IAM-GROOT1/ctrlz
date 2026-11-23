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
- [x] **3. App updates through winget** - done, shipping in v1.4.0
- [x] **4. Reliability and security** - done, shipping in v1.4.0 (ARM64 builds deferred)
- [ ] **5. Foundation for optional extras** - planned
- [x] **6. UI polish and motion** - done, shipping in v1.4.0 (translations later)
- [x] **7. Onboarding and sharing profiles** - done, shipping in v1.4.0
- [ ] **8. Release and distribution** - **next**
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
- [x] **B. Portable apps.** Start menu shortcuts for portable apps and a check for newer versions
  of apps installed from GitHub releases.
- [x] **C. Command line.** `ctrlz updates` lists app updates; `--all --yes` or `--id <id> --yes`
  applies them.

### 4. Reliability and security

- Done: installer verification beyond SHA-256. A GitHub release's own checksum file is used when
  GitHub has no digest, and every `.exe`/`.msi` has its Authenticode signature checked offline
  before it runs. A broken signature stops the install.
- Done: **Verify** on the Browsers page reads every file of a browser backup (decrypting and
  checking each one) and reports whether it can be restored, without changing anything.
- Done: **Save diagnostics** in Settings writes a ZIP, only when you ask, with versions, settings
  switches, counts and recent logs with paths and names removed. Nothing is sent anywhere.
- Done: **Import list** on My Apps adds apps from a `winget export`, `scoop export` or Chocolatey
  `packages.config` file, matching Scoop and Chocolatey names through the catalog on this PC and
  listing what could not be matched.
- Later: ARM64 builds (deferred; not a priority for this release).
- Done: downloads resume after a dropped connection (an HTTP range request guarded by the
  server's ETag or Last-Modified, so a changed file starts over), retry server errors and stalled
  transfers, and fall back to a mirror where one exists. The winget bootstrap tries aka.ms, then
  Microsoft's own winget-cli release.
- Done: personal paths and names are kept out of logs and saved state. Logs write personal
  folders as `%USERPROFILE%`-style tokens and hide the account and PC names; the install state file
  stores the same tokens and expands them on load
  ([#8](https://github.com/IAM-GROOT1/ctrlz/issues/8)).

### 5. Foundation for optional extras

A clean boundary so optional extras can be added without changing the free core. The app was
relicensed to AGPL-3.0-only for this (versions up to v1.1.3 remain MIT).

### 6. UI polish and motion

Calm, Windows 11 style polish and motion across every page, full keyboard operation, sensible focus
order and screen-reader labels, and translations later.

- [x] **A. Screen readers.** Every button, check box, field and list a user can reach has a name
  a screen reader can read; row buttons name their app ("Move Steam up"). A test reads every
  window and page and fails the build if a new control has no name.
- [x] **B. Keyboard.** Ctrl+1 to Ctrl+8 open each page and Ctrl+, opens Settings; Enter accepts and
  Esc closes every window (checked by a test); dialogs and the app editor open with focus in
  their first field.
- [x] **C. Motion.** The page fade is skipped when Windows' *Animation effects* setting is off.
- [ ] **D. Translations.** Later.

### 7. Onboarding and sharing profiles

A first-run guide, multiple profiles, moving a profile between PCs without hand-editing JSON, and
restoring a browser profile from an existing backup on a machine that never had that browser.

- [x] **A. First-run guide.** A short welcome on first launch: what to do before reinstalling and
  on the fresh install, with buttons to start on Scan or My Apps. Existing users are not
  interrupted, and Settings can open it again.
- [x] **B. Multiple profiles.** Already in place: create, rename, switch, delete, import and
  export profiles in My Apps.
- [x] **C. Moving a profile to another PC.** **Export** in My Apps first says what the file
  carries and what needs attention there (apps installed from a local file, post-install commands
  that stay blocked until reviewed on the new PC), and leaves notes out unless you tick *Include
  my notes*.
- [x] **D. Restoring for a browser that is not installed yet.** When a backup's browser is not on
  this PC, the restore panel says so and offers to install it with winget (Chrome, Edge, Brave,
  Vivaldi, Firefox, LibreWolf, Waterfox, Zen). The browser creates its own data folder on its
  first start; after that, Refresh and restore as usual. CTRL+Z does not build a browser's folder
  by hand.

### 8. Release and distribution

Code signing so SmartScreen stops warning about an unsigned download, a winget listing for CTRL+Z
itself, smaller artifacts, a proper uninstall path, and a short demo.

- [x] **A. Smaller artifacts.** The portable app drops the unused translated framework resources
  (70.1 MB to 64.6 MB); the UI is English-only until translations land.
- [x] **B. Uninstall.** The uninstaller asks whether to also delete CTRL+Z's data (default: keep
  it), and the README explains how to remove a portable copy. CI now builds the installer on
  every pull request.
- [ ] **C. Code signing.** Later.
- [ ] **D. A winget listing for CTRL+Z.** After the next stable release.
- [ ] **E. A short demo.** Later.

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

- [x] **Done for portable copies.** The update helper now waits up to 90 seconds for the new build
  to show its main window; if it exits first, the previous build is restored at once. The previous
  `CTRL+Z.exe.old` (and `ctrlz.exe.old`) is kept until the new build has shown its window on 2
  launches. If a later start of the new build never shows its window, the next start puts the
  previous build back and opens it, with a message saying why. The logic lives in
  `src/CtrlZ.Core/Updates/UpdateProbation.cs`. It applies to updates made *from* this version
  onward, because the update helper is a copy of the version being replaced. Installed copies
  are unchanged (the installer replaces them).

## Keeping this in sync

`build/sync-readme-roadmap.ps1` regenerates the block between `<!-- roadmap:start -->` and
`<!-- roadmap:end -->` in `README.md` from the "Sections" list above. Run it after changing a status
line here:

```powershell
pwsh -File build/sync-readme-roadmap.ps1
```

CI runs the same script with `-Check` on every pull request, so a stale README fails the build.
