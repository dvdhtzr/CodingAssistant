using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// Read-only host inspection. Current file pins are not retrospective
// attestation of old qualification runs. No grant or runtime promotion.
internal sealed class PilotQualificationInspector(EvaluationPlanningHostConfiguration configuration)
{
    internal const string ScriptPath="tools/m4/static_web_pilot_evidence.mjs";
    private static readonly string[] RequiredFiles=[
        "tools/m4/calculation_journal_contract.mjs",
        "tools/m4/smoke_generated_zip.mjs",
        "tools/m4/generated_zip_smoke_guest.mjs",
        "tools/m4/static_web_evaluation_admission.mjs",
        "tools/m4/static_web_evaluation_candidate.mjs",
        "tools/m4/static_web_evaluation_host_bridge.mjs",
        "tools/m4/windows_aet8_trusted_guest.mjs",
        "tools/m4/windows_aet8_trusted_qualification.mjs",
        "tools/m4/windows_aet8_pipe_driver.mjs",
        "tools/m4/windows_calculator_evaluator_guest.mjs",
        "tools/m4/windows_calculator_evaluator_qualification.mjs",
        "tools/m4/windows_calculator_harness.js",
        "tools/m4/windows_chromium_sandbox_guest.mjs",
        "tools/m4/windows_chromium_sandbox_qualification.mjs",
        "tools/m4/windows_docker_guest.mjs",
        "tools/m4/windows_docker_qualification.mjs",
        "tools/m4/static_web_pilot_policy.mjs",
        "tools/m4/static_web_pilot_attempt_journal.mjs",
        "tools/m4/windows_pilot_lifecycle_qualification.mjs",
        "tools/m4/windows_candidate_fixture_qualification.mjs",
        "tools/m4/windows_candidate_fixture_guest.mjs",
        "tools/m4/static_web_candidate_assets.mjs",
        "tools/m4/static_web_candidate_server.mjs",
        "tools/m4/static_web_candidate_driver.mjs",
        "tools/m4/static_web_candidate_observations.mjs",
        "tools/m4/static_web_candidate_lifecycle.mjs",
        "tools/m4/static_web_pilot_browser_boundary.mjs",
        "tools/m4/static_web_candidate_payload.mjs",
        "tools/m4/static_web_candidate_transport.mjs",
        "tools/m4/static_web_candidate_transport_bridge.mjs",
        "tools/m4/static_web_candidate_build.mjs",
        "tools/m4/static_web_build_qualification.mjs",
        "tools/m4/windows_pinned_dispatch_qualification.mjs",
        "tools/m4/static_web_candidate_dispatch.mjs",
        "tools/m4/windows_pilot_interruption_qualification.mjs",
        "tools/m4/windows_pilot_browser_boundary_guest.mjs",
        "tools/m4/profiles/playwright-chromium-seccomp.json",
        "tools/m4/profiles/m47-fixed-chromium-chroot-seccomp.json",
        "src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/working.html",
        "src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/working.js",
        "src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/broken.html",
        "src/CodingAssistant.Web/tests/browser/fixtures/calculation-journal/broken.js"
    ];
    internal static IReadOnlyList<string> FileNames=>Array.AsReadOnly(RequiredFiles);
    internal static bool HasExactFilePins(IReadOnlyDictionary<string,string>? pins)=>
        pins is not null&&pins.Count==RequiredFiles.Length&&
        pins.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(RequiredFiles);
    internal static void RequirePinnedFiles(string root,IReadOnlyDictionary<string,string>? pins)
    {
        if(!Path.IsPathFullyQualified(root)||!HasExactFilePins(pins))
            throw new StoreFailure("PILOT_HOST_FILE_PINS_REQUIRED");
        foreach(var name in RequiredFiles)
            EvaluationPlanBinder.RequirePinnedFile(Path.Combine(root,name),pins![name]);
    }

