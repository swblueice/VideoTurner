# VideoTurner

**Wherever the window is, the sound comes out of that monitor's audio device.**

A small per-monitor audio routing utility for Windows. It sits in the tray, keeps a
*monitor → audio device* mapping, and automatically moves each application's sound to the
speakers/headphones belonging to the display its window is on. Any running app can also be
pinned to a specific speaker and saved to an **app list** — the list is applied first on the
next launch, and everything else falls back to monitor-based routing.

Chinese UI. Per-user install (no UAC prompt), no admin rights, no driver required.
Derived from the MIT-licensed [ScreenSound](https://github.com/twibster/ScreenSound).

[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D6.svg)](#requirements)
[![.NET](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Release](https://img.shields.io/github/v/release/swblueice/VideoTurner?include_prereleases&sort=semver)](https://github.com/swblueice/VideoTurner/releases)

*[中文说明请见 README.md](README.md)*

---

## Why

Windows has no native "route audio per monitor". Once a second screen or TV is plugged in,
either every app plays through the default device, or you pick output devices one by one in
the volume mixer — and those choices are gone as soon as the app restarts.

VideoTurner does three things:

1. Remembers *which monitor maps to which audio device* and sends each app's sound to the
   device belonging to the display its window is on.
2. Pins a single app to a specific speaker (for example "games always go to headphones") and
   persists that choice.
3. Handles apps that **read the output device only once at startup** (games, emulator clients)
   with a driver-free, per-process virtual channel, so the output follows the window without
   restarting the app.

## Download

Grab a build from [Releases](https://github.com/swblueice/VideoTurner/releases/latest):

| File | Description |
|---|---|
| `VideoTurner-<version>-win-x64-setup.exe` | Installer. Per-user install into `%LOCALAPPDATA%\Programs\VideoTurner`, **no UAC prompt**, includes an uninstaller |
| `VideoTurner-<version>-win-x64-portable.zip` | Portable. Unzip and run; no install, no .NET prerequisite (runtime is bundled) |

Both are **self-contained** — the target machine does not need the .NET 8 Desktop Runtime.
A `.sha256.txt` is published next to each artifact.

> The binaries are not code-signed, so Windows SmartScreen shows "Unknown publisher" the first
> time. Choose *More info → Run anyway*. Verifying the SHA-256 hash is the safer check.

## Quick start

1. Launch VideoTurner. The home page lists your monitors and audio devices.
2. Pick an audio device for each monitor. HDMI/DisplayPort screens are usually matched
   **automatically** (see below); a manual choice is marked "manual" and is never overridden.
3. Move a playing app's window to the other screen — its sound follows (routing is per
   process, not per window; see [Known limitations](#known-limitations)).

**Dry run first:** `VideoTurner.exe --dry-run` opens the UI without touching any audio or
writing settings — use it to confirm the mapping looks right.

Settings live in `%APPDATA%\VideoTurner\settings.json`, decisions are logged to
`%APPDATA%\VideoTurner\routing.log` (rotated at 1 MB).

## How automatic matching works

Two layers, strongest first.

**1. Device container ID (most reliable).** Windows keeps a hidden pairing between audio
endpoints and displays: the display's `ContainerID` (from
`HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\…`, addressed via the CCD interface path) is
*identical* to the `PKEY_Device_ContainerId` of the HDMI/DisplayPort endpoint currently
attached to it. Empty containers
(`00000000-0000-0000-ffff-ffffffffffff`) are discarded, otherwise onboard/virtual cards get
mis-assigned to the primary display.

**2. Monitor model name inside the endpoint name.** Some GPUs (AMD/Intel) embed the monitor
model in the endpoint name, e.g. `AMD High Definition Audio Device — 3 - <MODEL>`. Model
tokens containing digits and at least 3 characters are looked up in the normalized endpoint name.

If neither layer matches, the monitor stays **unmapped** (follows the system default device)
and the UI asks you to choose. The tool never guesses a device.

The model name comes from CCD (`QueryDisplayConfig` →
`DISPLAYCONFIG_TARGET_DEVICE_NAME`) rather than `EnumDisplayDevices`, which returns
"Generic PnP Monitor" on many machines; EDID parsing (tag `0xFC`) is the fallback.

Read-only probe (no UI, changes nothing):

```powershell
dotnet run --project tools/MatcherProbe
```

## Per-app assignment and the app list

Three decision layers, highest priority first:

```
1. App list: the app is assigned a speaker  -> always that speaker, whichever screen it is on
2. Auto: not in the list                    -> the mapping of the monitor its window is on
3. System default: no mapping at all        -> leave it alone
```

The same order applies at startup: **list first, then auto**.

Assigning is available in two places — inline on the home page (with a *save to list*
checkbox) and on the app list page, where apps that are not currently playing can be assigned
in advance. Details, including behaviour when a mapped device disappears (graceful fallback,
entry preserved), are in the Chinese [README](README.md#按应用分配扬声器--应用清单).

## Virtual channel mode

For apps that read the output device only once at startup:

```
app -> silent virtual cable (stable endpoint ID, read once)
    -> per-process loopback capture -> the real device of the monitor its window is on
```

Each app gets its own channel (process loopback, Windows 10 2004+). The app is restarted once
when first adopted; after that, moving its window only changes the downstream sink.

Requires a virtual audio cable. The helper script downloads the official installer from
VB-Audio (no third-party binary is bundled):

```powershell
powershell -ExecutionPolicy Bypass -File tools/install-virtual-cable.ps1
```

Latency is roughly 0.2–0.3 s (20 ms capture + 200 ms buffer + 100 ms endpoint) — fine for
music and video, not for rhythm games. DRM-protected content may refuse loopback capture.

## Known limitations

1. **Granularity is the process, not the window.** Routing uses the undocumented
   `Windows.Media.Internal.AudioPolicyConfig.SetPersistedDefaultAudioEndpoint(pid, …)`, which
   is equivalent to choosing an output device for an app in the volume mixer. Multiple windows
   of one app spread across two screens cannot be separated; audio follows the app's main
   window.
2. **Undocumented API.** A major Windows update can change it and break routing.
3. **Only apps that are currently producing audio appear in the live list.**
4. Process identity is the image name (no `.exe`, case-insensitive); all instances of one exe
   share a single rule.
5. Running normally applies routing immediately. Use `--dry-run` when unsure.
6. **A minimized player window may be attributed to the wrong monitor** (inherited from
   upstream). The upstream fix PR is not merged here yet — pull requests welcome.
7. **Some apps read the output device once at startup** and will not migrate audio that is
   already playing. Restart the app, or assign it on the home page and tick *save to list* so
   the watchdog applies the policy before the app creates its audio stream.
8. App-list assignment is keyed by image name, not by window.

## Troubleshooting

The home page status line is the last routing result; the full decision chain is in
`routing.log`. Settings → *Diagnostics* offers three buttons: **run self-test**, **listen to
each device in turn**, and **open log**. If `0x80070005` (access denied) appears, the process
was started from a restricted context — launch it from Explorer or a desktop shortcut.

Before filing an issue, attach the self-test report and the relevant `routing.log` lines
(review them for usernames and device names first).

## Build from source

Requires the **.NET SDK 8 or later** (`net8.0-windows` target; the .NET 10 SDK works too).

```powershell
git clone https://github.com/swblueice/VideoTurner.git
cd VideoTurner
dotnet build src/VideoTurner/VideoTurner.csproj -c Release
src/VideoTurner/bin/Release/net8.0-windows/VideoTurner.exe --dry-run
```

Packaging (requires [Inno Setup 6](https://jrsoftware.org/isdl.php); the script finds
`ISCC.exe` on `PATH`, in `%ProgramFiles(x86)%\Inno Setup 6`, or accepts `-Iscc <path>`):

```powershell
powershell -ExecutionPolicy Bypass -File tools/make-installer.ps1
powershell -ExecutionPolicy Bypass -File tools/make-portable-zip.ps1
```

## Requirements

- Windows 10 1809 (17763) or newer, or any Windows 11; x64
- Virtual channel mode: Windows 10 2004+ (process loopback) plus a virtual audio cable
- Released binaries need no .NET runtime installed; building from source needs .NET SDK 8+

## Contributing

Issues and pull requests are welcome. See [CONTRIBUTING.md](CONTRIBUTING.md).

## License

MIT — see [LICENSE](LICENSE). Derived from
[ScreenSound](https://github.com/twibster/ScreenSound) (MIT, Copyright © 2026 Omar Omran);
the original notice is preserved in
[`LICENSE-ScreenSound-MIT.txt`](LICENSE-ScreenSound-MIT.txt). Third-party components are listed
in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
