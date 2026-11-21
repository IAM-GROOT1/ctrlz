# Website rebuild hook

The marketing site (a separate repository, deployed by Cloudflare Pages) reads data from this
repository: the latest release, the changelog and the roadmap. Cloudflare Pages rebuilds when its
own repository changes, so it does not know when **this** repository publishes something. This hook
closes that gap.

## What it does

Two places call a Cloudflare Pages **deploy hook**:

| Trigger | Where | Why |
| --- | --- | --- |
| A release is published | the last step of `.github/workflows/release.yml`, after the release exists and its assets are verified | so the site's download buttons and version number move to the new release |
| A pull request merges to `main` and touches `README.md`, `ROADMAP.md`, `CHANGELOG*` or `docs/**` | `.github/workflows/rebuild-site.yml` | so the roadmap and changelog shown on the site match the repository |

The hook is a plain `curl -X POST` of a secret URL. It sends no data, and the URL is the only
credential.

## Why not trigger on the `release` event

A workflow that listens for `release: published` does not fire for releases created by the release
workflow itself, because GitHub does not start new workflow runs from events caused by the built-in
`GITHUB_TOKEN`. Calling the hook from inside `release.yml` avoids that trap entirely.

## Set it up

### 1. Create the deploy hook in Cloudflare

1. Open the Cloudflare dashboard and go to **Workers & Pages**.
2. Select the site's Pages project.
3. Open **Settings** > **Builds** > **Deploy hooks**.
4. Create a hook named `app-repo-release`, for the **production** branch, and copy the URL.

### 2. Store it as a repository secret

Run this in your terminal and paste the URL when prompted. Do not paste it into chat, an issue, or
a file:

```powershell
gh secret set CLOUDFLARE_PAGES_DEPLOY_HOOK --repo IAM-GROOT1/ctrlz
```

### 3. Nothing else

The next release, or the next merge that touches the watched files, calls the hook.

## Behaviour when it is not configured

- **No secret** (forks, or before step 2): both callers print a notice and skip. Nothing fails.
- **Hook returns an error**: the release is unaffected. `release.yml` marks the step
  `continue-on-error`, retries three times, and emits a warning annotation so the failure is visible
  in the run summary.
- **Repeated pushes**: `rebuild-site.yml` uses a concurrency group with `cancel-in-progress`, so a
  burst of merges results in a single rebuild of the newest state instead of a queue of stale ones.

## Security notes

- The secret is passed as an environment variable and never echoed. `curl` runs with `--silent` and
  without `--verbose`, so a failure cannot print the URL.
- Both callers use `permissions: contents: read`.
- The URL is equivalent to a deployment trigger: anyone holding it can start builds. Rotate it in
  Cloudflare if it is ever exposed, then repeat step 2.
