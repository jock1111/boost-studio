# Testing

This document separates automated tests, real system smoke tests, and audio
verification that is still blocked by the test machine's Equalizer APO setup.

## Automated Tests

Latest result:

```text
dotnet test BoostStudio.sln -c Debug
Passed! - Failed: 0, Passed: 112, Skipped: 0, Total: 112
```

The suite targets `BoostStudio.Core` and covers:

- **DspMathTests** — peaking/low-shelf/high-shelf math, filter building, maximum
  static boost, headroom reservation, response evaluation.
- **DspConfigBuilderTests** — Bass/Volume/Equalizer independence (including all
  ON/OFF combinations), master bypass, auto headroom, AGC runtime trim, device
  scope syntax, generated configuration grammar.
- **AutoGainControllerTests** — disabled state, first-frame handling, attack
  rate limiting, clamp at −24 dB, recovery, reset, disable, long-gap reset.
- **FftAndSpectrumTests** — FFT correctness, buffer validation, bass detection,
  clipping counts, silence/short buffers, sample-rate clamping, log axis mapping.
- **EqualizerApoConfigStoreTests** — user config preservation, backups, managed
  file write/validation, integration removal, baseline restore, malformed and
  orphan markers, backup-failure abort, stale-baseline/conflict detection,
  concurrent user edits, corrupted managed file replacement.
- **StorageTests** — settings round-trip and normalization, corrupt settings
  recovery, preset normalization, backup export/import, invalid backup rejection.
- **SettingsAndPresetsTests** — safe defaults, module independence, master
  override, no shared collections, schema migration (including Loudness 20 → 0
  for old settings), effective gain math, presets, hotkeys.
- **AudioEngineServiceTests** — NotInstalled, idle, integrate/apply, bypass,
  disconnect, baseline restore, reconnect, APO-not-enabled state and scoped
  device handling with an injected readiness probe.
- **DeviceScopingTests** — all-output scope, selected-output scope, device
  pattern generation and matching.
- **ApoAssociationTests** — endpoint GUID extraction, known EAPO CLSID matching,
  access-grant rejection of missing folders and invalid SIDs.

## Build Results

- Debug build: 0 warnings, 0 errors.
- Release publish: self-contained single-file `BoostStudio.exe`, version 1.0.0,
  approximately 164 MiB.
- Portable ZIP: `artifacts/BoostStudio-1.0.0-win-x64-portable.zip`.
- MSI: `artifacts/BoostStudio-1.0.0-win-x64.msi`, built with WiX Toolset 5.0.2.
- MSI silent install exit code 0; installed executable launched and responded.
- MSI silent uninstall exit code 0; install folder and Start Menu shortcut were
  removed, while `%LOCALAPPDATA%\BoostStudio\settings.json` was preserved.

## Real System Smoke Tests

These tests ran on the actual development machine with Equalizer APO 1.4.2
installed. No audio result is inferred from these tests.

- **Published EXE, standard user** — launched `artifacts/publish-x64/BoostStudio.exe`
  with `--minimized`. It loaded the real settings file, saved a schema-v2
  migration, wrote the DSP configuration, and logged that Equalizer APO was not
  registered on any output.
- **Real config preservation** — the real
  `C:\Program Files\EqualizerAPO\config\config.txt` retained the user's
  `Preamp`, `Include` and `GraphicEQ` lines while Boost Studio's managed block
  was added.
- **Real APO association check** — neither Equalizer APO CLSID
  (`{EACD2258-FCAC-4FF4-B36D-419E924A6D79}` pre-mix,
  `{EC1CC9CE-FAED-4822-828A-82A81A6F018F}` post-mix) is present in the render
  endpoint `FxProperties`. The UI therefore reports "configuration written, APO
  not registered; audible processing not verified".
- **Monitoring** — WASAPI loopback started on `Speakers (4- HyperX QuadCast S)`
  at 48 kHz, 2 channels.
- **Hotkey conflict** — `Ctrl+Alt+M` was already registered by another
  application; Boost Studio logged the failure and kept running.

Backups created before these tests:

- `artifacts/eapo-config-backup-20261102-074149.txt`
- `artifacts/eapo-booststudio-backup-20261102-074149.txt`
- `artifacts/msi-install.log`
- `artifacts/msi-uninstall.log`

## Not Verified

The following could **not** be verified in this environment:

- **Audible DSP processing** — no render endpoint has the Equalizer APO APO
  registered. A configuration file was written, but no controlled tone was
  measured through the APO.
- **Frequency-response accuracy** — requires APO-enabled output plus loopback or
  external measurement.
- **Clipping/headroom behavior on real content** — requires audible output and a
  known full-scale signal.
- **AGC response to real program material** — controller unit tests pass; no real
  loopback test was performed.
- **Per-device audible isolation** — scoped config syntax is tested, but no
  second APO-enabled output was available for an audible comparison.
- **Interactive MSI UI** — only silent install/uninstall were exercised.
- **Windows default-device switching** — best-effort IPolicyConfig path was not
  exercised in this run.
- **Tray interaction and startup-with-Windows** — processes and persistence were
  exercised, but GUI interaction across DPI scales was not.

## Manual Audio Verification Procedure

When an output has Equalizer APO enabled:

1. Reboot after enabling the APO in Equalizer APO's Device Selector.
2. Confirm **Settings → APO status by output** shows the output as enabled.
3. Play a 50 Hz, 250 Hz, 1 kHz and 10 kHz controlled tone through the output.
4. With Bass Booster off, capture the loopback level/spectrum as a baseline.
5. Enable Bass Booster at 50% and repeat. The low-frequency level must rise while
   1 kHz remains essentially unchanged.
6. Enable only Volume Booster with Loudness at 0 and repeat. The overall level
   must rise without tonal change.
7. Set Loudness to 50 and confirm low/high shelves appear.
8. Enable both boosters and confirm the combined response matches the individual
   responses.
9. Toggle each module OFF in turn and confirm only that module's effect
   disappears.
10. Use a near-full-scale signal and confirm the reported net gain includes the
    automatic headroom subtraction. AGC is a slow controller, not a limiter.

Until steps 3–10 are performed on an APO-enabled endpoint, Boost Studio's
honest status remains **"configuration written; audible processing not
verified."**
