# CTRL+Z licensing

## Current license: AGPL-3.0-only

CTRL+Z is free software licensed under the **GNU Affero General Public License, version 3.0 only**
(`AGPL-3.0-only`). The full text is in [`LICENSE`](LICENSE). That file is the official, unmodified
license text published by the Free Software Foundation and is the authoritative version; this
document only explains how the project applies it.

Every first-party source file carries the SPDX identifier at the top, in the comment syntax native
to that file type:

```csharp
// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (c) 2026 iam-groot1
```

Rendered in XAML as `<!-- SPDX-License-Identifier: AGPL-3.0-only -->` and in PowerShell and Inno
Setup scripts with `#` and `;` respectively.

## Earlier releases remain MIT

Versions up to and including **v1.1.3** were published under the MIT License. Those releases, their
git tags, and their source archives are unchanged and remain available under the MIT terms. You may
continue to use, fork, and redistribute any release up to and including v1.1.3 under MIT.

Nothing released under MIT is revoked or relicensed by this change. The AGPL-3.0-only license
applies to this version and to every version after it. The public repository history is preserved
as-is so that the MIT-licensed history stays auditable.

Practical consequence: if you received a copy of CTRL+Z up to and including v1.1.3, the MIT grant
you received still applies to that copy. If you take updates beyond v1.1.3, the AGPL-3.0-only terms
apply to the updated code.

## What this means for the core

The whole application in this repository — scanning, profiles, the install engine, install sources,
the catalog, browser backup and restore, the UI, and the tests — is AGPL-3.0-only.

AGPL-3.0 is a strong copyleft license with a network clause. If you run a modified version as a
network service, you must offer the corresponding source to that service's users. CTRL+Z is a
local-first Windows desktop application: it has no telemetry and no server component, so in normal
use the ordinary GPL obligations apply — if you distribute a modified build, you must make the
corresponding source available under the same license.

## Pro / commercial module

The AGPL-licensed core in this repository contains no Pro module and no Pro feature code.

Any future Pro edition will live **outside this repository**, in a separate private codebase, and
will be distributed under its own separate commercial license. This public repository must never
contain:

- Pro feature implementation code;
- a production license server;
- private signing keys, signing secrets, or real license keys;
- customer data.

Keeping Pro code out of this repository is what allows the public core to stay fully free and
AGPL-3.0-only while a commercial edition is offered separately.

## Contributions

Contributions to this repository are accepted under AGPL-3.0-only. See
[`CONTRIBUTING.md`](CONTRIBUTING.md), which also describes the Contributor License Agreement
placeholder that contributors may be asked to sign so the project owner can continue to offer
commercial licenses.

## Third-party components

See [`THIRD_PARTY.md`](THIRD_PARTY.md) for every dependency and its license, and for the AGPL-3.0
compatibility assessment of each one.
