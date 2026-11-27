using System.Diagnostics;
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
        try
        {
            token.ThrowIfCancellationRequested();
            if(!process.Start())throw new StoreFailure("EVALUATION_BINDER_UNAVAILABLE");
            var stdout=process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr=process.StandardError.ReadToEndAsync(timeout.Token);
            await process.StandardInput.WriteAsync(payload.AsMemory(),timeout.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            var result=await stdout;_ = await stderr;
            if(result.Length is <2 or >90000)throw new StoreFailure("EVALUATION_BINDER_RESPONSE_INVALID");
            using var document=JsonDocument.Parse(result);
            var descriptor=document.RootElement;
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
        {throw new StoreFailure("EVALUATION_BINDER_TIMEOUT");}
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
