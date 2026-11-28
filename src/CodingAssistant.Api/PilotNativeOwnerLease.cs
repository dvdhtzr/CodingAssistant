namespace CodingAssistant.Api;

// Shared by the future dispatch owner and cleanup orchestrator. Keep the
// file handle for the entire operation, including confirmation persistence.
// Windows closes the handle on process death; the lock file is never deleted.
// No automatic takeover of a live owner. No claim of protection from a
// malicious same-account process or administrator.
internal sealed class PilotNativeOwnerLease : IDisposable
{
    private readonly FileStream stream;
    private PilotNativeOwnerLease(FileStream stream)=>this.stream=stream;
    internal static PilotNativeOwnerLease Acquire(string ownerDataDirectory)
    {
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        if(!Path.IsPathFullyQualified(ownerDataDirectory))
            throw new StoreFailure("PILOT_NATIVE_OWNER_DIRECTORY_INVALID");
        var root=new DirectoryInfo(Path.GetFullPath(ownerDataDirectory));
        if(!root.Exists)throw new StoreFailure("PILOT_NATIVE_OWNER_DIRECTORY_INVALID");
        for(DirectoryInfo? parent=root;parent is not null;parent=parent.Parent)
            if((parent.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new StoreFailure("PILOT_NATIVE_OWNER_DIRECTORY_INVALID");
        PrivateDataDirectory.Ensure(root.FullName);
        var path=Path.Combine(root.FullName,"pilot-native-owner.lock");
        FileStream? opened=null;
        try
        {
            PrivateDataDirectory.ValidateFile(path);
            opened=new FileStream(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            // The containing owner-private directory constrains creation.
            // Do not reopen this exclusively held file for ACL inspection.
            if((File.GetAttributes(path)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)
                throw new StoreFailure("PILOT_NATIVE_OWNER_DIRECTORY_INVALID");
            return new(opened);
        }
        catch(IOException)
        {
            opened?.Dispose();
            throw new StoreFailure("PILOT_NATIVE_OWNER_LEASE_UNAVAILABLE");
        }
        catch
        {
            opened?.Dispose();
            throw;
        }
    }
    public void Dispose()=>stream.Dispose();
}
