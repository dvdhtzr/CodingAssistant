using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// A binding joins historical evidence to explicitly selected current host
// pins. It never claims historical executable provenance or grants execution.
internal static class PilotProposalBinding
{
    private static readonly Regex Digest=new("^[a-f0-9]{64}\\z",RegexOptions.CultureInvariant);
    private static readonly Regex Receipt=new("^windows-static-web-[0-9a-f]{12}\\.json\\z",RegexOptions.CultureInvariant);
    private static string Sha(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string ConfigurationSha256(EvaluationPlanningHostConfiguration c)=>
        Sha(JsonSerializer.Serialize(new {schemaVersion=1,c.RepositoryRoot,c.NodeExecutable,c.NodeSha256,
            c.BinderSha256,modulePins=c.ModulePins.OrderBy(x=>x.Key,StringComparer.Ordinal).Select(x=>new {name=x.Key,sha256=x.Value}),
            c.DockerExecutable,c.DockerSha256}));
    internal static async Task<JsonElement> InspectAsync(string ownerDataDirectory,IReadOnlyList<string> receipts,CancellationToken token)
    {
        var configuration=EvaluationPlanningHostConfiguration.LoadPilot(ownerDataDirectory);
        var configHash=ConfigurationSha256(configuration);
        var evidence=await new PilotQualificationInspector(configuration).InspectAsync(receipts,token);
        if(ConfigurationSha256(EvaluationPlanningHostConfiguration.LoadPilot(ownerDataDirectory))!=configHash)
            throw new StoreFailure("PILOT_HOST_CONFIGURATION_CHANGED");
        return Create(evidence,configHash,receipts);
    }
    internal static JsonElement Create(JsonElement evidence,string configurationSha256,IReadOnlyList<string> receipts)
    {
        PilotQualificationInspector.Validate(evidence);
        if(!Digest.IsMatch(configurationSha256)||receipts.Count is not (4 or 5 or 7)||receipts.Distinct(StringComparer.Ordinal).Count()!=receipts.Count||
            (receipts.Count>=5)!=evidence.TryGetProperty("buildQualification",out _)||
            (receipts.Count==7)!=evidence.TryGetProperty("dispatchQualification",out _)||
            receipts.Any(x=>x is null||!Receipt.IsMatch(x)))
            throw new StoreFailure("PILOT_EXECUTION_BINDING_INVALID");
        // Preserve exact raw evidence bytes: the inspector checks the
        // descriptor hash over its compact JSON representation.
        return JsonSerializer.SerializeToElement(new {schemaVersion=1,kind="pilot-proposal-only",
            configurationSha256,receiptNames=receipts.ToArray(),evidenceJson=evidence.GetRawText(),
            executionEnabled=false,runtimeQualified=false});
    }
    internal static void ValidateAgainstAdmission(JsonElement binding,JsonElement admission)
    {
        try
        {
            var names=binding.EnumerateObject().Select(x=>x.Name).ToArray();
            if(names.Length!=7||!names.ToHashSet(StringComparer.Ordinal).SetEquals(
                new[]{"schemaVersion","kind","configurationSha256","receiptNames","evidenceJson","executionEnabled","runtimeQualified"})||
                binding.GetProperty("schemaVersion").GetInt32()!=1||binding.GetProperty("kind").GetString()!="pilot-proposal-only"||
                binding.GetProperty("executionEnabled").ValueKind!=JsonValueKind.False||
                binding.GetProperty("runtimeQualified").ValueKind!=JsonValueKind.False)
                throw new StoreFailure("PILOT_EXECUTION_BINDING_INVALID");
            var raw=binding.GetProperty("evidenceJson").GetString()??"";
            if(raw.Length is <2 or >90000)throw new StoreFailure("PILOT_EXECUTION_BINDING_INVALID");
            using var evidence=JsonDocument.Parse(raw);
            var receipts=binding.GetProperty("receiptNames").EnumerateArray().Select(x=>x.GetString()??"").ToArray();
            _=Create(evidence.RootElement,binding.GetProperty("configurationSha256").GetString()??"",receipts);
            var policy=evidence.RootElement.GetProperty("policy");
            var qualification=admission.GetProperty("qualification");
            if(admission.GetProperty("contractId").GetString()!=policy.GetProperty("contractId").GetString()||
                admission.GetProperty("evaluatorSha256").GetString()!=policy.GetProperty("evaluatorSha256").GetString()||
                new[]{"imageId","registryDigest","seccompSha256"}.Any(k=>qualification.GetProperty(k).GetString()!=policy.GetProperty(k).GetString())||
                qualification.GetProperty("fixturePidLimit").GetInt32()!=policy.GetProperty("pidLimit").GetInt32())
                throw new StoreFailure("PILOT_ADMISSION_POLICY_MISMATCH");
        }
        catch(Exception e)when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {throw new StoreFailure("PILOT_EXECUTION_BINDING_INVALID");}
    }
}
