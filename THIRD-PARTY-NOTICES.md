# Third-Party Notices

Boost Studio uses the following third-party software and libraries:

## NAudio 2.2.1

- **License**: MIT License
- **Project**: https://github.com/naudio/NAudio
- **Use**: WASAPI loopback audio monitoring, test tone playback, audio device enumeration
- **Attribution**: NAudio is maintained by Mark Heath and contributors.

## xUnit 2.5.3

- **License**: Apache License 2.0
- **Project**: https://xunit.net/
- **Use**: Unit testing framework for BoostStudio.Core.Tests
- **Attribution**: xUnit is maintained by the xUnit team.

## .NET 8.0

- **License**: MIT License
- **Project**: https://dotnet.microsoft.com/
- **Use**: Runtime framework for Boost Studio
- **Attribution**: .NET is developed by Microsoft.

## Equalizer APO

- **License**: GPL-2.0-or-later
- **Project**: https://sourceforge.net/projects/equalizerapo/
- **Use**: System-wide audio processing engine; Boost Studio integrates with Equalizer APO by writing a managed include block and DSP file.
- **Attribution**: Equalizer APO is developed by jbegt. Boost Studio does not distribute Equalizer APO binaries; users must install it separately.
- **Note**: Boost Studio's integration with Equalizer APO does not modify Equalizer APO itself. Boost Studio only writes configuration text files to Equalizer APO's config directory.

## Windows APIs

Boost Studio uses the following Windows APIs (via p/invoke or framework libraries):

- **Equalizer APO ConfigPath Registry Key** – Read from `HKLM\SOFTWARE\EqualizerAPO\ConfigPath` to locate the Equalizer APO installation.
- **Uninstall Entry Registry Keys** – Read from `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall` to find Equalizer APO installation path and version.
- **IPolicyConfig COM Interface** – Used (best-effort) to change the default audio device when requested by the user. This may fail in some environments due to Windows security policies.
- **DWM (Desktop Window Manager) APIs** – Used for window chrome dark mode and rounded corners on Windows 11.
- **Shell APIs** – Used for opening URLs and folders in File Explorer.
- **Registry Run Key** – Used to add/remove Boost Studio from Windows startup (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`).

## Icons and Fonts

- **Segoe UI Variable / Segoe UI Display** – Windows 11 system fonts (included with Windows).
- **Segoe MDL2 Assets** – Windows 11 system icon font (included with Windows).
- **App Icon** – Custom icon generated for Boost Studio.

## No Bundled Binaries

Boost Studio does not bundle or distribute Equalizer APO, NAudio, xUnit, or .NET runtime binaries in its source repository. These are either:
- Retrieved via NuGet during build (NAudio, xUnit).
- Installed separately by the user (Equalizer APO).
- Provided by the operating system (.NET runtime, system fonts, Windows APIs).
