using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace CodingAssistant.Api;

// Fixed-only qualification of bounded stdin/stdout in the Windows job.
// This path is not a production grant. In particular, no request can
// select a script, command, archive, generated source or runtime policy.
internal sealed partial class PilotWindowsJob
{
    private static readonly IntPtr HandleListAttribute=new(0x00020002);
    private const uint UseStdHandles=0x100,HandleInherit=1;
    private sealed class PipePair : IDisposable
    {
        internal readonly SafeFileHandle Read,Write;
        internal PipePair(bool childReads)
        {
            var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Inherit=1};
            if(!CreatePipe(out var read,out var write,ref attributes,0))
            {
                read?.Dispose();write?.Dispose();
                throw new StoreFailure("PILOT_JOB_PIPE_CREATE_FAILED");
            }
            Read=read;Write=write;
            if(!SetHandleInformation(childReads?Write:Read,HandleInherit,0))
            {
                Dispose();throw new StoreFailure("PILOT_JOB_PIPE_INHERITANCE_FAILED");
            }
        }
        public void Dispose(){Read.Dispose();Write.Dispose();}
    }
    private SafeFileHandle StartRedirected(string node,string script,SafeFileHandle input,
        SafeFileHandle output,SafeFileHandle error)
    {
        var bytes=IntPtr.Zero;
        _=InitializeProcThreadAttributeList(IntPtr.Zero,2,0,ref bytes);
        if(bytes==IntPtr.Zero||bytes.ToInt64()>65536)throw new StoreFailure("PILOT_JOB_ATTRIBUTE_FAILED");
        var list=Marshal.AllocHGlobal(bytes);
        var jobValue=Marshal.AllocHGlobal(IntPtr.Size);
        var handles=Marshal.AllocHGlobal(3*IntPtr.Size);
        var initialized=false;var addedRef=false;
        try
        {
            if(!InitializeProcThreadAttributeList(list,2,0,ref bytes))
                throw new StoreFailure("PILOT_JOB_ATTRIBUTE_FAILED");
            initialized=true;
            handle.DangerousAddRef(ref addedRef);
            Marshal.WriteIntPtr(jobValue,handle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles,0,input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles,IntPtr.Size,output.DangerousGetHandle());
            Marshal.WriteIntPtr(handles,2*IntPtr.Size,error.DangerousGetHandle());
            if(!UpdateProcThreadAttribute(list,0,JobListAttribute,jobValue,new IntPtr(IntPtr.Size),IntPtr.Zero,IntPtr.Zero)||
                !UpdateProcThreadAttribute(list,0,HandleListAttribute,handles,new IntPtr(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero))
                throw new StoreFailure("PILOT_JOB_ATTRIBUTE_FAILED");
            var startup=new StartupInfoEx{Startup=new StartupInfo{Size=Marshal.SizeOf<StartupInfoEx>(),Flags=UseStdHandles,
                StdInput=input.DangerousGetHandle(),StdOutput=output.DangerousGetHandle(),StdError=error.DangerousGetHandle()},Attributes=list};
            var command=new StringBuilder($"\"{node}\" \"{script}\" --stdin-fixed");
            if(!CreateProcessW(node,command,IntPtr.Zero,IntPtr.Zero,true,ExtendedStartup|CreateNoWindow,
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
            Marshal.FreeHGlobal(handles);Marshal.FreeHGlobal(jobValue);Marshal.FreeHGlobal(list);
            if(addedRef)handle.DangerousRelease();
        }
    }
    private static byte[] Drain(SafeFileHandle handle,int limit)
    {
        // Anonymous pipes are synchronous. Separate workers drain stdout
        // and stderr while the owner writes input and waits for exit.
        using var stream=new FileStream(handle,FileAccess.Read,4096,isAsync:false);
        using var result=new MemoryStream();
        var buffer=new byte[4096];
        while(true)
        {
            var n=stream.Read(buffer,0,buffer.Length);
            if(n==0)return result.ToArray();
            if(n>limit-result.Length)throw new StoreFailure("PILOT_JOB_OUTPUT_LIMIT");
            result.Write(buffer,0,n);
        }
    }
    internal async Task<(int ExitCode,JsonElement Response)> InvokeFixedIoAsync(
        EvaluationPlanningHostConfiguration config,string bridgeSha256,string request,
        TimeSpan deadline,CancellationToken token)
    {
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        if(deadline<=TimeSpan.Zero||deadline>TimeSpan.FromMinutes(5))
            throw new StoreFailure("PILOT_JOB_DEADLINE_INVALID");
        var payload=new UTF8Encoding(false,true).GetBytes(request);
        if(payload.Length is <1 or >4096)throw new StoreFailure("PILOT_JOB_REQUEST_LIMIT");
        token.ThrowIfCancellationRequested();
        if(Members().Length!=0)throw new StoreFailure("PILOT_JOB_NOT_EMPTY");
        var script=Path.Combine(config.RepositoryRoot,"tools","m4","windows_job_fixed_io.mjs");
        void Pins()
        {
            PilotQualificationInspector.RequirePinnedFiles(config.RepositoryRoot,config.ModulePins);
            EvaluationPlanBinder.RequirePinnedFile(Path.Combine(config.RepositoryRoot,PilotQualificationInspector.ScriptPath),config.BinderSha256);
            EvaluationPlanBinder.RequirePinnedFile(script,bridgeSha256);
            EvaluationPlanBinder.RequirePinnedFile(config.NodeExecutable,config.NodeSha256);
            EvaluationPlanBinder.RequirePinnedFile(config.DockerExecutable,config.DockerSha256);
        }
        Pins();
        if(!string.Equals(Path.GetFileName(config.NodeExecutable),"node.exe",StringComparison.OrdinalIgnoreCase)||
            !string.Equals(Path.GetFileName(config.DockerExecutable),"docker.exe",StringComparison.OrdinalIgnoreCase)||
            config.NodeExecutable.Contains('"')||script.Contains('"')||
            Environment.GetEnvironmentVariable("CA_PILOT_NODE_SHA256")!=config.NodeSha256||
            Environment.GetEnvironmentVariable("CA_PILOT_DOCKER_SHA256")!=config.DockerSha256||
            !string.Equals(Environment.GetEnvironmentVariable("CA_PILOT_DOCKER_EXE"),config.DockerExecutable,StringComparison.OrdinalIgnoreCase)||
            Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(k=>
                (k.StartsWith("NODE_",StringComparison.OrdinalIgnoreCase)||
                 k.Equals("OPENSSL_CONF",StringComparison.OrdinalIgnoreCase)||k.Equals("OPENSSL_MODULES",StringComparison.OrdinalIgnoreCase)||
                 k.Equals("DOCKER_HOST",StringComparison.OrdinalIgnoreCase)||k.Equals("DOCKER_CONTEXT",StringComparison.OrdinalIgnoreCase))&&
                !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(k))))
            throw new StoreFailure("PILOT_JOB_ENVIRONMENT_INVALID");
        using var input=new PipePair(true);using var output=new PipePair(false);using var error=new PipePair(false);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(deadline);
        SafeFileHandle? child=null;
        Task? all=null;
        try
        {
            var started=StartRedirected(config.NodeExecutable,script,input.Read,output.Write,error.Write);
            child=started;
            input.Read.Dispose();output.Write.Dispose();error.Write.Dispose();
            var stdout=Task.Run(()=>Drain(output.Read,65536));
            var stderr=Task.Run(()=>Drain(error.Read,8192));
            var write=Task.Run(()=>{
                using var stream=new FileStream(input.Write,FileAccess.Write,4096,isAsync:false);
                stream.Write(payload);
            });
            var exit=Task.Run(()=>WaitExit(started,deadline));
            all=Task.WhenAll(stdout,stderr,write,exit);
            // Fail promptly on overflow/IO failure rather than waiting for
            // the remaining pipe workers to complete against a blocked child.
            var pending=new List<Task>{stdout,stderr,write,exit};
            while(pending.Count!=0)
            {
                var next=await Task.WhenAny(pending).WaitAsync(timeout.Token);
                await next;pending.Remove(next);
            }
            await all;
            timeout.Token.ThrowIfCancellationRequested();
            ConfirmEmpty();Pins();
            timeout.Token.ThrowIfCancellationRequested();
            if((await stderr).Length!=0)throw new StoreFailure("PILOT_JOB_STDERR_UNEXPECTED");
            using var document=JsonDocument.Parse(await stdout,new JsonDocumentOptions{MaxDepth=32});
            if(document.RootElement.ValueKind!=JsonValueKind.Object)throw new StoreFailure("PILOT_JOB_RESPONSE_INVALID");
            return (await exit,document.RootElement.Clone());
        }
        catch
        {
            // A killed job is not container cleanup. Leave its journal
            // untouched; no result can be accepted on an interrupted path.
            TerminateAndConfirm();
            if(all is not null)
            {
                try{await all.WaitAsync(TimeSpan.FromSeconds(15));}
                catch(TimeoutException){throw new StoreFailure("PILOT_JOB_IO_UNCONFIRMED");}
                catch{ /* already blocked; all workers terminated */ }
            }
            throw;
        }
        finally{child?.Dispose();}
    }

#pragma warning disable CS0649
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int Inherit;
    }
#pragma warning restore CS0649
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref SecurityAttributes attributes,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
}
