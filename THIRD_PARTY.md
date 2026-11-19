# Third-party components and licenses

CTRL+Z is licensed under AGPL-3.0-only (see [`LICENSE`](LICENSE) and [`LICENSING.md`](LICENSING.md)).
This file inventories every third-party component the project uses and records whether it is
compatible with AGPL-3.0.

All licenses below were read from each package's published `.nuspec` metadata on nuget.org, not
inferred from package names.

## Compatibility summary

MIT, ISC, BSD, and Apache-2.0 are permissive licenses. They impose no field-of-use restriction, no
additional copyleft obligation, and can lawfully be combined with AGPL-3.0 code and redistributed
as part of an AGPL-3.0 work, provided their notice and attribution requirements are met.

**No component in this project is GPLv2-only, and none carries a restriction that AGPL-3.0 cannot
accommodate.** Every entry below is compatible.

## NuGet packages referenced directly

| Package | Version | License | AGPL-3.0 compatible | Used by |
| --- | --- | --- | --- | --- |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | Yes | `CtrlZ.App` (MVVM source generators) |
| Microsoft.Data.Sqlite | 8.0.11 | MIT | Yes | `CtrlZ.Core` (Chromium profile reads) |
| SharpZipLib | 1.4.2 | MIT | Yes | `CtrlZ.Core` (compressed browser archives) |
| System.Management | 8.0.0 | MIT | Yes | `CtrlZ.Core` (registry and WMI scans) |
| Microsoft.NET.Test.Sdk | 17.8.0 | MIT | Yes | `CtrlZ.Core.Tests` (test host) |
| coverlet.collector | 6.0.0 | MIT | Yes | `CtrlZ.Core.Tests` (coverage) |
| xunit | 2.5.3 | Apache-2.0 | Yes | `CtrlZ.Core.Tests` (test framework) |
| xunit.runner.visualstudio | 2.5.3 | Apache-2.0 | Yes | `CtrlZ.Core.Tests` (test adapter) |

## Notable transitive dependencies

These are not referenced directly but are restored into the build graph and, in the case of the
SQLite components, ship inside the published executable.

| Component | Version | License | AGPL-3.0 compatible | Pulled in by |
| --- | --- | --- | --- | --- |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.6 | Apache-2.0 | Yes | Microsoft.Data.Sqlite |
| SQLitePCLRaw.core / provider / lib | 2.1.6 | Apache-2.0 | Yes | SQLitePCLRaw.bundle_e_sqlite3 |
| SQLite (e_sqlite3 native library) | 3.x | Public domain | Yes | SQLitePCLRaw.lib.e_sqlite3 |
| Microsoft.TestPlatform.* | 17.8.0 | MIT | Yes | Microsoft.NET.Test.Sdk |
| System.CodeDom | 8.0.0 | MIT | Yes | System.Management |
| Microsoft.Win32.SystemEvents | 8.0.0 | MIT | Yes | System.Management |
| Newtonsoft.Json | 13.0.x | MIT | Yes | Test platform only |

Test-only dependencies (`xunit`, `coverlet`, `Microsoft.NET.Test.Sdk` and their transitive
packages) are never shipped to users and do not affect the license of distributed builds.

## Fonts

No font files are bundled or redistributed. The application references Windows system fonts only,
declared in `src/CtrlZ.App/Themes/Typography.xaml`:

- Segoe UI Variable Display / Segoe UI — Windows system font
- Cascadia Mono / Consolas — Windows system font
- Segoe Fluent Icons — Windows system icon font

System fonts are supplied by the operating system and are not redistributed with CTRL+Z, so no font
license needs to be carried by this project.

## Icons and images

No third-party icon set is bundled. Interface icons come from the Segoe Fluent Icons system font
(see above), and `src/CtrlZ.App/Themes/Icons.xaml` contains first-party path geometry.

All images and icons under `src/CtrlZ.App/Assets/` and `branding/` are first-party project assets
(see `branding/README.md`).

## Bundled data

| Asset | Origin | License | AGPL-3.0 compatible |
| --- | --- | --- | --- |
| `src/CtrlZ.Core/Resources/winutil-applications.json` | Application catalog data derived from Chris Titus Tech's WinUtil | MIT | Yes |

WinUtil is an MIT-licensed community project and is credited in the README. The bundled file is
catalog data (application names, categories, and package identifiers), not executable code.

## Build tooling (not distributed)

| Tool | Purpose | License | Notes |
| --- | --- | --- | --- |
| Inno Setup 6 | Builds the `CTRL+Z-Setup-*.exe` installer | Inno Setup License (free, permits commercial use) | Never shipped to users; only used at build time |

## Keeping this file current

When a NuGet package is added, removed, or upgraded, update the tables above with its version and
the `license` field from its `.nuspec`. If a new dependency is GPLv2-only, or its license is
unclear, do not merge it before the project owner has reviewed the compatibility question.
