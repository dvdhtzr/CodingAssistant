using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodingAssistant.Api;

// Host-only Windows process ownership. The process is born in the job
// via STARTUPINFOEX JOB_LIST, rather than launched then assigned. The
// handle is non-inheritable, no breakaway flags, and kill-on-close is
// mandatory. No shell, PATH resolution or user-facing process interface.
internal sealed class PilotWindowsJob : IDisposable
{
    private const uint KillOnClose=0x2000;
    private const uint ExtendedStartup=0x80000;
    private const uint CreateNoWindow=0x08000000;
    private const int ExtendedLimits=9,ProcessIdList=3;
    private const uint JobQuery=4;
    private static readonly IntPtr JobListAttribute=new(0x0002000D);
    private readonly SafeFileHandle handle;
    private PilotWindowsJob(SafeFileHandle handle)=>this.handle=handle;
    internal static PilotWindowsJob Create(string name)
    {
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        CheckName(name);
        var h=CreateJobObjectW(IntPtr.Zero,name);
        var lastError=Marshal.GetLastWin32Error();
        if(h.IsInvalid||lastError==183){h.Dispose();throw new StoreFailure("PILOT_JOB_CREATE_FAILED");}
        try
        {
            var limits=new ExtendedLimitInformation{Basic=new BasicLimitInformation{LimitFlags=KillOnClose}};
            if(!SetInformationJobObject(h,ExtendedLimits,ref limits,(uint)Marshal.SizeOf<ExtendedLimitInformation>()))
                throw new StoreFailure("PILOT_JOB_LIMITS_FAILED");
            return new(h);
        }
        catch{h.Dispose();throw;}
    }
    private static void CheckName(string name)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(name,@"^Local\\CodingAssistant-Pilot-[a-f0-9]{32}\z"))
            throw new StoreFailure("PILOT_JOB_NAME_INVALID");
    }
    // Exact diagnostic query only. The observer MUST close its query
    // handle before terminating the owner, since any handle keeps a job alive.
    internal static int[] Inspect(string name)
    {
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        CheckName(name);
        using var h=OpenJobObjectW(JobQuery,false,name);
        if(h.IsInvalid)throw new StoreFailure("PILOT_JOB_QUERY_FAILED");
        return ReadMembers(h);
    }
    internal static bool ConfirmMember(string name,Process process)
    {
        CheckName(name);
        using var h=OpenJobObjectW(JobQuery,false,name);
        return !h.IsInvalid&&IsProcessInJobRaw(process.Handle,h,out var member)&&member;
    }
    internal static bool Exists(string name)
    {
        CheckName(name);
        using var h=OpenJobObjectW(JobQuery,false,name);
        if(!h.IsInvalid)return true;
        if(Marshal.GetLastWin32Error()!=2)throw new StoreFailure("PILOT_JOB_QUERY_FAILED");
        return false;
    }
    internal int[] Members()=>ReadMembers(handle);
    private static int[] ReadMembers(SafeFileHandle h)
    {
        const int max=128;
        var size=8+max*IntPtr.Size;
        var memory=Marshal.AllocHGlobal(size);
        try
        {
            if(!QueryInformationJobObject(h,ProcessIdList,memory,(uint)size,out _))
                throw new StoreFailure("PILOT_JOB_QUERY_FAILED");
            var assigned=Marshal.ReadInt32(memory,0);var count=Marshal.ReadInt32(memory,4);
            if(count<0||count>max||assigned!=count)throw new StoreFailure("PILOT_JOB_MEMBERS_UNCERTAIN");
            return Enumerable.Range(0,count).Select(i=>checked((int)Marshal.ReadIntPtr(memory,8+i*IntPtr.Size).ToInt64())).ToArray();
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    // Only the fixed shared-dispatch entrypoint or cleanup-only entrypoint
    // is permitted here. Production generated execution is not connected.
    internal SafeFileHandle StartFixed(string node,string nodeHash,string script,string scriptHash,string argument)
    {
        node=EvaluationPlanBinder.RequirePinnedFile(node,nodeHash);
        script=EvaluationPlanBinder.RequirePinnedFile(script,scriptHash);
        var file=Path.GetFileName(script);
        if(!string.Equals(Path.GetFileName(node),"node.exe",StringComparison.OrdinalIgnoreCase)||
            !((file=="static_web_candidate_dispatch.mjs"&&(argument is "--run-fixed-controls" or "--run-job-crash-control"))||
              (file=="windows_job_recovery_control.mjs"&&argument=="--recover-fixed"))||
            node.Contains('"')||script.Contains('"'))
            throw new StoreFailure("PILOT_JOB_FIXED_CONTROL_REQUIRED");
        if(Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k=>
            (k.StartsWith("NODE_",StringComparison.OrdinalIgnoreCase)||
             k.Equals("OPENSSL_CONF",StringComparison.OrdinalIgnoreCase)||k.Equals("OPENSSL_MODULES",StringComparison.OrdinalIgnoreCase))&&
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k))))
            throw new StoreFailure("PILOT_DISPATCH_ENVIRONMENT_OVERRIDE");
        var bytes=IntPtr.Zero;
        _=InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref bytes);
        if(bytes==IntPtr.Zero||bytes.ToInt64()>65536)throw new StoreFailure("PILOT_JOB_ATTRIBUTE_FAILED");
        var list=Marshal.AllocHGlobal(bytes);
        var value=Marshal.AllocHGlobal(IntPtr.Size);
        var initialized=false;
        var addedRef=false;
        try
        {
            if(!InitializeProcThreadAttributeList(list,1,0,ref bytes))throw new StoreFailure("PILOT_JOB_ATTRIBUTE_FAILED");
            initialized=true;
            handle.DangerousAddRef(ref addedRef);
            Marshal.WriteIntPtr(value,handle.DangerousGetHandle());
            if(!UpdateProcThreadAttribute(list,0,JobListAttribute,value,new IntPtr(IntPtr.Size),IntPtr.Zero,IntPtr.Zero))
                throw new StoreFailure("PILOT_JOB_ATTRIBUTE_FAILED");
            var startup=new StartupInfoEx{Startup=new StartupInfo{Size=Marshal.SizeOf<StartupInfoEx>()},Attributes=list};
            // No inherited handles, no shell, and no untrusted arguments.
            // The fixed control writes its bounded private evidence file.
            var command=new StringBuilder($"\"{node}\" \"{script}\" {argument}");
            if(!CreateProcessW(node,command,IntPtr.Zero,IntPtr.Zero,false,ExtendedStartup|CreateNoWindow,
                IntPtr.Zero,Path.GetDirectoryName(script),ref startup,out var process))
                throw new StoreFailure("PILOT_JOB_PROCESS_CREATE_FAILED");
            using var thread=new SafeFileHandle(process.Thread,true);
            var child=new SafeFileHandle(process.Process,true);
            try
            {
                if(!IsProcessInJob(child,handle,out var inJob)||!inJob)
                    throw new StoreFailure("PILOT_JOB_MEMBERSHIP_UNCONFIRMED");
                return child;
            }
            catch{child.Dispose();throw;}
        }
        finally
        {
            if(initialized)DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(value);Marshal.FreeHGlobal(list);
            if(addedRef)handle.DangerousRelease();
        }
    }
    internal static int WaitExit(SafeFileHandle process,TimeSpan timeout)
    {
        var wait=WaitForSingleObject(process,checked((uint)timeout.TotalMilliseconds));
        if(wait!=0||!GetExitCodeProcess(process,out var code))throw new StoreFailure("PILOT_JOB_PROCESS_EXIT_UNCONFIRMED");
        return unchecked((int)code);
    }
    internal void TerminateAndConfirm()
    {
        if(!TerminateJobObject(handle,2))throw new StoreFailure("PILOT_JOB_TERMINATION_FAILED");
        ConfirmEmpty();
    }
    internal void ConfirmEmpty()
    {
        var watch=Stopwatch.StartNew();
        while(watch.Elapsed<TimeSpan.FromSeconds(15))
        {
            if(Members().Length==0)return;
            Thread.Sleep(100);
        }
        throw new StoreFailure("PILOT_JOB_DESCENDANTS_REMAIN");
    }
    public void Dispose()=>handle.Dispose();

