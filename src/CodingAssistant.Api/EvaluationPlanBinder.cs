using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// Internal bridge: no HTTP route or model tool exposes this seam. It
// invokes only the reviewed non-executing Node planning binder. The owner
// must still independently review semantic test coverage. No runtime grant.
public sealed class EvaluationPlanBinder(TaskStore tasks,string? repositoryRoot=null,
    string? nodeExecutable=null,string? nodeSha256=null,string? binderSha256=null)
{
    private static readonly Regex Receipt=new("^windows-static-web-[0-9a-f]{12}\\.json\\z",
        RegexOptions.CultureInvariant);
    // These inputs must come from trusted host configuration, never a model,
    // request, project directory, or digest calculated from an unreviewed file.
    // File identity checks narrow the launch surface; they are not OS attestation
    // or a proof against concurrent modification by the same host account.
    internal static string RequirePinnedFile(string? file,string? digest)
    {
        if(string.IsNullOrWhiteSpace(file)||!Path.IsPathFullyQualified(file)||
            digest is null||!Regex.IsMatch(digest,"^[0-9a-f]{64}\\z",RegexOptions.CultureInvariant))
            throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_REQUIRED");
        try
        {
            var full=Path.GetFullPath(file);
            if(OperatingSystem.IsWindows() &&
                !Regex.IsMatch(Path.GetPathRoot(full)??"","^[A-Za-z]:\\\\\\z",RegexOptions.CultureInvariant))
                throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");
            var attributes=File.GetAttributes(full);
            if((attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)
                throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");
            for(var parent=Directory.GetParent(full);parent is not null;parent=parent.Parent)
                if((parent.Attributes&FileAttributes.ReparsePoint)!=0)
                    throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");
            using var stream=File.OpenRead(full);
            var actual=Convert.ToHexStringLower(SHA256.HashData(stream));
            if(!string.Equals(actual,digest,StringComparison.Ordinal))
                throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");
            return full;
        }
        catch(IOException){throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");}
        catch(UnauthorizedAccessException){throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");}
        catch(ArgumentException){throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");}
        catch(NotSupportedException){throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");}
    }

    internal static void RemoveNodeEnvironmentOverrides(ProcessStartInfo start)
    {
        foreach(var key in start.Environment.Keys.ToArray())
            if(key.StartsWith("NODE_",StringComparison.OrdinalIgnoreCase)||
                key.Equals("OPENSSL_CONF",StringComparison.OrdinalIgnoreCase)||
                key.Equals("OPENSSL_MODULES",StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(key);
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
        if(string.IsNullOrWhiteSpace(repositoryRoot)||!Path.IsPathFullyQualified(repositoryRoot))
            throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_REQUIRED");
        var script=RequirePinnedFile(Path.Combine(repositoryRoot,"tools","m4",
            "static_web_evaluation_host_bridge.mjs"),binderSha256);
        var executable=RequirePinnedFile(nodeExecutable,nodeSha256);
        if(!string.Equals(Path.GetFileName(executable),"node.exe",StringComparison.OrdinalIgnoreCase))
            throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");
        var start=new ProcessStartInfo(executable){
            WorkingDirectory=Path.GetDirectoryName(script)!,
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new System.Text.UTF8Encoding(false),
            StandardOutputEncoding=System.Text.Encoding.UTF8
        };
        RemoveNodeEnvironmentOverrides(start);
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
