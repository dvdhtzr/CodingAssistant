using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// Host-only preview of candidate guest transport. No public route, grant,
// executable artifact, Docker create/start or shell.
internal sealed class PilotCandidateTransportPlanner(TaskStore tasks)
{
    internal const string ScriptPath="tools/m4/static_web_candidate_transport_bridge.mjs";
    private static readonly Regex Receipt=new("^windows-static-web-[0-9a-f]{12}\\.json\\z",RegexOptions.CultureInvariant);
    private static readonly Regex Digest=new("^[0-9a-f]{64}\\z",RegexOptions.CultureInvariant);
    internal async Task<PilotExecutionAdmission> StageAsync(string taskId,string proposalId,string ownerDataDirectory,
        string trustedReceiptName,CancellationToken token)
    {
        var admissions=new PilotExecutionAdmissionStore(tasks);
        var prior=admissions.FindForProposal(taskId,proposalId);
        if(prior is not null&&!prior.Current)throw new StoreFailure("PILOT_ADMISSION_NOT_CURRENT");
        var descriptor=await PrepareAsync(taskId,proposalId,ownerDataDirectory,trustedReceiptName,token,prior?.AttemptId);
        token.ThrowIfCancellationRequested();
        return admissions.Stage(taskId,proposalId,descriptor);
    }
    internal async Task<JsonElement> PrepareAsync(string taskId,string proposalId,string ownerDataDirectory,
        string trustedReceiptName,CancellationToken token,string? attemptId=null)
    {
        if(attemptId is not null)ExecutionStore.CheckId(attemptId);
        if(!Receipt.IsMatch(trustedReceiptName))throw new StoreFailure("EVALUATION_RECEIPT_NAME_INVALID");
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        token.ThrowIfCancellationRequested();
        var store=new EvaluationExecutionAuthorizationStore(tasks);
        var captured=store.CaptureBoundTransport(taskId,proposalId);
        var config=EvaluationPlanningHostConfiguration.LoadPilot(ownerDataDirectory);
        var configHash=PilotProposalBinding.ConfigurationSha256(config);
        if(configHash!=captured.Binding.GetProperty("configurationSha256").GetString())
            throw new StoreFailure("PILOT_HOST_CONFIGURATION_CHANGED");
        using var historical=JsonDocument.Parse(captured.Binding.GetProperty("evidenceJson").GetString()!);
        var evidence=historical.RootElement;
        // The old fixture-only preview remains diagnostic. This host
        // path now requires the separately build-qualified adapter.
        if(!evidence.TryGetProperty("buildQualification",out var build))
            throw new StoreFailure("PILOT_BUILD_EVIDENCE_REQUIRED");
        var selection=JsonSerializer.SerializeToElement(new {
            attemptId=attemptId??Guid.NewGuid().ToString("N"),
            admissionSha256=captured.Descriptor.GetProperty("admissionSha256").GetString(),
            candidateSha256=captured.Descriptor.GetProperty("candidateSha256").GetString(),
            archiveSha256=captured.Input.ArchiveSha256,
            completeEvidenceSha256=evidence.GetProperty("qualifiedEvidenceSha256").GetString(),
            policySha256=build.GetProperty("policySha256").GetString(),
            adapterSha256=build.GetProperty("policy").GetProperty("adapterSha256").GetString()
        });
        var request=JsonSerializer.Serialize(new {
            operation="prepare-candidate-transport",trustedReceiptName,
            receiptNames=captured.Binding.GetProperty("receiptNames"),currentScope=captured.Input.Scope,
            criteria=captured.Input.Criteria,reviewedContract=captured.Contract,
            archiveBase64=Convert.ToBase64String(captured.Input.Archive),selection
        },new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if(Encoding.UTF8.GetByteCount(request)>3*1024*1024)
            throw new StoreFailure("EVALUATION_HOST_REQUEST_TOO_LARGE");
        void CheckFiles()
        {
            PilotQualificationInspector.RequirePinnedFiles(config.RepositoryRoot,config.ModulePins);
            EvaluationPlanBinder.RequirePinnedFile(Path.Combine(config.RepositoryRoot,PilotQualificationInspector.ScriptPath),config.BinderSha256);
            EvaluationPlanBinder.RequirePinnedFile(config.NodeExecutable,config.NodeSha256);
            EvaluationPlanBinder.RequirePinnedFile(config.DockerExecutable,config.DockerSha256);
        }
        CheckFiles();
        var script=Path.Combine(config.RepositoryRoot,ScriptPath);
        if(!string.Equals(Path.GetFileName(config.NodeExecutable),"node.exe",StringComparison.OrdinalIgnoreCase)||
            !string.Equals(Path.GetFileName(config.DockerExecutable),"docker.exe",StringComparison.OrdinalIgnoreCase))
            throw new StoreFailure("EVALUATION_BINDER_PROVENANCE_INVALID");
        var start=new ProcessStartInfo(config.NodeExecutable){
            WorkingDirectory=Path.GetDirectoryName(script)!,UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardInputEncoding=new UTF8Encoding(false),StandardOutputEncoding=Encoding.UTF8};
        EvaluationPlanBinder.RemoveNodeEnvironmentOverrides(start);
        start.Environment["CA_EVALUATION_DOCKER_EXE"]=config.DockerExecutable;
        start.Environment["CA_EVALUATION_DOCKER_SHA256"]=config.DockerSha256;
        start.ArgumentList.Add(script);start.ArgumentList.Add("--stdin");
        var response=await EvaluationPlanBinder.InvokePlanningProcessAsync(start,request,TimeSpan.FromSeconds(60),token);
        Validate(response,selection,captured.Descriptor.GetProperty("evaluatorSha256").GetString()!,
            evidence.GetProperty("engineId").GetString()!,captured.Input.Scope.SourceFingerprint!,
            captured.Input.Scope.Files.Count);
        CheckFiles();
        if(PilotProposalBinding.ConfigurationSha256(EvaluationPlanningHostConfiguration.LoadPilot(ownerDataDirectory))!=configHash)
            throw new StoreFailure("PILOT_HOST_CONFIGURATION_CHANGED");
        token.ThrowIfCancellationRequested();
        var current=store.CaptureBoundTransport(taskId,proposalId);
        if(current.Proposal.AuthorizationSha256!=captured.Proposal.AuthorizationSha256||
            current.Proposal.PilotBindingSha256!=captured.Proposal.PilotBindingSha256)
            throw new StoreFailure("PILOT_EXECUTION_BINDING_CHANGED");
        // Only bounded metadata; even this response is not a grant.
        return response;
    }
    internal static void Validate(JsonElement response,JsonElement selection,string evaluator,string engine,string sourceFingerprint,int assetCount)
    {
        try
        {
            var names=response.EnumerateObject().Select(x=>x.Name).ToArray();
            var fields=new[]{"schemaVersion","status","attemptId","candidateSha256","archiveSha256","evaluatorSha256",
                "adapterSha256","policySha256","admissionSha256","completeEvidenceSha256","engineId","sourceFingerprint",
                "assetCount","payloadSha256","transportSha256","transportBytes","executionEnabled","runtimeQualified","generatedArtifactVerified"};
            if(names.Length!=fields.Length||!names.ToHashSet(StringComparer.Ordinal).SetEquals(fields)||
                response.GetProperty("schemaVersion").GetInt32()!=1||
                response.GetProperty("status").GetString()!="CANDIDATE_TRANSPORT_PREPARED_ONLY"||
                selection.EnumerateObject().Any(x=>response.GetProperty(x.Name).GetString()!=x.Value.GetString())||
                response.GetProperty("evaluatorSha256").GetString()!=evaluator||
                response.GetProperty("engineId").GetString()!=engine||
                response.GetProperty("sourceFingerprint").GetString()!=sourceFingerprint||
                response.GetProperty("assetCount").GetInt32()!=assetCount||
                response.GetProperty("transportBytes").GetInt32() is <1 or >4194304||
                !new[]{"payloadSha256","transportSha256"}.All(k=>Digest.IsMatch(response.GetProperty(k).GetString()??""))||
                new[]{"executionEnabled","runtimeQualified","generatedArtifactVerified"}.Any(k=>response.GetProperty(k).ValueKind!=JsonValueKind.False))
                throw new StoreFailure("PILOT_TRANSPORT_RESPONSE_INVALID");
        }
        catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {throw new StoreFailure("PILOT_TRANSPORT_RESPONSE_INVALID");}
    }
}
