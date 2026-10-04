# Contributing

Thanks for your interest in improving Laptop Input Lock!

## Reporting problems

Open an issue and include:

- Laptop model and Windows version
- Your `devices.txt` (device instance IDs are not personal data, but review it first)
- What you expected and what happened (a screenshot of the balloon/error helps)

Security problems: see [SECURITY.md](SECURITY.md) instead of opening an issue.

## Development

- The whole app is a single file: `src/LaptopInputLock.cs`.
- Build with `src\build.bat` (uses the .NET Framework 4.x C# compiler that ships
  with Windows, so the code must stay within C# 5 features).
- You cannot rebuild while the app is running and locked.
- Test both locking and unlocking with an external mouse connected.

## Pull requests

- Keep changes focused and describe how you tested them (laptop model, Windows version).
- By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
