# CTRL+Z license server — reference only

**This is an example, not a production service.** The real license server and the Pro module
must live in a private repository.

It demonstrates:

- placeholder license keys and machine limits;
- ECDSA P-256/SHA-256 token signing;
- `/activate`, `/revalidate`, and `/deactivate` endpoints;
- a signed payload containing license id, plan, features, machine hash, issued/expiry times.

## Run

```powershell
dotnet run --project tools/license-server-reference
```

The server listens on `http://127.0.0.1:5080`.

On first run it creates `keys/demo-private.pem` and prints the matching public key. The folder
is git-ignored. To use the demo server with the app:

1. Copy the printed public key into
   [`LicensePublicKey.Base64`](../../src/CtrlZ.Core/Licensing/LicensePublicKey.cs).
2. Set **Settings → Pro License → License server URL** to `http://127.0.0.1:5080`.
3. Use one of the demo keys:
   - `CTRLZ-DEMO-0001` (2 machines)
   - `CTRLZ-DEMO-0002` (1 machine)

Replace the placeholder keys, store, URL, and keypair before any real deployment. Never commit
the generated private key.

## Production boundary

The app contains only the public verification key. The real server must:

- authenticate customers and license keys;
- enforce plans, machine limits, expiry, and revocation;
- sign tokens with a private key held in a secret manager;
- expose HTTPS and rate limiting;
- never put Pro feature code or signing keys in this public repository.
