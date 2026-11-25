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

    internal static void ValidateFile(string path)
    {
        var info = new FileInfo(path);
        if (info.LinkTarget is not null ||
            (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0))
            throw new InvalidOperationException("Refusing redirected private data file");
        if (!info.Exists)
            return;
        if (OperatingSystem.IsWindows())
            ValidateWindows(info.GetAccessControl(AccessControlSections.Access |
                AccessControlSections.Owner));
        else if ((File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead |
                     UnixFileMode.UserWrite)) != 0)
            throw new InvalidOperationException("Data file must be owner-private (0600)");
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
