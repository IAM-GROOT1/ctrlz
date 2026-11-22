# CLAUDE.md

Guidance for AI coding assistants working in this repository. Read `CONTRIBUTING.md` too.

## Build and test

```powershell
dotnet build CTRL+Z.sln -c Release   # must finish with 0 warnings
dotnet test
```

After editing `ROADMAP.md`, regenerate the README roadmap block:
`pwsh -File build/sync-readme-roadmap.ps1` (CI checks it with `-Check`).

## Branches, PRs, merges

- Never commit or push to `main`. Run `git branch --show-current` before every commit or push.
- All changes go through a pull request. Required checks: `test-and-publish` and
  `README roadmap block is in sync`. Merge only when CI is green, using a **merge commit**.
- Never force-push, rewrite history, or delete tags or releases. Pushing a tag starts the release
  workflow, so create tags and releases only with the maintainer's explicit approval.
- Commit small. Keep progress in the PR description checklist; it is the source of truth.

## Code rules

- Every new first-party file starts with the SPDX header (`AGPL-3.0-only`); see `CONTRIBUTING.md`
  for the syntax per file type.
- No telemetry or analytics. The only network calls are update checks and downloads.
- Tests never ship: nothing under `tests/` (including `tests/UiAutomation` and
  `tests/CtrlZ.App.Tests`) may be referenced from shipped projects or outputs.
- Post-install commands: never weaken the trust gate. Imported or unknown profiles never run a
  command without a per-command reviewed approval. Load profiles only through
  `ProfileOriginRecorder`.
- Keep the UI consistent with the existing styles; no new styling system.
- Never log or commit secrets, tokens, or personal paths.

## Where things live

- `ROADMAP.md`: roadmap sections and status.
- Open PR descriptions: per-milestone checklists.

## Public-safety rule

`ROADMAP.md`, `README.md` and this file are public. Do not add pricing, payment providers,
license-server or key internals, signing details, or cloud infrastructure details to them.
