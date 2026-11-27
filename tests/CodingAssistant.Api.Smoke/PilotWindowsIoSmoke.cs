using System.Security.Cryptography;
using System.Text.Json;
using CodingAssistant.Api;

internal static class PilotWindowsIoSmoke
{
    private static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Required(string key)=>Environment.GetEnvironmentVariable(key)??throw new Exception("Missing "+key);
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    internal static async Task<int> Run()
    {
        Check(OperatingSystem.IsWindows(),"Windows required");
        var root=Directory.GetCurrentDirectory();
        var node=Required("CODINGASSISTANT_TEST_NODE_EXE");var docker=Required("CA_PILOT_DOCKER_EXE");
        var pins=PilotQualificationInspector.FileNames.ToDictionary(x=>x,x=>Hash(Path.Combine(root,x)),StringComparer.Ordinal);
        var config=new EvaluationPlanningHostConfiguration(root,node,Required("CA_PILOT_NODE_SHA256"),
            Hash(Path.Combine(root,PilotQualificationInspector.ScriptPath)),pins,docker,Required("CA_PILOT_DOCKER_SHA256"));
        var bridgeHash=Hash(Path.Combine(root,"tools","m4","windows_job_fixed_io.mjs"));
        var active=Path.Combine(Required("LOCALAPPDATA"),"CodingAssistant","pilot-attempts","active");
        Check(!Directory.Exists(active),"Prior cleanup required");
        async Task<(int ExitCode,JsonElement Response)> Invoke(string raw,CancellationToken token=default)
        {
            using var job=PilotWindowsJob.Create(@"Local\CodingAssistant-Pilot-"+Guid.NewGuid().ToString("N"));
            return await job.InvokeFixedIoAsync(config,bridgeHash,raw,TimeSpan.FromMinutes(4),token);
        }
        var rejected=await Invoke("""{"operation":"generated-source","source":"not permitted"}""");
        Check(rejected.ExitCode==2&&rejected.Response.GetProperty("reason").GetString()=="PILOT_JOB_REQUEST_INVALID"&&
            !Directory.Exists(active),"Invalid request must not start a pilot");
        Console.WriteLine("  invalid request: rejected");
        var results=new Dictionary<string,JsonElement>(StringComparer.Ordinal);
        foreach(var (control,status) in new[]{("build-working","CHECKS_PASSED_NOT_VERIFIED"),("build-spoof","TESTS_FAILED"),
            ("build-missing-asset","BUILD_FAILED")})
        {
            var attempt=Guid.NewGuid().ToString("N");
            var response=await Invoke(JsonSerializer.Serialize(new {operation="fixed-control",control,attemptId=attempt}));
            var v=response.Response;
            Check(response.ExitCode==0&&v.GetProperty("attemptId").GetString()==attempt&&v.GetProperty("control").GetString()==control&&
                v.GetProperty("status").GetString()==status&&v.GetProperty("qualificationPassed").GetBoolean()&&
                v.GetProperty("attachClosed").GetBoolean()&&v.GetProperty("containerRemoved").GetBoolean()&&
                !v.GetProperty("cleanupDebt").GetBoolean()&&!v.GetProperty("generatedArtifactVerified").GetBoolean()&&
                !v.GetProperty("executionEnabled").GetBoolean()&&!v.GetProperty("runtimeQualified").GetBoolean()&&!Directory.Exists(active),
                "Job-owned fixed I/O failed: "+control);
            results.Add(control,v);
            Console.WriteLine("  "+control+": "+status+"; job empty; cleanup confirmed");
        }
        Check(results.Values.Select(x=>x.GetProperty("engineId").GetString()).Distinct().Count()==1&&
            results.Values.Select(x=>x.GetProperty("policySha256").GetString()).Distinct().Count()==1,"Host changed");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();
            try
            {
                await Invoke("""{"operation":"fixed-control","control":"build-working","attemptId":"11111111111111111111111111111111"}""",canceled.Token);
                throw new Exception("Pre-cancelled request started");
            }
            catch(OperationCanceledException){Check(!Directory.Exists(active),"Pre-cancel left journal");}
        }
        var evidence=new {schemaVersion=1,stage="AET-8-windows-job-fixed-io",status="PILOT_JOB_FIXED_IO_PASS",
            recordedAtUtc=DateTimeOffset.UtcNow.ToString("O"),bridgeSha256=bridgeHash,nodeSha256=config.NodeSha256,dockerSha256=config.DockerSha256,
            currentModulePins=pins,inspectorSha256=config.BinderSha256,ownerAssemblySha256=Hash(typeof(PilotWindowsJob).Assembly.Location),
            harnessAssemblySha256=Hash(typeof(PilotWindowsIoSmoke).Assembly.Location),controls=results,
            invalidRequestRejected=true,preCancelledRequestRejected=true,jobEmptyConfirmed=true,
            generatedSourceExecuted=false,generatedCodeExecutionEnabled=false,runtimeQualified=false,generatedArtifactVerified=false};
        var folder=Path.Combine(Required("LOCALAPPDATA"),"CodingAssistant","qualifications");
        PrivateDataDirectory.Ensure(folder);
        var file=Path.Combine(folder,"windows-static-web-"+Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6))+".json");
        using(var stream=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None))
        {
            JsonSerializer.Serialize(stream,evidence);stream.Flush(flushToDisk:true);
        }
        PrivateDataDirectory.ValidateFile(file);
        Console.WriteLine("AET-8 WINDOWS_PILOT_JOB_FIXED_IO_PASS");
        Console.WriteLine("Evidence: "+file);
        return 0;
    }
}
