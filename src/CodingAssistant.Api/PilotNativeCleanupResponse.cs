using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// Structural validation alone does not establish provenance. Only the
// host-owned, separately pinned observation path may supply these data.
internal sealed record PilotNativeCleanupObservation(string AttemptId,string EngineId,string PolicySha256,
    string ContainerId,string JournalSha256);

internal static class PilotNativeCleanupResponse
{
    internal static PilotNativeCleanupObservation Validate(PilotNativeRecoveryIdentity identity,JsonElement response)
    {
        try
        {
            string[] fields=["schemaVersion","status","attemptId","engineId","policySha256","containerId","archiveSha256",
                "containerRemoved","executionEnabled","runtimeQualified","generatedArtifactVerified"];
            if(response.ValueKind!=JsonValueKind.Object||response.EnumerateObject().Count()!=fields.Length||
                !response.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields)||
                response.GetProperty("schemaVersion").GetInt32()!=1||
                response.GetProperty("status").GetString()!="PILOT_EXACT_CLEANUP_OBSERVED"||
                response.GetProperty("attemptId").GetString()!=identity.Obligation.AttemptId||
                response.GetProperty("engineId").GetString()!=identity.EngineId||
                response.GetProperty("policySha256").GetString()!=identity.PolicySha256||
                response.GetProperty("containerRemoved").ValueKind!=JsonValueKind.True||
                new[]{"executionEnabled","runtimeQualified","generatedArtifactVerified"}.Any(k=>response.GetProperty(k).ValueKind!=JsonValueKind.False))
                throw new StoreFailure("PILOT_NATIVE_CLEANUP_RESPONSE_INVALID");
            string Hash(string key)
            {
                var value=response.GetProperty(key).GetString();
                if(value is null||!Regex.IsMatch(value,@"^[a-f0-9]{64}\z"))
                    throw new StoreFailure("PILOT_NATIVE_CLEANUP_RESPONSE_INVALID");
                return value;
            }
            return new(identity.Obligation.AttemptId,identity.EngineId,identity.PolicySha256,Hash("containerId"),Hash("archiveSha256"));
        }
        catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {throw new StoreFailure("PILOT_NATIVE_CLEANUP_RESPONSE_INVALID");}
    }
}
