# Testing browser restore on a fresh VM / different Windows user

The v1 restore path was exercised against synthetic profiles in automated tests. Before
relying on it for real data, do a manual restore on a spare VM or a second local Windows
account. The browser's encrypted password/cookie stores are intentionally outside the
backup, so this validates the portable data only.

## 1. Prepare the source backup

1. On the source PC, close the browser.
2. Open **Browsers → Back up**.
3. Select one browser and one small profile first.
4. Choose a destination you can copy to the VM.
5. Optionally set a password and record it in a password manager.
6. Confirm the archive size and that the manifest contains the expected profile names.

## 2. Prepare a clean VM

- Windows 10 1809+ or Windows 11 x64.
- Install the same Chromium browser/fork (for Helium, install Helium; for Chrome, install
  Chrome).
- Launch the browser once, then close it fully. This creates a valid `User Data`/`Local State`.
- Do **not** sign into any browser account; sync must stay off so the test cannot be masked
  by cloud data.

## 3. Restore

1. Copy the archive into the VM.
2. Open CTRL+Z → **Browsers → Restore**.
3. Browse to the archive; enter the password if encrypted; click **Inspect**.
4. Select the same profiles you backed up.
5. Select the target browser.
6. Leave **Restore as new profiles** checked for the first test.
7. Restore. Confirm the app reports a target profile folder and, if applicable, a safety
   backup folder.

## 4. Verify

- [ ] The target browser starts without a corruption or “profile cannot be opened” error.
- [ ] New profile(s) appear in the browser profile menu with sensible names.
- [ ] Bookmarks are present.
- [ ] History is present.
- [ ] Recently closed tabs / last session behavior is as expected. The browser may only
      reopen tabs if “Continue where you left off” was enabled in the restored preferences.
- [ ] Extensions are listed. Re-enable any that Chromium disabled; check extension settings.
- [ ] Search engines are present in browser settings.
- [ ] Site permissions (camera, notifications, location) are retained for a site you backed up.
- [ ] No saved passwords were restored (sign-in should be required).
- [ ] No cookies were restored (sites show signed-out state).
- [ ] Existing cookies/passwords on the target, if any, are still intact.
- [ ] Archive file names and manifest do not contain `Cookies`, `Login Data`, or `Network`
      cookie stores.

## 5. Test merge mode (optional)

1. Repeat the restore with **Restore as new profiles** unchecked.
2. Confirm the overwrite warning.
3. Verify the matching existing profile receives bookmarks/history/preferences.
4. Verify existing cookies/passwords are still present (CTRL+Z never deletes them).

## 6. Test conflict handling

1. Restore the same profiles again with new-profile mode off.
2. CTRL+Z should detect conflicts and offer to restore as new instead.
3. Verify the resulting new profile has a new folder name (`Profile N`) and the original is
   unchanged.

## Known limitations to expect

- Saved passwords/cookies: absent by design.
- Some extensions: may need re-enabling or signing in.
- Tab restore: version/fork and “continue where you left off” dependent.
- Opera: flat single-profile layout cannot create an additional profile.
- Cross-fork restores: best-effort; same fork is the supported path.
- Encrypted zip: Windows Explorer cannot open it; use CTRL+Z.
- Locked files: close the browser; VSS is a best-effort fallback that needs administrator
  rights.

Record the browser version and fork in any bug report. Include the install/browser log from
**Settings → Open logs** with personal paths redacted if sharing publicly.
