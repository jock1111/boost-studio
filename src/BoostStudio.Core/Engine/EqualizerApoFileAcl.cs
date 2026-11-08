using System.Security.AccessControl;
using System.Security.Principal;
using BoostStudio.Core.Storage;

namespace BoostStudio.Core.Engine;

/// <summary>
/// Equalizer APO's processing object runs inside audiodg.exe, and the Windows audio
/// service runs as <c>NT AUTHORITY\LocalService</c>. A config file that is moved into
/// Equalizer APO's config folder keeps the security descriptor of the temporary file it
/// was created from (typically only the interactive user, SYSTEM and Administrators).
/// The audio engine then cannot read the file and silently applies no processing at all.
///
/// This helper adds read-only access for the audio service accounts and repairs files
/// that were written before the fix. It never removes existing access rules.
/// </summary>
public static class EqualizerApoFileAcl
{
    private const string AudioServiceAccountName = "NT SERVICE\\Audiosrv";

    private static readonly SecurityIdentifier LocalServiceSid =
        new(WellKnownSidType.LocalServiceSid, null);

    private static readonly SecurityIdentifier UsersSid =
        new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>
    /// Ensures the audio engine can read <paramref name="path"/>. Returns true when at
    /// least one access rule was added. Safe to call repeatedly and on missing files.
    /// </summary>
    public static bool EnsureAudioEngineCanRead(string path, ILogService? log = null,
        bool logChange = true)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var file = new FileInfo(path);
            var security = file.GetAccessControl(AccessControlSections.Access);
            var changed = false;

            changed |= AddReadRule(security, LocalServiceSid);
            changed |= AddReadRule(security, UsersSid);

            // A service SID is resolved from the installed service; if the service is
            // absent the file simply keeps the LocalService/Users grants above.
            try
            {
                var audioServiceSid = (SecurityIdentifier)new NTAccount(AudioServiceAccountName)
                    .Translate(typeof(SecurityIdentifier));
                changed |= AddReadRule(security, audioServiceSid);
            }
            catch (SystemException)
            {
                // The audio service is not installed or its SID cannot be resolved.
            }

            if (!changed)
                return false;

            file.SetAccessControl(security);
            if (logChange)
                log?.Info($"Granted the Windows audio engine read access to {path}.");
            return true;
        }
        catch (Exception ex)
        {
            // ACL repair is best effort. A failure here must not break configuration
            // writing; the readiness/verification path still reports that audio was
            // not measured.
            log?.Warn($"Could not update read access for {path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>True when the audio service accounts already have a read grant.</summary>
    public static bool HasAudioEngineReadAccess(string path)
    {
        if (!File.Exists(path))
            return false;

        try
        {
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                         .Cast<FileSystemAccessRule>())
            {
                if (rule.AccessControlType != AccessControlType.Allow)
                    continue;
                if (!GrantsRead(rule.FileSystemRights))
                    continue;

                if (rule.IdentityReference.Equals(LocalServiceSid))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static bool AddReadRule(FileSecurity security, SecurityIdentifier sid)
    {
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                     .Cast<FileSystemAccessRule>())
        {
            if (rule.AccessControlType == AccessControlType.Allow &&
                rule.IdentityReference.Equals(sid) &&
                GrantsRead(rule.FileSystemRights))
            {
                return false;
            }
        }

        security.AddAccessRule(new FileSystemAccessRule(
            sid,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Allow));
        return true;
    }

    private static bool GrantsRead(FileSystemRights rights) =>
        (rights & FileSystemRights.ReadData) != 0 ||
        (rights & FileSystemRights.Read) == FileSystemRights.Read;
}