    private static readonly Regex Receipt=new("^[a-z]+-static-web-[0-9a-f]{12}\\.json\\z",RegexOptions.CultureInvariant);
    private static readonly Regex Digest=new("^[0-9a-f]{64}\\z",RegexOptions.CultureInvariant);
    internal async Task<JsonElement> InspectAsync(IReadOnlyList<string> receipts,CancellationToken token)
    {
        if(receipts.Count is not (4 or 5 or 7)||receipts.Distinct(StringComparer.Ordinal).Count()!=receipts.Count||
            receipts.Any(x=>x is null||!x.StartsWith("windows-static-web-",StringComparison.Ordinal)||!Receipt.IsMatch(x)))
            throw new StoreFailure("PILOT_RECEIPT_SELECTION_INVALID");
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        token.ThrowIfCancellationRequested();
        var root=configuration.RepositoryRoot;
        RequirePinnedFiles(root,configuration.ModulePins);
        var script=EvaluationPlanBinder.RequirePinnedFile(Path.Combine(root,ScriptPath),configuration.BinderSha256);
        var node=EvaluationPlanBinder.RequirePinnedFile(configuration.NodeExecutable,configuration.NodeSha256);
        var docker=EvaluationPlanBinder.RequirePinnedFile(configuration.DockerExecutable,configuration.DockerSha256);
        if(!string.Equals(Path.GetFileName(node),"node.exe",StringComparison.OrdinalIgnoreCase)||
            !string.Equals(Path.GetFileName(docker),"docker.exe",StringComparison.OrdinalIgnoreCase))
            throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");
        var start=new ProcessStartInfo(node){
            WorkingDirectory=Path.GetDirectoryName(script)!,UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8};
        EvaluationPlanBinder.RemoveNodeEnvironmentOverrides(start);
        start.Environment["CA_PILOT_NODE_SHA256"]=configuration.NodeSha256;
        start.Environment["CA_EVALUATION_DOCKER_EXE"]=docker;
        start.Environment["CA_EVALUATION_DOCKER_SHA256"]=configuration.DockerSha256;
        start.ArgumentList.Add(script);start.ArgumentList.Add("--stdin");
        var payload=JsonSerializer.Serialize(new {operation="inspect-complete-pilot",receiptNames=receipts});
        var value=await EvaluationPlanBinder.InvokePlanningProcessAsync(start,payload,TimeSpan.FromSeconds(60),token);
        Validate(value);
        if((receipts.Count>=5)!=value.TryGetProperty("buildQualification",out _)||
            (receipts.Count==7)!=value.TryGetProperty("dispatchQualification",out var dispatch))
            throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
        if(receipts.Count==7&&(dispatch.GetProperty("nodeSha256").GetString()!=configuration.NodeSha256||
            dispatch.GetProperty("dockerSha256").GetString()!=configuration.DockerSha256))
            throw new StoreFailure("PILOT_DISPATCH_BINDING_CHANGED");
        // Recheck every current executable and trusted data input after
        // the read-only operation. This still does not close same-account races.
        RequirePinnedFiles(root,configuration.ModulePins);
        EvaluationPlanBinder.RequirePinnedFile(script,configuration.BinderSha256);
        EvaluationPlanBinder.RequirePinnedFile(node,configuration.NodeSha256);
        EvaluationPlanBinder.RequirePinnedFile(docker,configuration.DockerSha256);
        token.ThrowIfCancellationRequested();
        return value;
    }
    internal static void Validate(JsonElement value)
    {
        bool Exact(JsonElement item,params string[] fields)=>item.ValueKind==JsonValueKind.Object&&
            item.EnumerateObject().Count()==fields.Length&&
            item.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields);
        bool Hash(JsonElement item,string key)=>item.TryGetProperty(key,out var x)&&x.ValueKind==JsonValueKind.String&&Digest.IsMatch(x.GetString()!);
        try
        {
            var upgraded=value.TryGetProperty("buildQualification",out var build);
            var pinned=value.TryGetProperty("dispatchQualification",out var dispatch);
            var fields=new[]{"schemaVersion","status","engineId","policySha256","policy","attempts","evidenceSha256",
                "hostProvenanceEstablished","runtimeQualified","executionEnabled","generatedArtifactVerified","boundary","completeEvidenceSha256"};
            if(upgraded)fields=[..fields,"buildQualification","qualifiedEvidenceSha256"];
            if(pinned)fields=[..fields,"dispatchQualification","dispatchEvidenceSha256"];
            if(!Exact(value,fields)||
                value.GetProperty("schemaVersion").GetInt32()!=1||
                value.GetProperty("status").GetString()!=(pinned?"PILOT_PINNED_DISPATCH_EVIDENCE_BOUND":upgraded?"PILOT_BUILD_QUALIFIED_HISTORICAL_EVIDENCE_BOUND":"PILOT_COMPLETE_HISTORICAL_EVIDENCE_BOUND")||
                (pinned&&(!upgraded||!Hash(value,"dispatchEvidenceSha256")))||
                (upgraded&&!Hash(value,"qualifiedEvidenceSha256"))||
                !Hash(value,"policySha256")||!Hash(value,"evidenceSha256")||!Hash(value,"completeEvidenceSha256")||
                value.GetProperty("engineId").ValueKind!=JsonValueKind.String||
                string.IsNullOrWhiteSpace(value.GetProperty("engineId").GetString())||
                new[]{"hostProvenanceEstablished","runtimeQualified","executionEnabled","generatedArtifactVerified"}
                    .Any(k=>value.GetProperty(k).ValueKind!=JsonValueKind.False))
                throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
            var attempts=value.GetProperty("attempts");
            var ids=new HashSet<string>(StringComparer.Ordinal);
            void Attempt(JsonElement item,bool boundary=false)
            {
                var attemptFields=boundary?new[]{"attemptId","archiveSha256","receiptSha256","policySha256","adapterSha256"}:
                    new[]{"attemptId","archiveSha256","receiptSha256"};
                if(!Exact(item,attemptFields)||!Hash(item,"archiveSha256")||!Hash(item,"receiptSha256")||
                    (boundary&&(!Hash(item,"policySha256")||!Hash(item,"adapterSha256")))||
                    item.GetProperty("attemptId").ValueKind!=JsonValueKind.String||
                    !Regex.IsMatch(item.GetProperty("attemptId").GetString()!,"^[0-9a-f]{32}\\z")||
                    !ids.Add(item.GetProperty("attemptId").GetString()!))
                    throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
            }
            if(!Exact(attempts,"working","broken","spoof","cancel","crash"))
                throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
            foreach(var property in attempts.EnumerateObject())Attempt(property.Value);
            Attempt(value.GetProperty("boundary"),true);
            void ValidatePolicy(JsonElement policy,string policyHash)
            {
            if(!Exact(policy,"schemaVersion","policyId","contractId","imageId","registryDigest","adapterSha256",
                "evaluatorSha256","seccompSha256","environmentSha256","pidLimit","memoryBytes","nanoCpus",
                "shmBytes","scratchBytes","guestDeadlineSeconds","hostDeadlineSeconds","user","network","ipc",
                "rootReadOnly","capabilities","noNewPrivileges","init","autoRemove","hostMounts","entrypoint","entryFlag")||
                !new[]{"adapterSha256","evaluatorSha256","seccompSha256","environmentSha256"}.All(k=>Hash(policy,k))||
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(policy.GetRawText())))!=
                    policyHash||
                policy.GetProperty("schemaVersion").GetInt32()!=1||
                policy.GetProperty("policyId").GetString()!="static-web-calculation-journal-windows-qualification-v1"||
                policy.GetProperty("contractId").GetString()!="calculation-journal-v1"||
                policy.GetProperty("network").GetString()!="none"||policy.GetProperty("hostMounts").GetInt32()!=0||
                policy.GetProperty("ipc").GetString()!="private"||policy.GetProperty("capabilities").GetString()!="none"||
                policy.GetProperty("user").GetString()!="1000:1000"||policy.GetProperty("entrypoint").GetString()!="node"||
                policy.GetProperty("entryFlag").GetString()!="--run-fixed-candidate-fixture"||
                policy.GetProperty("pidLimit").GetInt32()!=256||policy.GetProperty("memoryBytes").GetInt64()!=2147483648L||
                policy.GetProperty("nanoCpus").GetInt64()!=2000000000L||policy.GetProperty("shmBytes").GetInt64()!=268435456L||
                policy.GetProperty("scratchBytes").GetInt64()!=268435456L||policy.GetProperty("guestDeadlineSeconds").GetInt32()!=145||
                policy.GetProperty("hostDeadlineSeconds").GetInt32()!=180||
                !policy.GetProperty("rootReadOnly").GetBoolean()||!policy.GetProperty("noNewPrivileges").GetBoolean()||
                !policy.GetProperty("init").GetBoolean()||policy.GetProperty("autoRemove").GetBoolean())
                throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
            }
            var original=value.GetProperty("policy");
            ValidatePolicy(original,value.GetProperty("policySha256").GetString()!);
            if(upgraded)
            {
                if(!Exact(build,"policySha256","policy","buildSha256","attempts","receiptSha256")||
                    !new[]{"policySha256","buildSha256","receiptSha256"}.All(k=>Hash(build,k))||
                    build.GetProperty("policySha256").GetString()==value.GetProperty("policySha256").GetString())
                    throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
                var newPolicy=build.GetProperty("policy");
                ValidatePolicy(newPolicy,build.GetProperty("policySha256").GetString()!);
                if(newPolicy.GetProperty("adapterSha256").GetString()==original.GetProperty("adapterSha256").GetString()||
                    original.EnumerateObject().Any(p=>p.Name!="adapterSha256"&&
                        p.Value.GetRawText()!=newPolicy.GetProperty(p.Name).GetRawText()))
                    throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
                var buildAttempts=build.GetProperty("attempts");
                if(!Exact(buildAttempts,"build-working","build-broken","build-spoof","build-missing-asset","build-invalid-syntax"))
                    throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
                foreach(var property in buildAttempts.EnumerateObject())Attempt(property.Value);
            }
            if(pinned)
            {
                if(!Exact(dispatch,"policySha256","adapterSha256","nodeSha256","dockerSha256","moduleClosureSha256",
                    "attempts","interruptionReceiptSha256")||
                    !new[]{"policySha256","adapterSha256","nodeSha256","dockerSha256","moduleClosureSha256","interruptionReceiptSha256"}.All(k=>Hash(dispatch,k))||
                    dispatch.GetProperty("policySha256").GetString()!=build.GetProperty("policySha256").GetString()||
                    dispatch.GetProperty("adapterSha256").GetString()!=build.GetProperty("policy").GetProperty("adapterSha256").GetString())
                    throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
                var dispatchAttempts=dispatch.GetProperty("attempts");
                if(!Exact(dispatchAttempts,"working","cancel","crash"))throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
                foreach(var property in dispatchAttempts.EnumerateObject())Attempt(property.Value);
            }
        }
        catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");}
    }
}
