# CTRL+Z roadmap

Forward-looking work. Nothing here is committed to a release, and none of it is implemented yet.

## Next

- General AppData/config settings backup/restore module (structured for later; see the README).
- Portable app shortcut creation and update tracking.
- Optional code signing — the release workflow already marks where the step goes.
- Per-app download resumption and mirror selection.

## Later improvements

### Mark an update healthy only after the main window has loaded, and keep the backup for 2 launches

The updater currently treats a new build as healthy once it has stayed alive for a fixed
12-second window, and it deletes the previous exe (`CTRL+Z.exe.old`) as soon as that window
passes. That catches a build which crashes on launch, but not one that starts and then fails
shortly afterwards, and the backup is gone by then.

A stronger design would:

- mark the update healthy only once the **main window has loaded**, which is a real signal that
  the new build initialized its UI and services, not just that the process survived; and
- keep the backup for **2 launches**, so a build that fails on its second start can still be
  rolled back automatically instead of leaving the user on a broken version.

Not implemented yet. The current behaviour lives in `src/CtrlZ.Core/Updates/UpdateApplier.cs`
(health window and `.old` cleanup).
