# Testing

This document separates automated tests, real system smoke tests, and measured
audio evidence. It also records what remains environment-dependent or requires
human listening.

## Automated Tests

Latest result:

```text
dotnet test BoostStudio.sln -c Debug
Passed! - Failed: 0, Passed: 147, Skipped: 0, Total: 147
```

The suite targets `BoostStudio.Core` and covers:

- **DspMathTests** — peaking/low-shelf/high-shelf math, filter building, maximum
  static boost, volume-preserving vs. safe-mode headroom reservation, bass
  selectivity (flat at 1 kHz, boost at 50–100 Hz), response evaluation.
- **DspConfigBuilderTests** — Bass/Volume/Equalizer independence (including all
  ON/OFF combinations), 1 kHz level preservation, 50–100 Hz bass gain, combined
  operation, Protect-headroom static protection, smooth module toggling, master
  bypass, auto headroom, AGC runtime trim, device scope syntax, generated
  configuration grammar.
- **AutoGainControllerTests** — disabled state, first-frame handling, attack
  rate limiting, clamp at −24 dB, recovery, reset, disable, long-gap reset.
- **FftAndSpectrumTests** — FFT correctness, buffer validation, bass detection,
  clipping counts, silence/short buffers, sample-rate clamping, log axis mapping.
- **EqualizerApoConfigStoreTests** — user config preservation, backups, managed
  file write/validation, integration removal, baseline restore, malformed and
  orphan markers, backup-failure abort, stale-baseline/conflict detection,
  concurrent user edits, corrupted managed file replacement.
- **StorageTests** — settings round-trip and normalization, corrupt settings
  recovery, preset normalization, backup export/import, profile backup
  round-trip, profile-file recovery, invalid backup rejection.
- **SoundProfileTests** — capture/apply round-trip of processing state only,
  metadata-insensitive matching, deep-copied EQ curves, duplicate id/name
  normalization, profile-store round-trip and `.bak` recovery.
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
- **ApoReadinessTests** — default-output targeting, scoped targeting, missing
  devices, and suggested APO-enabled output selection.

## Build Results

- Debug build: 0 warnings, 0 errors.
- `dotnet test BoostStudio.sln -c Debug`: 147 passed, 0 failed.
- Release publish: self-contained single-file `artifacts\publish-x64\BoostStudio.exe`,
  version 1.1.1 (`1.1.1+77acdbaafc98664432e6b760d9b72b7dd89e3d17`),
  171,875,897 bytes (~164 MiB), SHA-256
  `52640A4D854713ECDEA99F0BEF4F51701F4036A22D6AF023F3914EAAF04460BC`.
- Portable ZIP: `artifacts\BoostStudio-1.1.1-win-x64-portable.zip`,
  70,011,942 bytes (~66.8 MiB), SHA-256
  `3E4FA8348F3B871D6ABFC6BFF71CE36AAD0460519BEA1F300B2BF4BC883AE812`.
- MSI: `artifacts\BoostStudio-1.1.1-win-x64.msi`, 56,369,152 bytes (~53.8 MiB),
  SHA-256 `16D7E92EC5E7CBD5395A6F0C151B3A664FCFC283B20874E97E75A7DC8B277DEC`,
  built with WiX Toolset 5.0.2.
- The 1.1.1 MSI was silently installed and uninstalled on this machine: both
  operations exited 0, the installed EXE reported version 1.1.1.0, and its
  `--audio-self-test` also passed. The previous 1.0.0 MSI smoke test is recorded
  below for reference.

## Real System Smoke Tests

These tests ran on the actual development machine with Equalizer APO 1.4.2
installed.

### Measured bass volume-preservation (Boost Studio 1.1.1)

`artifacts\publish-x64\BoostStudio.exe --audio-self-test` was run on this
machine's APO-enabled default output, `Headphones (JBL LIVE660NC)`, at 48 kHz,
2 channels. It exited 0 and wrote
`%LOCALAPPDATA%\BoostStudio\diagnostics\audio-self-test-20261112-171547.txt`.

The bass test configuration is generated with **auto headroom enabled** and
**Protect headroom disabled**, so it measures the default volume-preserving
path rather than a special test-only bypass.

- Current DSP configuration (Bass Booster on): 1 kHz baseline **−24.9 dBFS**;
  flat reference **−24.9 dBFS** → **0.0 dB** broadband change.
- Bass Booster low shelf at 55 Hz: measured **+8.3 dB** (DSP prediction +8.3 dB).
- Bass Booster selectivity at 1 kHz: measured **+0.1 dB** (DSP prediction
  +0.1 dB).
- −18 dB preamp step: measured **−18.2 dB** (expected −18.0 dB).
- Volume Booster preamp: measured **+12.0 dB** (expected +12.0 dB).

Measured values come from WASAPI loopback; expected values are DSP predictions.
The installed 1.1.1 MSI produced the same measurements before it was uninstalled.

### Measured audio self-test (Boost Studio 1.1.0, historical)

`artifacts\publish-x64\BoostStudio.exe --audio-self-test` was run with the
processing scope temporarily pinned to `Speakers (4- HyperX QuadCast S)`. It
exited 0 and wrote
`%LOCALAPPDATA%\BoostStudio\diagnostics\audio-self-test-20261107-214231.txt`:

