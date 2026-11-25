using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace CodingAssistant.Api;

/// <summary>
/// Read metadata and owner/DACL from the same non-following Windows file
/// handle. Expected delete-pending/sharing races use Win32 status codes,
/// not first-chance managed exceptions that interrupt Visual Studio.
/// No permission is modified and no data access is requested.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class WindowsSidecarSecurity
{
    private const uint ReadControlAndAttributes = 0x00020080;
    private const uint ShareReadWriteDelete = 7;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint OwnerAndDacl = 5;
    private const int FileAttributeTagInfo = 9;
    private const int SeFileObject = 1;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int ErrorDeletePending = 303;
    private const int MaximumDescriptorBytes = 1024 * 1024;

    internal static void Validate(string path, Action<FileSystemSecurity> validate) =>
        ValidateObserved(() => Observe(path, validate), Thread.Sleep);

    // Internal deterministic test seam; the production path always uses
    // the fixed Windows observer and bounded delays above.
    internal static void ValidateObserved(Func<int> observe, Action<int> delay)
    {
        // Never interpret access denied as absent. A retry must either
        // observe actual absence or complete the original security check.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var status = observe();
            if (status is 0 or ErrorFileNotFound)
                return;
            if (attempt == 4 || status is not (ErrorAccessDenied or
                    ErrorSharingViolation or ErrorLockViolation or ErrorDeletePending))
                throw new InvalidOperationException("Private SQLite sidecar could not be validated");
            delay(5 << attempt);
        }
    }

    private static int Observe(string path, Action<FileSystemSecurity> validate)
    {
        using var handle = CreateFile(path, ReadControlAndAttributes,
            ShareReadWriteDelete, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            return error == 0 ? -1 : error;
        }
        if (!GetFileInformationByHandleEx(handle, FileAttributeTagInfo,
                out var info, (uint)Marshal.SizeOf<AttributeTagInfo>()))
            return MetadataFailure(Marshal.GetLastPInvokeError());
        if ((info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidOperationException("Refusing redirected private data file");

        // GetSecurityInfo returns a Win32 status rather than throwing.
        // The descriptor is allocated by Windows and freed with LocalFree.
        var status = GetSecurityInfo(handle, SeFileObject, OwnerAndDacl,
            out _, out _, out _, out _, out var descriptor);
        try
        {
            if (status != 0)
                return MetadataFailure((int)status);
            if (descriptor == IntPtr.Zero)
                throw new InvalidOperationException("Missing private file security descriptor");
            var length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > MaximumDescriptorBytes)
                throw new InvalidOperationException("Invalid private file security descriptor");
            var bytes = new byte[(int)length];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var raw = new RawSecurityDescriptor(bytes, 0);
            if (raw.DiscretionaryAcl is null ||
                (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
                throw new InvalidOperationException("Private file has no discretionary ACL");
            var security = new FileSecurity();
            security.SetSecurityDescriptorBinaryForm(bytes,
                AccessControlSections.Owner | AccessControlSections.Access);
            validate(security);
            return 0;
        }
        finally
        {
            if (descriptor != IntPtr.Zero)
                _ = LocalFree(descriptor);
        }
    }

    // Only CreateFile can establish name absence; failure on an already
    // opened handle is never an absence observation.
    private static int MetadataFailure(int status) =>
        status is 0 or ErrorFileNotFound ? -1 : status;

    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInfo
    {
        public FileAttributes Attributes;
        public uint ReparseTag;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access,
        uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle,
        int infoClass, out AttributeTagInfo info, uint size);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityInfo(SafeFileHandle handle, int objectType,
        uint securityInfo, out IntPtr owner, out IntPtr group, out IntPtr dacl,
        out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
