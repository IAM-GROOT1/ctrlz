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
