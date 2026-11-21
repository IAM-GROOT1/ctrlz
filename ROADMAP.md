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
- [x] **1. Safety checks before a wipe** - done, shipping in v1.3.0
- [ ] **2. AppData and settings backup and restore** - **next**
- [ ] **3. Portable app shortcuts and update tracking** - planned
- [ ] **4. Optional code signing** - planned
- [ ] **5. Per-app download resumption and mirror selection** - planned
- [ ] **6. Backup verification and restore drills** - planned
- [ ] **7. Multiple profiles and moving a profile between PCs** - planned
- [ ] **8. Scheduled and recurring backups** - planned
- [ ] **9. Keyboard-first navigation and accessibility** - planned
- [ ] **10. Translations** - planned
- [ ] **11. Offline diagnostics without telemetry** - planned
- [ ] **12. Restore from a browser backup without reinstalling** - planned
- [ ] **13. Packaging and distribution improvements** - planned

Optional extras planned: a hosted sync option and a few conveniences for people who manage several
PCs. The free app stays local, complete and useful on its own.

## Section detail

### 0. Self-update and tag-driven releases

Shipped in v1.2.0. The app checks GitHub releases on a stable or pre-release channel, downloads the
right asset, verifies its SHA-256 against `SHA256SUMS.txt`, applies it with a rollback copy, and
prompts only when the main window is idle. Releases are produced by pushing a version tag.

### 1. Safety checks before a wipe

Pre-wipe checklist, profile drift detection and an install preview with a dry run. Described in the
README under "Before you wipe". Shipping in v1.3.0.

### 2. AppData and settings backup and restore

The next piece of work, and the one the app is missing most: a structured module that backs up and
restores application settings and per-app data folders, so a clean install can bring more than the
app list across.

### 3. Portable app shortcuts and update tracking

Create Start menu shortcuts for portable apps and notice when their source has a newer version.

### 4. Optional code signing

Sign the installer and portable exe so SmartScreen stops warning about an unsigned download. The
release workflow already marks where the step goes.

### 5. Per-app download resumption and mirror selection

Resume an interrupted download instead of starting over, and fall back to a mirror when a source is
slow or unavailable.

### 6. Backup verification and restore drills

Prove a browser backup can actually be restored before it is needed, rather than discovering it is
unreadable on the new machine.

### 7. Multiple profiles and moving a profile between PCs

Keep separate profiles for different machines or roles, and move one between PCs without hand-editing
JSON.

### 8. Scheduled and recurring backups

Back up on a schedule instead of only when the app is opened.

### 9. Keyboard-first navigation and accessibility

Full keyboard operation, sensible focus order and screen-reader labels across every page.

### 10. Translations

Localise the interface, starting with the languages the project already has contributors for.

### 11. Offline diagnostics without telemetry

Let a user produce a diagnostic bundle locally, by choice, with counts and versions only and no
personal file names.

### 12. Restore from a browser backup without reinstalling

Point the app at an existing CTRL+Z backup and restore a browser profile on a machine that never had
that browser installed.

### 13. Packaging and distribution improvements

Smaller artifacts, a proper uninstall path and cleaner upgrade handling.

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
