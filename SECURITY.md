# Security Policy

## Supported versions

Only the latest release receives security fixes.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report them privately through GitHub:
**Security** tab → **Report a vulnerability**
(<https://github.com/kemushiboy/keyboard-toggle/security/advisories/new>).

Include the Windows version, the release you used, and steps to reproduce.
You can expect an initial response within 7 days.

## Security model

Laptop Input Lock runs **as administrator** because removing and rescanning
devices requires it. Keep this in mind:

- **Download releases only from this repository's Releases page.** Release
  binaries are built by GitHub Actions from the tagged source. Each release
  includes a `SHA256SUMS.txt` you can check with
  `Get-FileHash LaptopInputLock.exe -Algorithm SHA256`, and a signed build
  provenance attestation you can verify with
  `gh attestation verify LaptopInputLock.exe --repo kemushiboy/keyboard-toggle`.
- **The executable is not code-signed**, so Windows SmartScreen may warn you.
  If you prefer, build it yourself from source with `src\build.bat`
  (it uses only the C# compiler that ships with Windows).
- **Keep the folder somewhere only administrators can write to** (for example
  under `C:\Program Files\`) if other users share the PC. Anyone who can replace
  `LaptopInputLock.exe` or edit `devices.txt` can change what runs elevated or
  which devices get removed.
- The tool does not use the network, does not collect data, and only calls the
  built-in `pnputil.exe` from `System32` plus Windows device APIs.
- Removed devices always come back after a restart.
