# Boost Studio

System-wide bass, volume and equalizer processing for Windows 10/11.

**Boost Studio** shapes audio from every application by integrating with
[Equalizer APO](https://sourceforge.net/projects/equalizerapo/). Bass Booster,
Volume Booster and the 10-band Equalizer can be enabled independently, so a
volume-only setup does not change the tonal balance unless you choose to add the
optional loudness contour.

## Features

- **Bass Booster** — Low-shelf filter with adjustable intensity (0–100%), corner
  frequency (20–250 Hz), shelf Q, and five presets.
- **Volume Booster** — Digital gain stage with a ceiling, manual trim, optional
  loudness contour (off by default), optional slow peak AGC, automatic headroom,
  peak meter, and clip warning. The UI shows the honest net gain after headroom.
- **10-Band Equalizer** — ISO octave bands (31 Hz–16 kHz), draggable sliders,
  live response graph, built-in and user presets.
- **Independent modules** — Bass and Volume have separate DSP, ON/OFF state,
  parameters, presets, bypass and persistence. All four ON/OFF combinations are
  covered by tests.
- **Processing output scope** — Apply DSP to every APO-enabled output, or pin it
  to one selected output. Monitoring is configured separately.
- **APO status by output** — Settings lists each active output and whether the
  Equalizer APO APO is registered on it.
- **Least-privilege setup** — The main UI runs as a standard user. A one-time
  elevated helper grants your account Modify access to `config.txt` and
  `BoostStudio.txt` plus create-file access to the config folder. The whole UI
  does not need to stay elevated.
- **Config safety** — Boost Studio preserves unrelated Equalizer APO
  configuration, creates timestamped backups, detects concurrent edits and
  malformed managed blocks, and never silently restores over newer user edits.
- **Monitoring** — WASAPI loopback spectrum, bass visualization and output level
  metering on a device chosen independently from the processing scope.
- **Tray & hotkeys** — Tray toggles, global hotkeys, optional start with Windows.
- **Dark Win11-inspired UI** — Responsive knobs and sliders, animated navigation.

## Requirements

- Windows 10/11 build 19041 or later, x64.
- [Equalizer APO](https://sourceforge.net/projects/equalizerapo/) installed
  separately. Boost Studio does not redistribute Equalizer APO.
- The Equalizer APO APO must be enabled for at least one output in Equalizer
  APO's Device Selector / Configurator, followed by a reboot.
- No .NET installation is required for the self-contained release.

## Installation

Release artifacts are in `artifacts/`:

- `BoostStudio-1.0.0-win-x64.msi` — per-user installer. It installs to
  `%LOCALAPPDATA%\Programs\Boost Studio` and does **not** require administrator
  rights. A Start Menu shortcut is created.
- `BoostStudio-1.0.0-win-x64-portable.zip` — portable build. Extract it anywhere
  and run `BoostStudio.exe`.
- `publish-x64/BoostStudio.exe` — self-contained single-file executable.

After launching:

1. If Equalizer APO is missing, follow the on-screen download instructions.
2. Enable Equalizer APO for your output in its Device Selector / Configurator,
   then reboot.
3. In Boost Studio, open **Settings → Equalizer APO**. Use **Grant access once**
   (one UAC prompt) so routine changes can be written without keeping the app
   elevated.
4. Choose the processing output scope and confirm that the output is marked
   **APO enabled**.
5. Enable a module and connect.

## What "connected" means

Boost Studio distinguishes these states:

1. Equalizer APO is installed.
2. The DSP configuration file was written.
3. Equalizer APO is registered on the target output (APO association).
4. Audio was actually measured for audible processing.

Boost Studio can confirm steps 1–3 without elevation remaining active. **Step 4
is not claimed from a written file.** In the current test environment Equalizer
APO 1.4.2 is installed, but no render endpoint has the EAPO APO registered, so
audible processing remains unverified. See [LIMITATIONS.md](LIMITATIONS.md) and
[TESTING.md](TESTING.md).

## Build and test

```powershell
dotnet build BoostStudio.sln -c Debug
dotnet test BoostStudio.sln -c Debug
dotnet publish src\BoostStudio.App\BoostStudio.App.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:DebugType=None -p:DebugSymbols=false -o artifacts\publish-x64
```

The MSI is built with WiX Toolset 5:

```powershell
wix build installer\BoostStudio.wxs -arch x64 -o artifacts\BoostStudio-1.0.0-win-x64.msi
```

## Documentation

- [User Guide](USER-GUIDE.md) — configuration, device scope, permissions, troubleshooting
- [Limitations](LIMITATIONS.md) — no limiter, no ASIO, unverified audio, environment limits
- [Testing](TESTING.md) — 112 automated tests, real smoke tests, pending verification
- [Third-Party Notices](THIRD-PARTY-NOTICES.md) — dependencies and licenses

## License

See [LICENSE](LICENSE) for details.
