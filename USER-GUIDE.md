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

## Sound Profiles

Profiles are named, complete sound setups: master switch, auto headroom, Bass
Booster, Volume Booster and the full Equalizer curve. They do not include your
output device, monitoring device, hotkeys, startup options or other application
preferences.

- **Save current setup** captures the live processing state. If the name already
  exists, you can replace that profile.
- **Apply selected profile** writes the profile into the live engine through
  Equalizer APO. Your current monitoring and output routing stay unchanged.
- **Update from current setup** replaces the selected profile with the live
  settings.
- **Rename** and **Delete** manage saved profiles. Deleting a profile does not
  change the live audio settings.
- **Undo last profile change** restores the setup that was live before the most
  recent apply. Undo is available for the current app session.
- **Applied** marks the active profile. **Applied · modified** appears after a
  live control is changed, so you can see that the saved profile no longer
  matches exactly.
- The tray menu's **Profiles** submenu switches profiles without opening the
  window.

Profiles are stored locally in `%LOCALAPPDATA%\BoostStudio\profiles.json` with an
automatic `.bak` recovery copy. Up to 50 profiles are supported.

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
**Not enabled**. If the current Windows default output is not APO-enabled but
another active output is, the dashboard shows that output's name and offers
**Use that output**. Boost Studio asks for confirmation before changing the
Windows default; it never switches audio routing silently.

If no active output has the APO enabled, Boost Studio can still write the
configuration but will state that audible processing is not verified. Open
Equalizer APO's Device Selector, enable the APO on the output, reboot, then use
**Recheck**.

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
  audible output; the state says **Configured — unverified** until the measured
  self-test passes.
- **Processing verified** — The measured loopback self-test passed on this
  session/output. The preceding −18 dB preamp step, Volume and Bass checks all
  matched their expected values.
- **Configuration written, APO not enabled** — The config file exists, but the
  current target output has no EAPO APO registered. If another output has the
  APO, use **Use that output** on the dashboard or Settings. Otherwise open
  Equalizer APO's Device Selector, enable the output, reboot, then recheck.
- **Bypassed** — Integration exists but master processing is off.
- **Permission Required** — The config files are not writable. Use **Grant
  access once**, or restart elevated for this session.
- **Error** — A configuration write or detection step failed.

Every state also shows a verification line. Unless real audio was measured,
Boost Studio says **"audible processing was not verified"** rather than claiming
that processing is fully operational.

## Tray and Hotkeys

- **Tray Icon** — Shows Boost Studio in the system tray. Right-click for quick toggles (master, bass, volume, EQ), saved-profile switching, or open/close the app.
- **Minimize to Tray** — When enabled, minimizing the main window hides to the tray instead of the taskbar.
- **Close to Tray** — When enabled, closing the window hides to the tray instead of exiting.
- **Hotkeys** — Global shortcuts (default: Ctrl+Alt+B for Bass, Ctrl+Alt+V for Volume, Ctrl+Alt+M for Master). Customize in Settings.
- **In-app shortcuts** — `Ctrl+1` Dashboard, `Ctrl+2` Bass, `Ctrl+3` Volume,
  `Ctrl+4` Equalizer, `Ctrl+5` Profiles, `Ctrl+6` Settings, and `Ctrl+M` toggles
  master processing while the window is focused.
- **Window memory** — Boost Studio restores its last size, position and maximized
  state, as long as the window is still visible on a connected display.
- **Second launch** — Starting Boost Studio again activates and reveals the
  running window instead of showing an "already running" message.

## Backup and Restore

- **Export** — Saves settings, sound profiles and user EQ presets to a
  `.boostbackup` ZIP file (format 2).
- **Import** — Restores settings, profiles and presets from a backup file.
  Backups created by older versions contain no profiles; importing one keeps
  your existing profile library instead of deleting it.
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
enabled**, either use the dashboard's **Use that output** suggestion when
another output is already APO-enabled, or open Equalizer APO's Device Selector,
enable the APO for that output, apply, and reboot. A written configuration alone
does not prove audible processing. Also confirm the processing scope (all
outputs or the selected output) matches the device you are listening on.

A common cause is switching the Windows default output to headphones or
speakers that were never enabled in Equalizer APO. Windows allows each endpoint
to have its own APO registration; "all APO outputs" means every output that has
the APO enabled, not every endpoint in Windows.

### Bass boost doesn't work on my Bluetooth speaker

First check that a module is actually enabled — if Bass, Volume and EQ are all
off, Boost Studio writes a bypass configuration by design. Bluetooth endpoints
can be APO-processed (verified by loopback on this machine), but the speaker's
own DSP/codec may mask the effect. Enable the APO for the Bluetooth output in
Equalizer APO's Device Selector, confirm it shows **Enabled** under
**Settings → APO status by output**, then run **Test audio engine**. If the
measured test passes but you still hear no change, the limitation is in the
speaker/codec, not Boost Studio. See [Limitations](LIMITATIONS.md#bluetooth-outputs-can-be-processed-with-caveats).

### Hotkeys not working
If a hotkey conflicts with another application, you will see a warning when applying shortcuts. Choose a different combination in Settings.

## Audio Monitoring

Boost Studio monitors your default audio output device via WASAPI loopback. This includes Equalizer APO processing, so the spectrum and meters reflect the actual output after DSP.

- To monitor a different device, select it in the **Audio** section of Settings.
- Monitoring does not affect the output device; it only reads the stream.
