# Contributing to CTRL+Z

Thanks for helping improve CTRL+Z. The project is local-first and stays that way: no telemetry, no
account, no server component.

## License of your contributions

CTRL+Z is licensed under **AGPL-3.0-only** (see [`LICENSE`](LICENSE) and [`LICENSING.md`](LICENSING.md)).

By submitting a contribution — a pull request, patch, or any other change — you agree that your
contribution is licensed under AGPL-3.0-only, and that you have the right to license it on those
terms. Do not submit code you cannot license this way, including code copied from a project whose
license is incompatible with AGPL-3.0.

Every first-party source file must carry the SPDX header at the top, in the comment syntax for that
file type:

```csharp
// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1
```

XAML uses `<!-- SPDX-License-Identifier: AGPL-3.0-only -->`, PowerShell uses `#`, and Inno Setup
scripts use `;`. New files need the header; generated output under `bin/` and `obj/` does not.

## Workflow

1. Branch from `main` with a focused name, for example `fix/winget-parser`.
2. Keep the change scoped; avoid unrelated reformatting.
3. Add or update tests for behaviour changes.
4. Run the build and the test suite:

   ```powershell
   dotnet build CTRL+Z.sln -c Release
   dotnet test
   ```

5. Open a pull request with a short summary and the test evidence. Do not merge it yourself.

## Keeping the README roadmap in sync

`README.md` has a "What's coming" block that is **generated** from the "Sections" list in
`ROADMAP.md`. Do not edit the lines between `<!-- roadmap:start -->` and `<!-- roadmap:end -->`
by hand. Change the status or title in `ROADMAP.md`, then regenerate:

```powershell
pwsh -File build/sync-readme-roadmap.ps1
```

On Windows PowerShell use `powershell -File build/sync-readme-roadmap.ps1` instead. CI runs the same
script with `-Check` and fails the pull request if the block is stale.

## Adding or fixing a translation

The app's English text lives in `src/CtrlZ.App/Resources/Strings.resx` (screens and messages) and
`src/CtrlZ.Core/Resources/CoreStrings.resx` (install, preview, readiness and safety messages). A
translation is a copy of each named for the language, for example `Strings.fr.resx` and
`CoreStrings.fr.resx`, with the same keys and translated values.

- Keep every `{0}`, `{1:N0}` and similar placeholder; you may move it within the sentence.
- Keep leading and trailing spaces: many values are joined to the next one.
- Line breaks are real line breaks in the file, never `\n`.
- Keys ending in `_One` and `_Other` are plural forms. Languages with more forms add `_Zero`,
  `_Two`, `_Few` or `_Many` (the Unicode CLDR categories); a missing form falls back to `_Other`.
- A key you leave out shows in English, so a partial translation still works.
- Add the language code to `AppLanguage.Supported` in `src/CtrlZ.App/Localization/AppLanguage.cs`
  and to `SatelliteResourceLanguages` in `build/publish.ps1`. A test checks that the three agree,
  and that every value keeps English's keys, placeholders and edge spacing.
- A right-to-left language (such as Arabic) mirrors every window by itself. Paths, ids, commands
  and logs stay left to right: give any new window `FlowDirection="{x:Static l:AppLanguage.FlowDirection}"`,
  and any new text in the code font `FlowDirection="LeftToRight"`. A test checks both.

## What not to submit

- Secrets of any kind: private keys, signing secrets, certificates, tokens, or real license keys.
- Pro or paid module code, production license server code, or customer data. Those belong outside
  this repository (see [`LICENSING.md`](LICENSING.md)).
- Telemetry or analytics.
- Weakening the free core, or moving an existing feature behind a paywall.

## Third-party code

New dependencies must have an AGPL-3.0-compatible license and must be recorded in
[`THIRD_PARTY.md`](THIRD_PARTY.md) with their version and license. Do not vendor third-party source
without documenting its license.

## Contributor License Agreement (placeholder)

<!-- TODO(owner): replace this section with the real CLA before it is needed. The text below is a
     placeholder description only and is deliberately not a license grant. -->

The project owner may ask contributors to sign a Contributor License Agreement (CLA) before their
contribution is accepted. The purpose is to allow the project owner to keep offering commercial
licenses alongside the AGPL-3.0-only public license.

**TODO — CLA text not yet written.** The CLA document itself has not been drafted or reviewed yet.
Until it is added to this repository, this section is a placeholder and grants no rights beyond the
AGPL-3.0-only license described above. Contributors will be told explicitly, in the pull request,
if a signed CLA is required for their change.
