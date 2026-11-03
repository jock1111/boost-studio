# Limitations

Boost Studio provides system-wide processing through Equalizer APO, but there
are deliberate and environment-dependent limits. This document lists what it
does **not** do and what remains unverified.

## Audio Processing Is Not Audibly Verified in This Build

Equalizer APO 1.4.2 is installed on the test machine, and Boost Studio writes a
valid DSP configuration. However, neither Equalizer APO APO CLSID is registered
on any render endpoint in that environment. Boost Studio therefore reports:

> Configuration written; Equalizer APO is not registered on the target output.
> Audible processing was not verified.

A written config file is **not** proof of audible processing. To enable
processing, open Equalizer APO's Device Selector / Configurator, enable the APO
for the output, apply, and reboot. Then recheck the output in
**Settings → APO status by output**.

## No Sample-Accurate Limiter

Equalizer APO 1.4.x static filters do not include a native limiter. Boost Studio
does **not** claim to have a sample-accurate limiter.

- **Auto Headroom** reserves preamp headroom equal to the maximum combined static
  filter boost (bass + EQ + loudness) plus a 0.5 dB safety margin. This prevents
  static clipping from the filters themselves.
- **Peak AGC** is a slow, real-output peak-based controller. It reduces gain in
  0.5–6 dB steps after measuring loopback peaks. It protects output over time,
  not sample-by-sample.
- If you need a sample-accurate limiter, use a VST limiter (not included) or
  other external DSP.

## No ASIO or WASAPI Exclusive Mode Support

Equalizer APO processes the Windows shared-mode audio path. ASIO and WASAPI
exclusive-mode streams bypass that path, so Boost Studio cannot affect them.

## No Custom Kernel Driver

Boost Studio installs no kernel driver. Processing is performed by Equalizer
APO's existing user-mode `EqualizerAPO.dll`.

## Device Scope Is Not Automatic

Boost Studio can process all APO-enabled outputs or pin DSP to a selected
output. It does not reconfigure Equalizer APO when Windows changes the default
device. If you switch outputs, verify the selected processing output and the APO
association in Settings.

The APO association shown in Settings is registry evidence that the APO is
attached to an endpoint. It is not an audio measurement.

## Permissions

The main UI is designed to run as a standard user. Writing to
`C:\Program Files\EqualizerAPO\config` normally requires elevation. The
**Grant access once** action runs a one-time elevated helper that grants the
current user:

- Modify on `config.txt`
- Modify on `BoostStudio.txt`
- Create-file permission on the config folder

It does not grant write access to the Equalizer APO program folder and does not
change unrelated configuration. If Windows, Equalizer APO, or another tool
resets those permissions, the grant must be repeated. Restarting the whole UI as
administrator remains available as a fallback for that session.

## Config Changes Are Conservative, Not Magical

Boost Studio preserves user content outside its managed block and creates
timestamped backups. It aborts if a backup cannot be created, detects concurrent
edits and stale baselines, repairs malformed managed markers, and replaces
corrupted managed files non-destructively. If another program rewrites
`config.txt` while Boost Studio is running, a later apply may overwrite that
change; review the conflict warning and use **Restore baseline** deliberately.

## Volume Booster Is Not a System Volume Replacement

Volume Booster applies digital gain to the processed stream. It does not move
the Windows master volume slider. If Windows is muted or the master volume is
zero, Boost Studio's processing is inaudible.

## Loudness Is Off by Default

The loudness contour is an optional static low/high shelf. It defaults to 0% so
volume-only operation preserves tonal balance. Old settings that were created
with the previous default of 20% are migrated to 0%; an explicit newer value is
preserved.

## Audio Enhancements Interference

Driver-level enhancements (bass boost, loudness equalization, virtual surround)
can interfere with Equalizer APO. Disable them for the output when comparing
results.

## Environment Coverage

Not verified on this machine:

- Audible output through an APO-enabled endpoint.
- Frequency-response accuracy against a reference measurement.
- Real-content clipping and AGC behavior.
- Multiple APO-enabled outputs and audible per-device isolation.
- Interactive installer UI, tray interaction across DPI scales, and
  startup-with-Windows behavior.
- Windows default-device switching through IPolicyConfig.

These require an APO-enabled output, controlled signals, and physical playback
hardware. See [TESTING.md](TESTING.md) for the exact procedure and evidence.
