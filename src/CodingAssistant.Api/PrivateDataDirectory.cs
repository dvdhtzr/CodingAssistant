using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CodingAssistant.Api;

/// <summary>Validate owner-local data storage before opening SQLite or credentials.
/// Never silently change permissions on an existing directory or expose its path.</summary>
internal static class PrivateDataDirectory
{
    internal static void Ensure(string path)
    {
        var info = new DirectoryInfo(path);
        if (info.LinkTarget is not null)
            throw new InvalidOperationException("Refusing redirected private data directory");
        if (!info.Exists)
        {
            if (OperatingSystem.IsWindows())
                CreateWindows(path);
            else
                Directory.CreateDirectory(path, UnixFileMode.UserRead |
                    UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        info.Refresh();
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refusing redirected private data directory");
        if (OperatingSystem.IsWindows())
            ValidateWindows(info.GetAccessControl(AccessControlSections.Access |
                AccessControlSections.Owner));
        else if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead |
                     UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            throw new InvalidOperationException("Data directory must be owner-private (0700)");
    }

    internal static void ValidateFile(string path, bool sqliteSidecar = false)
    {
        if (sqliteSidecar && OperatingSystem.IsWindows())
        {
            WindowsSidecarSecurity.Validate(path, ValidateWindows);
            return;
        }
        // SQLite WAL/SHM files can disappear after attributes are read but
        // before the security descriptor is fetched on another connection.
        // Retry that narrow race; never swallow ACL denial or an unsafe file.
        for (var attempt = 0; attempt < 4; attempt++)
        {
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (FileNotFoundException)
            {
                // No file is present at this observation. The containing
                // owner-private directory already constrains newly created files.
                return;
            }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new InvalidOperationException("Refusing redirected private data file");
            try
            {
                if (OperatingSystem.IsWindows())
                    ValidateWindows(new FileInfo(path).GetAccessControl(
                        AccessControlSections.Access | AccessControlSections.Owner));
                else if ((File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead |
                             UnixFileMode.UserWrite)) != 0)
                    throw new InvalidOperationException("Data file must be owner-private (0600)");
                return;
            }
            catch (FileNotFoundException) when (attempt < 3)
            {
                // Observe again, including reparse-point and ACL checks if
                // SQLite recreated the sidecar. No busy wait or permission waiver.
            }
        }
        throw new InvalidOperationException("Private data file could not be validated");
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWindows(string path)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var owner = identity.User ?? throw new InvalidOperationException("Windows owner unavailable");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        foreach (var sid in new[] { owner,
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).Create(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ValidateWindows(FileSystemSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("Windows owner unavailable");
        bool Trusted(SecurityIdentifier sid) => sid.Equals(user) ||
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid);
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !Trusted(owner))
            throw new InvalidOperationException("Private data has an unexpected Windows owner");
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true,
                     typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            if (rule.IdentityReference is SecurityIdentifier sid &&
                (Trusted(sid) || (sid.IsWellKnown(WellKnownSidType.CreatorOwnerSid) &&
                 (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0)))
                continue;
            throw new InvalidOperationException("Private data grants access to an unexpected Windows identity");
        }
    }
}
