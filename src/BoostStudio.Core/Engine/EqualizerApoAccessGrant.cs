using System.Diagnostics;
using System.Security.Principal;
using System.Text;

namespace BoostStudio.Core.Engine;

public sealed record AccessGrantResult(bool Success, string Message, string? Detail = null);

/// <summary>
/// One-time, least-privilege ACL grant for Equalizer APO's configuration folder.
/// The helper runs elevated (through a UAC prompt), but grants the standard user only:
///   * Modify on config.txt
///   * Modify on BoostStudio.txt
///   * create-file (WD) on the config folder
/// It never grants write access to the Equalizer APO installation folder and never
/// rewrites unrelated configuration. Config store writes use a temporary file in the
/// user's own runtime folder, so delete-child rights on the config folder are unnecessary.
/// </summary>
public static class EqualizerApoAccessGrant
{
    public static AccessGrantResult Grant(string configDirectory, string? userSid)
    {
        try
        {
            if (!IsValidSid(userSid, out var sid))
                return new AccessGrantResult(false, "The current Windows user could not be identified.",
                    "A valid user SID is required to grant file access.");

            if (string.IsNullOrWhiteSpace(configDirectory))
                return new AccessGrantResult(false, "No Equalizer APO configuration folder was supplied.");

            var directory = Path.GetFullPath(configDirectory);
            if (!Directory.Exists(directory))
                return new AccessGrantResult(false, "The Equalizer APO configuration folder was not found.", directory);

            if (string.Equals(Path.GetPathRoot(directory), directory, StringComparison.OrdinalIgnoreCase))
                return new AccessGrantResult(false, "Refusing to change permissions on a drive root.", directory);

            var configPath = Path.Combine(directory, "config.txt");
            if (!File.Exists(configPath))
                return new AccessGrantResult(false, "The folder does not contain an Equalizer APO config.txt.", directory);

            var managedPath = Path.Combine(directory, EqualizerApoConfigStore.ManagedFileName);
            try
            {
                Directory.CreateDirectory(directory);
                if (!File.Exists(managedPath))
                    File.WriteAllText(managedPath, EqualizerApoConfigStore.ManagedPlaceholder, new UTF8Encoding(false));

                ClearReadOnly(configPath);
                ClearReadOnly(managedPath);
            }
            catch (Exception ex)
            {
                return new AccessGrantResult(false,
                    "Could not prepare the Equalizer APO files for access.", ex.Message);
            }

            var steps = new[]
            {
                (Target: configPath, Grant: $"*{sid}:(M)", Label: "config.txt"),
                (Target: managedPath, Grant: $"*{sid}:(M)", Label: EqualizerApoConfigStore.ManagedFileName),
                (Target: directory, Grant: $"*{sid}:(WD)", Label: "config folder create-file access")
            };

            foreach (var step in steps)
            {
                var result = RunIcacls(step.Target, step.Grant);
                if (!result.Success)
                    return new AccessGrantResult(false,
                        $"Could not grant access to {step.Label}.", result.Detail);
            }

            return new AccessGrantResult(true,
                "Boost Studio can now update Equalizer APO's configuration without running as administrator.",
                $"Granted Modify on config.txt and {EqualizerApoConfigStore.ManagedFileName}, plus create-file access to the config folder.");
        }
        catch (Exception ex)
        {
            return new AccessGrantResult(false, "The permission grant failed.", ex.Message);
        }
    }

    private static bool IsValidSid(string? value, out string sid)
    {
        sid = string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 200)
            return false;

        try
        {
            var parsed = new SecurityIdentifier(value);
            sid = parsed.Value;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ClearReadOnly(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
    }

    private static AccessGrantResult RunIcacls(string target, string grant)
    {
        try
        {
            var startInfo = new ProcessStartInfo("icacls")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add(target);
            startInfo.ArgumentList.Add("/grant");
            startInfo.ArgumentList.Add(grant);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new AccessGrantResult(false, "Could not start icacls.");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return new AccessGrantResult(false, "icacls timed out.");
            }

            if (process.ExitCode != 0)
            {
                var detail = string.Join(" ", new[] { output, error }
                    .Where(t => !string.IsNullOrWhiteSpace(t)))
                    .Trim();
                return new AccessGrantResult(false, $"icacls exited with code {process.ExitCode}.", detail);
            }

            return new AccessGrantResult(true, "Permissions updated.");
        }
        catch (Exception ex)
        {
            return new AccessGrantResult(false, "Could not run icacls.", ex.Message);
        }
    }
}