#pragma warning disable CS0649 // Native ABI fields populated by Windows or zero-initialized.
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTime,PerJobUserTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet,MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass,SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved,Desktop,Title;
        public uint X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;
        public ushort ShowWindow,Reserved2Length;
        public IntPtr Reserved2,StdInput,StdOutput,StdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo Startup;
        public IntPtr Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process,Thread;
        public uint ProcessId,ThreadId;
    }
#pragma warning restore CS0649
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes,string name);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    private static extern SafeFileHandle OpenJobObjectW(uint access,[MarshalAs(UnmanagedType.Bool)]bool inherit,string name);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref ExtendedLimitInformation value,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job,int kind,IntPtr value,uint size,out uint returned);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,uint flags,ref IntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,IntPtr environment,string? directory,ref StartupInfoEx startup,out ProcessInformation process);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeFileHandle process,SafeFileHandle job,[MarshalAs(UnmanagedType.Bool)]out bool result);
    [DllImport("kernel32.dll",EntryPoint="IsProcessInJob",ExactSpelling=true,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJobRaw(IntPtr process,SafeFileHandle job,[MarshalAs(UnmanagedType.Bool)]out bool result);
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern uint WaitForSingleObject(SafeFileHandle process,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeFileHandle process,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job,uint code);
}
