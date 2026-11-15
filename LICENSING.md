# CTRL+Z licensing

CTRL+Z uses an **open-core** model.

## Free core

The code in this repository is licensed under the **MIT License**. It includes:

- scanning, profiles, install engine, custom sources, catalog, and browser backup/restore;
- the plugin boundary in `CtrlZ.Core.Pro`;
- the client-side license verifier and offline-grace logic;
- the Pro page shell and license settings;
- the reference license server under `tools/license-server-reference/` (example only).

Everything that is free today remains free and fully functional without a Pro module or a
license. No existing feature is moved behind the Pro boundary.

## Pro

Commercial Pro features live in a **separate private repository** and are distributed under a
commercial license. This public repository must never contain:

- Pro feature implementation code;
- a production license server;
- private signing keys, signing secrets, or real license keys;
- customer data.

The public plugin loader only discovers the optional `CtrlZ.Pro.dll`. The Pro assembly is
trusted local code; it is not a security sandbox. The app has no telemetry.

## License tokens

The app verifies ECDSA P-256/SHA-256 tokens locally using only an embedded **public** key. A
reference server demonstrates activation, machine limits, revalidation, deactivation, and
signing with an ignored demo key. Production deployments must use their own keypair, HTTPS,
rate limiting, secure storage, and entitlement/revocation logic.

The placeholder public key in
[`LicensePublicKey.cs`](src/CtrlZ.Core/Licensing/LicensePublicKey.cs) is intentionally not
usable for production licenses until replaced.

## Contributions

Contributions are accepted to the **core** only. See [`CONTRIBUTING.md`](CONTRIBUTING.md).
By submitting a contribution you agree to the project's contributor terms; a formal CLA will be
published before commercial contributions are accepted.
