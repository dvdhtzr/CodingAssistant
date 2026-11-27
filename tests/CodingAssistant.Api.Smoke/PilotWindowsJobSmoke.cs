using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CodingAssistant.Api;

// Opt-in owner-local actual Windows job control. No generated source.
// The outer observer keeps exact process handles, not merely PID names,
// and releases all job query handles before abruptly killing the owner.
internal static class PilotWindowsJobSmoke
{
    private static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Required(string key)=>Environment.GetEnvironmentVariable(key)??throw new Exception("Missing "+key);
    private static string JobName()=>@"Local\CodingAssistant-Pilot-"+Guid.NewGuid().ToString("N");
    private static string EvidenceRoot()=>Path.Combine(Required("LOCALAPPDATA"),"CodingAssistant","qualifications");
    private static HashSet<string> Receipts()=>Directory.Exists(EvidenceRoot())?
        Directory.GetFiles(EvidenceRoot(),"windows-static-web-*.json").ToHashSet(StringComparer.OrdinalIgnoreCase):new(StringComparer.OrdinalIgnoreCase);
    private static (string Name,string Hash,JsonElement Value) NewReceipt(HashSet<string> before,string expectedStatus)
    {
        var added=Receipts().Except(before,StringComparer.OrdinalIgnoreCase).ToArray();
        Check(added.Length==1,"Expected exactly one new fixed-control receipt");
        PrivateDataDirectory.Ensure(EvidenceRoot());PrivateDataDirectory.ValidateFile(added[0]);
        Check(new FileInfo(added[0]).Length is >0 and <=65536,"Invalid receipt size");
        using var json=JsonDocument.Parse(File.ReadAllBytes(added[0]));
        Check(json.RootElement.GetProperty("status").GetString()==expectedStatus,"Fixed receipt status mismatch");
        return (Path.GetFileName(added[0]),Hash(added[0]),json.RootElement.Clone());
    }
    private static string Root()=>Path.Combine(Required("LOCALAPPDATA"),"CodingAssistant","pilot-attempts");
    private static string Script(string name)=>Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(),"tools","m4",name));
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private static void Fixed(PilotWindowsJob job,string script,string argument)
    {
        var node=Required("CODINGASSISTANT_TEST_NODE_EXE");
        Check(Hash(node)==Required("CA_PILOT_NODE_SHA256"),"Node pin changed");
        var docker=Required("CA_PILOT_DOCKER_EXE");
        Check(Hash(docker)==Required("CA_PILOT_DOCKER_SHA256"),"Docker pin changed");
        // Diagnostic current checkout hashes, not production enrollment.
        var pins=PilotQualificationInspector.FileNames.ToDictionary(p=>p,p=>Hash(Path.GetFullPath(p)),StringComparer.Ordinal);
        PilotQualificationInspector.RequirePinnedFiles(Directory.GetCurrentDirectory(),pins);
        using var child=job.StartFixed(node,Hash(node),script,Hash(script),argument);
        var code=PilotWindowsJob.WaitExit(child,TimeSpan.FromMinutes(12));
        job.ConfirmEmpty();
        Check(code==0,"Fixed job child failed: "+code);
        PilotQualificationInspector.RequirePinnedFiles(Directory.GetCurrentDirectory(),pins);
        Check(Hash(node)==Required("CA_PILOT_NODE_SHA256")&&Hash(docker)==Required("CA_PILOT_DOCKER_SHA256"),"Executable pins changed");
    }
    private static (int Owner,string Attempt)? Starting()
    {
        var active=Path.Combine(Root(),"active");
        try
        {
            using var identity=JsonDocument.Parse(File.ReadAllText(Path.Combine(active,"identity.json")));
            var last=File.ReadLines(Path.Combine(active,"events.jsonl")).Last();
            using var stage=JsonDocument.Parse(last);
            if(stage.RootElement.GetProperty("stage").GetString()!="Starting")return null;
            return (identity.RootElement.GetProperty("ownerPid").GetInt32(),
                identity.RootElement.GetProperty("identity").GetProperty("attemptId").GetString()!);
        }
        catch(IOException){return null;}
        catch(JsonException){return null;}
        catch(InvalidOperationException){return null;}
    }
    private static async Task<JsonElement> Observe()
    {
        var node=EvaluationPlanBinder.RequirePinnedFile(Required("CODINGASSISTANT_TEST_NODE_EXE"),Required("CA_PILOT_NODE_SHA256"));
        var script=Script("windows_job_recovery_control.mjs");
        var hash=Hash(script);
        EvaluationPlanBinder.RequirePinnedFile(script,hash);
        var start=new ProcessStartInfo(node){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Directory.GetCurrentDirectory(),
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new System.Text.UTF8Encoding(false),StandardOutputEncoding=System.Text.Encoding.UTF8};
        EvaluationPlanBinder.RemoveNodeEnvironmentOverrides(start);
        start.ArgumentList.Add(script);start.ArgumentList.Add("--observe-fixed");
        var value=await EvaluationPlanBinder.InvokePlanningProcessAsync(start,"",TimeSpan.FromSeconds(20),CancellationToken.None);
        EvaluationPlanBinder.RequirePinnedFile(script,hash);
        EvaluationPlanBinder.RequirePinnedFile(node,Required("CA_PILOT_NODE_SHA256"));
        return value;
    }
    internal static int Owner(string name)
    {
        using var job=PilotWindowsJob.Create(name);
        Fixed(job,Script("static_web_candidate_dispatch.mjs"),"--run-fixed-controls");
        return 0;
    }
    private static Process StartOwner(string name)
    {
        var executable=Environment.ProcessPath??throw new Exception("No current executable");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,
            WorkingDirectory=Directory.GetCurrentDirectory()};
        if(string.Equals(Path.GetFileNameWithoutExtension(executable),"dotnet",StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(PilotWindowsJobSmoke).Assembly.Location);
        start.ArgumentList.Add("--pilot-job-owner");start.ArgumentList.Add(name);
        return Process.Start(start)??throw new Exception("Owner start failed");
    }
    internal static async Task<int> Run()
    {
        Check(OperatingSystem.IsWindows(),"Windows required");
        Check(!Directory.Exists(Path.Combine(Root(),"active")),"Prior cleanup required");
        var diagnosticPins=PilotQualificationInspector.FileNames.ToDictionary(p=>p,p=>Hash(Path.GetFullPath(p)),StringComparer.Ordinal);
        var inspectorHash=Hash(Path.GetFullPath(PilotQualificationInspector.ScriptPath));
        var normalName=JobName();var beforeNormal=Receipts();
        using(var normal=PilotWindowsJob.Create(normalName))
            Fixed(normal,Script("static_web_candidate_dispatch.mjs"),"--run-fixed-controls");
        var normalReceipt=NewReceipt(beforeNormal,"PILOT_CANDIDATE_DISPATCH_CONTROLS_PASS");
        Console.WriteLine("  normal: fixed controls completed; all job descendants exited");
        Check(!Directory.Exists(Path.Combine(Root(),"active")),"Prior cleanup required");
        var crashName=JobName();using var owner=StartOwner(crashName);
        var observed=new List<Process>();string? attempt=null;JsonElement live=default;
        try
        {
            var clock=Stopwatch.StartNew();
            while(clock.Elapsed<TimeSpan.FromSeconds(90)&&!owner.HasExited)
            {
                var pending=Starting();
                if(pending is null){await Task.Delay(100);continue;}
                int[] members;
                try{members=PilotWindowsJob.Inspect(crashName);}
                catch(StoreFailure){await Task.Delay(100);continue;}
                if(!members.Contains(pending.Value.Owner)){await Task.Delay(100);continue;}
                var batch=new List<Process>();
                try
                {
                    foreach(var pid in members)
                    {
                        var process=Process.GetProcessById(pid);batch.Add(process);
                        _=process.Handle; // retain the actual kernel process handle
                    }
                    var nodePath=Path.GetFullPath(Required("CODINGASSISTANT_TEST_NODE_EXE"));
                    var dockerPath=Path.GetFullPath(Required("CA_PILOT_DOCKER_EXE"));
                    bool Image(Process p,string expected)=>string.Equals(p.MainModule?.FileName,expected,StringComparison.OrdinalIgnoreCase);
                    if(!batch.Any(p=>p.Id==pending.Value.Owner&&Image(p,nodePath))||
                        !batch.Any(p=>Image(p,dockerPath))||batch.Any(p=>p.HasExited||!PilotWindowsJob.ConfirmMember(crashName,p)))
                        continue;
                    var running=await Observe();
                    if(!running.GetProperty("running").GetBoolean()||running.GetProperty("attemptId").GetString()!=pending.Value.Attempt||
                        running.GetProperty("ownerPid").GetInt32()!=pending.Value.Owner)
                        continue;
                    // Recheck the exact journal and member set after opening
                    // handles. Inspect's query handle is already disposed.
                    if(Starting()!=pending||!PilotWindowsJob.Inspect(crashName).Order().SequenceEqual(members.Order()))
                        continue;
                    observed.AddRange(batch);batch.Clear();attempt=pending.Value.Attempt;live=running;break;
                }
                catch(ArgumentException){}
                catch(InvalidOperationException){}
                catch(System.ComponentModel.Win32Exception){}
                finally{foreach(var p in batch)p.Dispose();}
                await Task.Delay(100);
            }
            Check(attempt is not null&&observed.Count>=2&&!owner.HasExited,"Actual Node and Docker job members not observed");
            // Do NOT use Kill(entireProcessTree:true). The owner alone
            // dies, and the kernel job must terminate the descendants.
            owner.Kill();
            using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                await owner.WaitForExitAsync(deadline.Token);
                await Task.WhenAll(observed.Select(p=>p.WaitForExitAsync(deadline.Token)));
            }
            Check(owner.ExitCode!=0&&observed.All(p=>p.HasExited),"Job descendant exit unconfirmed");
            Console.WriteLine("  crash: owner and "+observed.Count+" observed job processes exited");
            Check(Starting()?.Attempt==attempt,"Interrupted exact attempt not retained");
            var beforeRecovery=Receipts();
            using(var recovery=PilotWindowsJob.Create(JobName()))
                Fixed(recovery,Script("windows_job_recovery_control.mjs"),"--recover-fixed");
            var recoveryReceipt=NewReceipt(beforeRecovery,"PILOT_JOB_FIXED_RECOVERY_PASS");
            Check(recoveryReceipt.Value.GetProperty("attemptId").GetString()==attempt&&
                recoveryReceipt.Value.GetProperty("containerRemoved").GetBoolean()&&
                !recoveryReceipt.Value.GetProperty("cleanupDebt").GetBoolean(),"Wrong recovery receipt");
            Check(!Directory.Exists(Path.Combine(Root(),"active")),"Recovery left active journal");
            PilotQualificationInspector.RequirePinnedFiles(Directory.GetCurrentDirectory(),diagnosticPins);
            EvaluationPlanBinder.RequirePinnedFile(Path.GetFullPath(PilotQualificationInspector.ScriptPath),inspectorHash);
            Check(Hash(Required("CODINGASSISTANT_TEST_NODE_EXE"))==Required("CA_PILOT_NODE_SHA256")&&
                Hash(Required("CA_PILOT_DOCKER_EXE"))==Required("CA_PILOT_DOCKER_SHA256"),"Executable pins changed");
            var evidence=new {schemaVersion=1,stage="AET-8-windows-job-ownership",status="PILOT_JOB_OWNERSHIP_PASS",
                recordedAtUtc=DateTimeOffset.UtcNow.ToString("O"),jobListAtomic=true,killOnClose=true,
                ownerExitConfirmed=true,observedProcessCount=observed.Count,observedProcessesExited=true,
                runningContainer=live,normalReceiptName=normalReceipt.Name,normalReceiptSha256=normalReceipt.Hash,
                recoveryReceiptName=recoveryReceipt.Name,recoveryReceiptSha256=recoveryReceipt.Hash,
                nodeSha256=Required("CA_PILOT_NODE_SHA256"),dockerSha256=Required("CA_PILOT_DOCKER_SHA256"),
                ownerAssemblySha256=Hash(typeof(PilotWindowsJob).Assembly.Location),
                harnessAssemblySha256=Hash(typeof(PilotWindowsJobSmoke).Assembly.Location),
                currentModulePins=diagnosticPins,inspectorSha256=inspectorHash,containerRemoved=true,cleanupDebt=false,
                generatedSourceExecuted=false,generatedCodeExecutionEnabled=false,runtimeQualified=false,generatedArtifactVerified=false};
            var evidencePath=Path.Combine(EvidenceRoot(),"windows-static-web-"+Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))+".json");
            using(var stream=new FileStream(evidencePath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                JsonSerializer.Serialize(stream,evidence);stream.Flush(flushToDisk:true);
            }
            PrivateDataDirectory.ValidateFile(evidencePath);
            Console.WriteLine("  recovery: exact container removed; all recovery processes exited");
            Console.WriteLine("AET-8 WINDOWS_PILOT_JOB_OWNERSHIP_PASS");
            Console.WriteLine("Evidence: "+evidencePath);
            Console.WriteLine("  generated source: NOT_AUTHORIZED; Static Web runtime: UNQUALIFIED");
            return 0;
        }
        finally
        {
            // Never silently mark a failed qualification as a clean one.
            if(!owner.HasExited){owner.Kill();owner.WaitForExit(15000);}
            foreach(var p in observed)p.Dispose();
        }
    }
}
