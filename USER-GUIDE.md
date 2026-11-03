# User Guide

## Master Switch

The **Master Processing** toggle at the top of the dashboard controls whether Boost Studio's DSP is applied. When bypassed, all module settings are retained but no audio is processed. This is useful for quick comparison or temporary disabling without losing your curve.

## Bass Booster

Bass Booster applies a low-shelf filter to increase low-frequency energy.

### Controls

- **Intensity** (0–100%) — Controls the overall bass strength. Maps to the maximum shelf gain.
- **Frequency** (20–250 Hz) — The corner/cutoff frequency of the low shelf. Lower values extend deeper; higher values focus on upper bass.
- **Max Gain** (0–24 dB) — The maximum boost applied at 100% intensity.
- **Q** (0.30–1.40) — Shelf slope. Lower Q = broader, smoother bass; higher Q = tighter, more upper-bass bite.

### Presets

- **Deep Bass** — Broad, room-filling sub foundation.
- **Punchy Bass** — Focused upper-bass impact for kick drums and electronic music.
- **Sub-Bass** — Very low extension for subwoofers.
- **Extreme Bass** — Maximum bass energy. Auto headroom is strongly recommended.
- **Custom** — Your own settings.

### Visualization

The live bass visualization shows the dominant bass frequency (20–250 Hz) as a vertical bar on the dashboard spectrum.

## Volume Booster

Volume Booster adds digital gain and optional loudness contour. It does not use the Windows master volume slider.

### Controls

- **Boost** (0–100%) — Percentage of the gain ceiling.
- **Ceiling** (3–24 dB) — Maximum digital boost.
- **Preamp** (–12 to +12 dB) — Manual trim added to the boost.
- **Loudness** (0–100%) — Optional static low + high shelf for Fletcher-Munson loudness compensation. It is **off by default (0%)**, so volume-only processing preserves the original tonal balance.
- **Auto Gain Control (AGC)** — Peak-based slow AGC that reduces gain if output clips. Deliberately not a limiter; it applies corrections in 0.5–6 dB steps after measuring actual loopback peaks.

### Headroom and net gain

When **Auto Headroom** is enabled, Boost Studio reserves preamp headroom equal to
the maximum combined static filter boost (bass + EQ + loudness) plus a 0.5 dB
safety margin. The Volume page shows **Net gain (preamp)** after that subtraction,
not just the requested boost. AGC can add further runtime trim.

Bass Booster and Volume Booster are independent. Turning one off leaves the
other's DSP, parameters and persisted state untouched. The four ON/OFF
combinations are covered by automated tests.

## Equalizer

Ten ISO octave bands (31 Hz – 16 kHz) with peaking filters.

### Bands

- Drag sliders to adjust gain (–12 to +12 dB).
- Double-click any slider to reset it to 0 dB.
- The response graph updates in real time.

### Presets

Built-in presets:
- **Gaming** — Footsteps and cues without mud.
- **Music** — Warm low end, open upper mids.
- **Movies** — Impact and dialogue clarity.
- **EDM** — Club sub and bright energy.
- **Hip-Hop** — Sub weight with vocal edge.
- **Rock** — Guitar body and drum punch.
- **Vocal** — Forward speech and vocals.
- **Balanced** — Neutral response.

User presets:
- **Save** — Stores the current ten-band curve as a reusable preset.
- **Rename** — Changes the name of a user preset.
- **Delete** — Removes a user preset.

## Outputs and Processing Scope

Settings separates **monitoring** from **processing**:

- **Monitoring device** — what Boost Studio's spectrum and level meters read via
  WASAPI loopback. It does not change Windows' output.
- **Processing output** — where Equalizer APO applies Boost Studio's DSP.
  - **All APO outputs** writes `Device: all`, so every output with the APO
    enabled is processed.
  - Turning the switch off lets you pin processing to one selected output. The
    Windows default device is not changed.

Under **APO status by output**, each active output is marked **Enabled** or
**Not enabled**. If no selected output has the APO enabled, Boost Studio can
still write the configuration but will state that audible processing is not
verified.

## Permissions

Boost Studio's main window runs as a standard user. To update Equalizer APO's
configuration without keeping the app elevated:

1. Open **Settings → Equalizer APO**.
2. Choose **Grant access once**.
3. Approve the single Windows administrator prompt.

The helper grants only:

- Modify on `config.txt`
- Modify on `BoostStudio.txt`
- Create-file permission on Equalizer APO's config folder

It does not grant access to the Equalizer APO program folder. If permissions are
reset later, run the grant again. **Restart elevated** remains available as a
temporary fallback.

## Engine Status

The Settings page shows Equalizer APO detection, integration state, APO
association, and configuration paths.

- **Detecting** — Searching for the Equalizer APO installation.
- **Missing** — Equalizer APO is not installed. Follow the on-screen instructions.
- **Ready to Connect** — Equalizer APO is installed but not integrated. Click
  **Connect** or enable a module.
- **DSP configuration written and APO attached** — The configuration was written
  and the APO is registered on the target output. This still does not measure
  audible output.
- **Configuration written, APO not enabled** — The config file exists, but no
  target output has the EAPO APO registered. Open Equalizer APO's Device
  Selector, enable the output, reboot, then recheck.
- **Bypassed** — Integration exists but master processing is off.
- **Permission Required** — The config files are not writable. Use **Grant
  access once**, or restart elevated for this session.
- **Error** — A configuration write or detection step failed.

Every state also shows a verification line. Unless real audio was measured,
Boost Studio says **"audible processing was not verified"** rather than claiming
that processing is fully operational.

## Tray and Hotkeys

- **Tray Icon** — Shows Boost Studio in the system tray. Right-click for quick toggles (master, bass, volume, EQ) or open/close the app.
- **Minimize to Tray** — When enabled, minimizing the main window hides to the tray instead of the taskbar.
- **Close to Tray** — When enabled, closing the window hides to the tray instead of exiting.
- **Hotkeys** — Global shortcuts (default: Ctrl+Alt+B for Bass, Ctrl+Alt+V for Volume, Ctrl+Alt+M for Master). Customize in Settings.

## Backup and Restore

- **Export** — Saves settings and user EQ presets to a `.boostbackup` ZIP file.
- **Import** — Restores settings and presets from a backup file.
- **Restore Baseline** — Restores the Equalizer APO `config.txt` captured before
  Boost Studio first connected. The current file is backed up first. If you
  edited `config.txt` after that baseline was captured, Boost Studio warns that
  those later edits will be replaced; the action is explicit and destructive by
  design.

## Troubleshooting

### "Engine missing"
Install Equalizer APO from SourceForge, then enable its APO for your output device in the Equalizer APO Configurator. Reboot Windows after enabling.

### "Permission required"
Use **Settings → Grant access once** and approve the one-time UAC prompt. If that
is not possible, **Restart as administrator** for this session. Do not leave the
whole app elevated for routine use.

### Processing seems inactive
Check **Settings → APO status by output**. If the output is marked **Not
enabled**, open Equalizer APO's Device Selector, enable the APO for that output,
apply, and reboot. A written configuration alone does not prove audible
processing. Also confirm the processing scope (all outputs or the selected
output) matches the device you are listening on.

### Hotkeys not working
If a hotkey conflicts with another application, you will see a warning when applying shortcuts. Choose a different combination in Settings.

## Audio Monitoring

Boost Studio monitors your default audio output device via WASAPI loopback. This includes Equalizer APO processing, so the spectrum and meters reflect the actual output after DSP.

- To monitor a different device, select it in the **Audio** section of Settings.
- Monitoring does not affect the output device; it only reads the stream.