- Current DSP configuration with −18 dB test preamp: measured **−18.0 dB**
  (expected −18.0 dB).
- Volume Booster preamp: measured **+12.0 dB** (expected +12.0 dB).
- Bass Booster low shelf at 55 Hz: measured **+8.3 dB** (expected +8.3 dB).
- Bass Booster selectivity at 1 kHz: measured **+0.1 dB** (expected +0.1 dB).
- Loopback device: `Speakers (4- HyperX QuadCast S)`, 48 kHz, 2 channels.

The user's saved settings were restored afterward (`ApplyToAllDevices = true`).
An earlier 1.0.0 run on the same endpoint also passed (−18.0 dB step, +6.0 dB
volume, +8.3 dB bass) and a separate process measured +5.9 dB, confirming shared
post-mix processing rather than a self-generated tone.

### Bluetooth endpoint self-test

A second run was scoped to `Headphones (Kevin speaker)`
(`{88d9ed67-73a2-4c29-9051-dd7b090fb1ec}`, a Bluetooth A2DP endpoint that the
user had since enabled in Equalizer APO's Device Selector). Report:
`audio-self-test-20261107-220151.txt`. The measured steps all passed through the
Bluetooth loopback:

- −18 dB preamp step: measured **−18.0 dB**.
- Volume preamp: measured **+12.0 dB**.
- Bass low shelf at 75 Hz: measured **+20.6 dB** (expected +21.1 dB).
- Bass selectivity at 1 kHz: measured **0.0 dB** (expected −0.1 dB).

This proves Equalizer APO processing reaches Bluetooth endpoints on this
machine, contradicting the older "Bluetooth is unsupported" assumption. The
overall verdict was "not verified for current configuration" only because every
module was disabled in the saved settings at test time (an all-off setup writes
a bypass configuration by design). Settings were restored afterward.

### Current endpoint state

`Headphones (Kevin speaker)` is a Bluetooth endpoint
(`BTHENUM\{0000110b-…}\…`, A2DP). It now has the EAPO pre-mix/post-mix CLSIDs
registered, as do `Speakers (4- HyperX QuadCast S)`, `Digital Output` and
`Oculus Virtual Audio Device` — the user enabled several outputs in Equalizer
APO's Device Selector. Boost Studio 1.1.0 labels Bluetooth endpoints in
**Settings → APO status by output** and still offers **Use that output** when
the current default is unprocessed but another output is enabled.

### Earlier smoke tests (1.0.0)

- **Published EXE, standard user** — launched `artifacts/publish-x64/BoostStudio.exe`
  with `--minimized`. It loaded the real settings file, saved a schema-v2
  migration, wrote the DSP configuration, and logged that Equalizer APO was not
  registered on the then-default output.
- **Real config preservation** — the real
  `C:\Program Files\EqualizerAPO\config\config.txt` retained the user's
  `Preamp`, `Include` and `GraphicEQ` lines while Boost Studio's managed block
  was added.
- **Monitoring** — WASAPI loopback started on `Speakers (4- HyperX QuadCast S)`
  at 48 kHz, 2 channels.
- **Hotkey conflict** — `Ctrl+Alt+M` was already registered by another
  application; Boost Studio logged the failure and kept running.
- **MSI** — the 1.0.0 MSI silent install/uninstall smoke test passed; logs are
  listed below. The 1.1.0 MSI was built but not installed in this run.

Backups and logs created before these tests:

- `artifacts/eapo-config-backup-20261102-074149.txt`
- `artifacts/eapo-booststudio-backup-20261102-074149.txt`
- `artifacts/msi-install.log`
- `artifacts/msi-uninstall.log`

## Not Verified

The following could **not** be fully verified in this environment:

- **Subjective audibility** — loopback measurement proves the DSP reaches the
  APO-enabled endpoint, but only the listener can confirm what they hear on
  their speakers/headphones.
- **Frequency-response accuracy** — requires a calibrated external microphone or
  reference measurement, not just loopback levels.
- **Clipping/headroom behavior on real program material** — the controlled tone
  passed; real music/game content was not measured.
- **AGC response to real program material** — controller unit tests pass; no
  real loopback test of the slow peak controller was performed.
- **Per-device audible isolation** — only one output (`Speakers (4- HyperX
  QuadCast S)`) has Equalizer APO registered, so a second APO-enabled output was
  not available for comparison.
- **Current default output** — `Headphones (Kevin speaker)` is not APO-enabled,
  so processing on that endpoint is unverified by design.
- **Interactive MSI UI** — the 1.1.0 MSI was built but not installed; only the
  earlier 1.0.0 silent install/uninstall was exercised.
- **Windows default-device switching** — the new **Use that output** command
  uses the best-effort IPolicyConfig path; it was implemented and unit-covered
  indirectly, but the live switch was not exercised in this run.
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
10. Use a near-full-scale signal and confirm the clipping warning appears. In the
    default volume-preserving bass mode the shelf is not subtracted from the
    preamp, so very hot bass can clip; enable **Protect headroom** for a static
    no-clipping guarantee. AGC is a slow controller, not a limiter.

The automated `--audio-self-test` loopback procedure covers the controlled-tone
portion of this checklist on an APO-enabled endpoint. Steps that require
listening, real program material, or external measurement still need a human.
Until those are performed, Boost Studio's honest status for subjective
audibility remains **"processing verified by loopback; confirm by ear."**
