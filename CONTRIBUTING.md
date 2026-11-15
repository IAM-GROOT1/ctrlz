# Contributing to CTRL+Z

Thanks for helping improve the free core.

## Scope

This public repository accepts contributions to the **free core** only:

- app scanning, profiles, install engine, sources, catalog, browser backup/restore;
- plugin and license *client* plumbing;
- UI, tests, docs, and the reference license server example.

Do **not** submit:

- Pro feature implementations;
- production license server code;
- private keys, signing secrets, certificates, or real license keys;
- customer data or telemetry.

## Workflow

1. Fork or branch from `main`.
2. Use a focused branch name such as `fix/winget-parser`.
3. Add tests for behavior changes.
4. Run:

```powershell
dotnet test
dotnet build CTRL+Z.sln -c Release
```

5. Open a pull request with a short summary and test evidence. Do not merge it yourself.

Do not weaken the free core, move an existing feature behind a license, or add startup
nagging/popups.

## Contributor terms (CLA placeholder)

By submitting a pull request you confirm that you have the right to license your contribution
under the project's MIT license. A formal Contributor License Agreement (CLA) will be published
before any commercial Pro contribution is accepted. This section is a placeholder, not a final
CLA.

## Security and privacy

Keep the app local-first. Do not add telemetry or analytics. License activation/revalidation
must remain the only added network calls, and failures must keep the app usable.
