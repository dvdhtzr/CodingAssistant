using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// Internal bridge: no HTTP route or model tool exposes this seam. It
// invokes only the reviewed non-executing Node planning binder. The owner
// must still independently review semantic test coverage. No runtime grant.
public sealed class EvaluationPlanBinder(TaskStore tasks,string? repositoryRoot=null)
{
    private static readonly Regex Receipt=new("^windows-static-web-[0-9a-f]{12}\\.json\\z",
        RegexOptions.CultureInvariant);
    private static string? Locate(string? root)
    {
        foreach(var start in new[]{root,Directory.GetCurrentDirectory(),AppContext.BaseDirectory})
        {
            if(string.IsNullOrWhiteSpace(start))continue;
            DirectoryInfo? current=new(Path.GetFullPath(start));
            for(var i=0;i<8 && current is not null;i++,current=current.Parent)
            {
                var script=Path.Combine(current.FullName,"tools","m4","static_web_evaluation_host_bridge.mjs");
                if(File.Exists(script)&&(File.GetAttributes(script)&FileAttributes.ReparsePoint)==0)
                    return script;
            }
        }
        return null;
    }

    // Count decoded characters while reading; never buffer an unbounded stream.
    // The overflow callback cancels the sibling pipe operations and exit wait.
    internal static async Task<string> ReadBoundedAsync(TextReader reader,int limit,
        Action overflow,CancellationToken token)
    {
        var result=new StringBuilder(Math.Min(limit,4096));
        var buffer=new char[4096];
        while(true)
        {
            token.ThrowIfCancellationRequested();
            var count=await reader.ReadAsync(buffer.AsMemory(),token);
            if(count==0)return result.ToString();
            if(count>limit-result.Length)
            {
                overflow();
                throw new StoreFailure("EVALUATION_BINDER_RESPONSE_INVALID");
            }
            result.Append(buffer,0,count);
        }
    }

    private static async Task WriteRequestAsync(Process process,string payload,CancellationToken token)
    {
        await process.StandardInput.WriteAsync(payload.AsMemory(),token);
        await process.StandardInput.FlushAsync(token);
        process.StandardInput.Close();
    }

    public async Task<EvaluationPlanReview> StageAsync(string taskId,string runId,
        string receiptName,JsonElement reviewedContract,CancellationToken token)
    {
        if(receiptName is null||!Receipt.IsMatch(receiptName))
            throw new StoreFailure("EVALUATION_RECEIPT_NAME_INVALID");
        if(!OperatingSystem.IsWindows())throw new StoreFailure("EVALUATION_LOCAL_WINDOWS_ONLY");
        if(reviewedContract.ValueKind!=JsonValueKind.Object||reviewedContract.GetRawText().Length>160000)
            throw new StoreFailure("EVALUATION_CONTRACT_NOT_CURRENT");
        var store=new EvaluationPlanReviewStore(tasks);
        var input=store.Capture(taskId,runId);
        var payload=JsonSerializer.Serialize(new {
            operation="prepare-admission",receiptName,currentScope=input.Scope,
            criteria=input.Criteria,reviewedContract,archiveBase64=Convert.ToBase64String(input.Archive)
        },new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if(System.Text.Encoding.UTF8.GetByteCount(payload)>3*1024*1024)
            throw new StoreFailure("EVALUATION_HOST_REQUEST_TOO_LARGE");
        var script=Locate(repositoryRoot)??throw new StoreFailure("EVALUATION_BINDER_NOT_FOUND");
        var start=new ProcessStartInfo("node"){
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new System.Text.UTF8Encoding(false),
            StandardOutputEncoding=System.Text.Encoding.UTF8
        };
        start.ArgumentList.Add(script);start.ArgumentList.Add("--stdin");
        using var process=new Process{StartInfo=start};
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        var outputOverflow=0;
        void Overflow()
        {
            Interlocked.Exchange(ref outputOverflow,1);
            timeout.Cancel();
        }
        try
        {
            token.ThrowIfCancellationRequested();
            if(!process.Start())throw new StoreFailure("EVALUATION_BINDER_UNAVAILABLE");
            var stdout=ReadBoundedAsync(process.StandardOutput,90000,Overflow,timeout.Token);
            var stderr=ReadBoundedAsync(process.StandardError,8192,Overflow,timeout.Token);
            var write=WriteRequestAsync(process,payload,timeout.Token);
            var exit=process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(stdout,stderr,write,exit);
            var result=await stdout;
            if(result.Length<2)throw new StoreFailure("EVALUATION_BINDER_RESPONSE_INVALID");
            using var document=JsonDocument.Parse(result);
            var descriptor=document.RootElement;
            if(descriptor.ValueKind!=JsonValueKind.Object)
                throw new StoreFailure("EVALUATION_BINDER_RESPONSE_INVALID");
            if(process.ExitCode!=0)
            {
                var reason=descriptor.TryGetProperty("reason",out var code)&&code.ValueKind==JsonValueKind.String?
                    code.GetString():null;
                throw new StoreFailure(reason is not null && Regex.IsMatch(reason,"^[A-Z_]{3,90}\\z")?
                    reason:"EVALUATION_BINDER_BLOCKED");
            }
            token.ThrowIfCancellationRequested();
            // Stage re-captures source/plan/criteria/archive inside its own
            // transaction. A changed snapshot while Node ran is rejected.
            return store.Stage(taskId,runId,descriptor);
        }
        catch(OperationCanceledException) when(!token.IsCancellationRequested)
        {throw new StoreFailure(Volatile.Read(ref outputOverflow)!=0?
            "EVALUATION_BINDER_RESPONSE_INVALID":"EVALUATION_BINDER_TIMEOUT");}
        catch(IOException)
        {throw new StoreFailure(Volatile.Read(ref outputOverflow)!=0?
            "EVALUATION_BINDER_RESPONSE_INVALID":"EVALUATION_BINDER_UNAVAILABLE");}
        catch(JsonException){throw new StoreFailure("EVALUATION_BINDER_RESPONSE_INVALID");}
        catch(System.ComponentModel.Win32Exception){throw new StoreFailure("EVALUATION_BINDER_UNAVAILABLE");}
        finally
        {
            try{if(!process.HasExited)process.Kill(entireProcessTree:true);}
            catch(InvalidOperationException){}
            catch(System.ComponentModel.Win32Exception){}
        }
    }
}
