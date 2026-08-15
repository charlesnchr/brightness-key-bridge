# Brightness Key Bridge

A tiny, standalone Windows utility that makes standard keyboard brightness keys control external DDC/CI monitors. It was built for a Keychron Q2 and GF400B monitor, but uses standard Windows and USB HID interfaces rather than device-specific drivers.

Twinkle Tray, administrator privileges, custom drivers, and network access are not required.

## Quick start

1. Ensure DDC/CI is enabled in the monitor's on-screen settings.
2. Run `Install-BrightnessKeyBridge.ps1` in PowerShell.
3. Press the keyboard's normal brightness-down or brightness-up key.

The installer copies the helper to `%LOCALAPPDATA%\BrightnessKeyBridge` and adds a per-user login startup entry. Run `Uninstall-BrightnessKeyBridge.ps1` to reverse those changes.

## How it works

```text
USB brightness key
    ↓
Windows Raw Input (WM_INPUT)
    ↓
HID Consumer usages 0x006F / 0x0070
    ↓
Brightness Key Bridge (±5%)
    ↓
Windows Monitor Configuration API
    ↓
DDC/CI brightness / MCCS VCP 0x10
    ↓
External monitor
```

The helper registers a hidden user-session window for the Consumer Control top-level collection with `RegisterRawInputDevices` and `RIDEV_INPUTSINK`. It parses reports with `GetRawInputData` and `HidP_GetUsages`.

For each adjustment, it re-enumerates active logical and physical monitors. It first tries the high-level `GetMonitorBrightness` and `SetMonitorBrightness` functions. If those are unavailable, it falls back to `GetVCPFeatureAndVCPFeatureReply` and `SetVCPFeature` using MCCS VCP code `0x10`.

Re-enumerating on demand avoids keeping invalid monitor handles after sleep, docking, or cable changes. Repeated key events are coalesced when DDC/CI operations are slower than the keyboard repeat rate.

## Why this exists

Windows only applies its native brightness bindings when a display exposes a Windows brightness endpoint such as `WmiMonitorBrightness`. Many external monitors instead expose brightness exclusively through DDC/CI, so the official keyboard key does nothing even though monitor-control applications can adjust it.

This utility joins those two standard interfaces directly.

It runs as a per-user background application rather than a Windows service because services execute in isolated Session 0 and cannot reliably receive desktop keyboard Raw Input.

## Build

Run:

```powershell
.\Build-BrightnessKeyBridge.ps1
```

The build script uses the .NET Framework C# compiler included with Windows:

`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`

It produces a small, dependency-free Windows executable. No NuGet restore or SDK download is needed.

## Runtime behavior

- Brightness step: 5 percentage points.
- Target: every active physical monitor that responds to DDC/CI brightness queries.
- Ordinary keyboard input is not monitored; the application registers only for the HID Consumer Control collection.
- A named mutex prevents duplicate instances.
- No UI or tray icon is created.
- No network listener is opened.

## Diagnostics

The helper writes a bounded log to:

`%LOCALAPPDATA%\BrightnessKeyBridge\bridge.log`

A successful keypress produces entries similar to:

```text
Brightness down received from raw-input device 0x...
Direct DDC/CI adjustment: Generic PnP Monitor 20%→15% via high-level DDC/CI
```

If HID events appear without a following adjustment, confirm that DDC/CI is enabled and that the cable, adapter, or dock passes DDC traffic.

## Files

- `BrightnessKeyBridge.cs` — complete source.
- `BrightnessKeyBridge.exe` — prebuilt executable.
- `Build-BrightnessKeyBridge.ps1` — reproducible local build.
- `Install-BrightnessKeyBridge.ps1` — per-user installation and login startup.
- `Uninstall-BrightnessKeyBridge.ps1` — removes the helper and startup entry while preserving the diagnostic log.
