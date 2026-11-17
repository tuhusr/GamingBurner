# GamingBurner

A frame rate, latency and hardware monitor for PC games, with an on-screen display and benchmark recording.

## Install

1. Open the [latest release](https://github.com/tuhusr/GamingBurner/releases/latest).
2. Download **GamingBurner-win-Setup.exe** and run it. It installs for your Windows account only and adds a desktop and Start menu shortcut.
3. Windows may show "Windows protected your PC", because the app is not code-signed. Choose **More info**, then **Run anyway**.

When a new version is released, GamingBurner shows an **Update and restart** prompt at the top of its window. You can also check under **Properties → General → Version**.

To uninstall, use **Windows Settings → Apps → Installed apps → GamingBurner**.

## First run

- **Administrator rights.** On most PCs Windows only lets administrators measure another program's frames. If the window says so, press **Run as administrator**. To make that automatic, tick **Properties → General → Always start as administrator** (Windows asks for permission each launch).
- **On-screen display.** Draws your chosen stats over the game in borderless or windowed mode. Placing it over the game adds a few milliseconds of latency; putting it on a second monitor (**Properties → On-screen display → Display**) costs nothing. It cannot appear over exclusive fullscreen.
- **Hotkeys** (change them in Properties → Hotkeys):
  - `Ctrl+Shift+F9` show or hide the on-screen display
  - `Ctrl+Shift+F10` start or stop a benchmark recording
  - `Ctrl+Shift+F11` reset highest, lowest and average

Recordings are saved to `Documents\GamingBurner\Logs`. Settings are kept in `%APPDATA%\GamingBurner`.

## What it measures

- Frame rate, frame time, 1% and 0.1% lows, stutters
- Latency: frame to screen, click to screen, PC latency when the game reports it, dropped frames, and whether the processor or graphics card is the limit
- Graphics card: load, temperatures, power, video memory, clocks, fan
- Processor: load per thread, clock speed, cores used by the game (temperature needs administrator mode and the PawnIO driver)
- Memory, paging, disk activity, and connection ping, jitter and loss

## How it works, and what it sends

GamingBurner reads frame timing from Windows event tracing and from Intel's PresentMon, bundled unmodified. It does not inject anything into games.

It contacts two places on the internet: GitHub, to check for updates, and `1.1.1.1` once a second for the network test (turn this off or change the address in Properties → General). Nothing else leaves your PC.

The **Tuning** page can change fan speed and graphics card clock offsets. Raise clocks in small steps; too high causes crashes. Offsets reset when the PC restarts.

## Building and releasing (maintainer)

Requires the .NET 9 SDK and the GitHub CLI signed in.

```powershell
.\release.ps1 -Version 1.0.1 -Notes "What changed"
```

This builds a self-contained copy, packages it with [Velopack](https://velopack.io), and publishes a GitHub release that installed copies offer as an update. Version numbers must increase every release. Use `-LocalOnly` to build the installer without uploading.

Third-party components and their licences are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).