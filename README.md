# Laptop Input Lock

[English](README.md) | [日本語](README.ja.md)

Temporarily lock a Windows laptop's **built-in keyboard and touchpad** while
keeping external keyboards and mice working. Run it once to lock, run it again
to unlock.

Useful when you put an external keyboard, MIDI controller or anything else on
top of the laptop, or when you perform live (VJ/DJ) and don't want stray
touches on the laptop to do anything.

## Features

- Locks only the devices you choose: the built-in keyboard and touchpad
  (cursor movement, clicks, taps and scrolling).
- External keyboards and mice keep working.
- One tool to toggle: run it again, or click the tray icon, to unlock.
- Detection window that finds which devices your built-in keyboard and
  touchpad really are, so it works on other laptop models.
- Safe by design: **restarting Windows always restores every device.**
- Lightweight: a single small executable, no installer, no network access.

## Requirements

- Windows 10 or 11, 64-bit
- .NET Framework 4.x (preinstalled on Windows 10/11)
- Administrator rights (a UAC prompt appears each time it runs)

## Getting started

1. Download the latest zip from the [Releases](../../releases) page and extract it
   to a folder (for example `C:\Program Files\LaptopInputLock\`).
2. Run **`Detect Devices.bat`**.
   - Type a few characters on the **built-in** keyboard.
   - Move your finger on the **built-in** touchpad.
   - The devices that sent input are listed. Keep only the built-in ones
     checked and click **Save**. This creates `devices.txt`.
3. Connect an external mouse (or keyboard) and make sure it works.
4. Run **`Toggle Input Lock.bat`** to lock. A shield icon appears in the
   notification area.
5. Run **`Toggle Input Lock.bat`** again, or click the shield icon, to unlock.

> [!WARNING]
> Lock only when you have a working external mouse or keyboard. Otherwise you
> cannot unlock until you restart the PC. The tool warns you if it cannot find
> an external mouse.

## `devices.txt`

One device instance ID per line. Wildcards `*` and `?` are allowed and text
after `#` is a comment. You can find the ID in Device Manager under
**Properties → Details → Device instance path**.

```text
# Keyboard: HID Keyboard Device
HID\VID_0D62&PID_BABC&COL03\*

# Touchpad: HID-compliant touch pad
HID\DELL0B27&COL02\*
```

See [`devices.example.txt`](devices.example.txt) for a full example.

## How it works

Windows refuses to *disable* keyboard and touchpad devices ("not supported"),
so Laptop Input Lock:

1. **Removes** the configured device nodes (`pnputil /remove-device`) when you
   lock.
2. Watches for device changes while locked. If another program triggers a
   hardware rescan and the devices come back, they are removed again
   (within about 2 seconds).
3. **Rescans** hardware (`pnputil /scan-devices`) when you unlock, which brings
   the devices back.

Nothing is changed permanently: no drivers are installed and no settings are
written, which is why a restart restores everything.

### Why the detection window?

Device Manager names can be misleading. For example, many laptops show a
"Standard PS/2 Keyboard" that is not the device that actually sends keystrokes.
The detection window uses the Raw Input API to see which device each key press
or touch really comes from.

## Limitations

- **PS/2 keyboards** (instance ID starting with `ACPI\`) may not come back with
  a rescan on some models. In that case you need to restart to unlock.
  The detection window warns you when you select one.
- After another program rescans hardware, the devices can work for up to
  about 2 seconds before they are removed again.
- Sleep/resume may re-enumerate devices. The tool removes them again, but
  check after resuming.
- The executable is not code-signed, so SmartScreen may show a warning.

## Building from source

No SDK is needed. The build uses the C# compiler included with Windows.

```bat
src\build.bat
```

This creates `LaptopInputLock.exe` in the repository root. You cannot rebuild
while the app is running.

## Command line

| Command | Action |
| --- | --- |
| `LaptopInputLock.exe` | Lock, or unlock if already locked |
| `LaptopInputLock.exe /detect` | Open the device detection window |

## Security

See [SECURITY.md](SECURITY.md) for how to report vulnerabilities and for notes
on running the tool safely.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

[MIT](LICENSE)
