# Limitations

Boost Studio provides system-wide processing through Equalizer APO, but there
are deliberate and environment-dependent limits. This document lists what it
does **not** do and what remains unverified.

## Audio Processing Is Verified Only on an APO-Enabled Output

Equalizer APO 1.4.2 is installed on the development machine, and Boost Studio
writes a valid DSP configuration. Measured WASAPI loopback verification has
passed on the Equalizer APO–enabled `Speakers (4- HyperX QuadCast S)` endpoint:

- −18 dB preamp step: measured −18.0 dB (expected −18.0 dB).
- Volume Booster +6 dB: measured +6.0 dB (expected +6.0 dB).
- Bass low shelf at 55 Hz: measured +8.3 dB (expected +8.3 dB).
- Bass selectivity at 1 kHz: measured +0.1 dB (expected +0.1 dB).
- A separate process playing through the same endpoint: +5.9 dB, confirming
  shared post-mix processing rather than a self-generated tone.

This is measured loopback evidence, not proof that every future output or
hardware path is processed. When Windows switches the default output to an
endpoint where Equalizer APO is not registered, Boost Studio correctly
reports:

> Configuration written; Equalizer APO is not registered on the target output.
> Audible processing was not verified.

A written config file is **not** proof of audible processing. To enable
processing, open Equalizer APO's Device Selector / Configurator, enable the APO
for the output, apply, and reboot. Then recheck the output in
**Settings → APO status by output**. If another output is already APO-enabled,
the dashboard offers **Use that output**, which asks before changing the Windows
default. Real listening confirmation on the user's speakers/headphones is still
required; loopback measurement cannot prove subjective audibility.

## No Sample-Accurate Limiter

Equalizer APO 1.4.x static filters do not include a native limiter. Boost Studio
does **not** claim to have a sample-accurate limiter.

- **Auto Headroom** reserves preamp headroom for the maximum static EQ and
  loudness boost plus a 0.5 dB safety margin. Bass Booster is deliberately
  excluded so turning it on does not lower mids and highs.
- **Volume-preserving Bass Booster** boosts the low shelf without a broadband
  preamp cut. The shelf is essentially flat at 1 kHz, so the rest of the
  spectrum keeps its original level. Very hot, near-full-scale bass content can
  still clip in this mode; the UI shows a clipping warning.
- **Protect headroom** is the optional safe mode for Bass Booster. It also
  reserves the shelf gain in the preamp, which guarantees no static clipping
  from the filter but lowers the entire output by the bass amount.
- **Peak AGC** is a slow, real-output peak-based controller. It reduces gain in
  0.5–6 dB steps after measuring loopback peaks. It protects output over time,
  not sample-by-sample.
- If you need a sample-accurate limiter, use a VST limiter (not included) or
  other external DSP.

## No ASIO or WASAPI Exclusive Mode Support

Equalizer APO processes the Windows shared-mode audio path. ASIO and WASAPI
exclusive-mode streams bypass that path, so Boost Studio cannot affect them.

## Bluetooth Outputs Can Be Processed, With Caveats

Equalizer APO's older documentation says Bluetooth is unsupported, but on the
development machine (Windows 11, Equalizer APO 1.4.2) the measured loopback
self-test **did** reach a Bluetooth endpoint: `Headphones (Kevin speaker)`
showed the −18 dB preamp step, +12 dB volume preamp and +20.6 dB bass shelf all
measured correctly. Bluetooth endpoints therefore appear in
**Settings → APO status by output** like any other output and can be enabled in
Equalizer APO's Device Selector.

Caveats:

- Bluetooth codecs (SBC/AAC/aptX) are lossy; heavy bass boost may be compressed
  or limited by the codec itself.
- Some speakers apply their own DSP/EQ, which can mask or alter the processed
  result.
- If processing seems absent on Bluetooth, first confirm a module is actually
  enabled — an all-modules-off setup writes a bypass configuration by design.
- Confirm by ear: loopback proves the APO output reaches the endpoint, but only
  the listener can verify the final wireless signal.

## No Custom Kernel Driver

Boost Studio installs no kernel driver. Processing is performed by Equalizer
APO's existing user-mode `EqualizerAPO.dll`.

## Device Scope Is Not Automatic

Boost Studio can process all APO-enabled outputs or pin DSP to a selected
output. It does not reconfigure Equalizer APO when Windows changes the default
device. If you switch outputs, verify the selected processing output and the APO
association in Settings. When another active output is already APO-enabled, the
dashboard names it and offers **Use that output**, which changes the Windows
default only after confirmation.

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

Verified on the development machine:

- Measured loopback processing on `Speakers (4- HyperX QuadCast S)`, including
  the controlled −18 dB preamp step, Volume +6 dB, Bass +8.3 dB at 55 Hz,
  1 kHz selectivity, and a separate-process shared post-mix check.
- APO association detection for enabled and non-enabled endpoints.
- Config preservation, managed-include repair, ACL grant and profile backup
  round-trip.

Not verified, or not fully verified:

- Subjective audibility on the user's speakers/headphones (loopback is
  objective, but only the listener can confirm what they hear).
- Frequency-response accuracy against a calibrated external microphone.
- Real-content clipping and AGC behavior.
- Multiple simultaneously APO-enabled outputs and audible per-device isolation.
- An output that was not APO-enabled at verification time.
- Subjective confirmation that the Bluetooth endpoint's final wireless signal
  carries the processed audio (loopback proves the APO output, not the codec).
- Interactive installer UI, tray interaction across DPI scales, and
  startup-with-Windows behavior.

These require physical playback hardware, controlled signals, and in some cases
additional reboots. See [TESTING.md](TESTING.md) for the exact procedure and
evidence.
