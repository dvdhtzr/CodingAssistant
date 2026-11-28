using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// Historical receipt, not a verification grant. Recovery/revocation can
// record a terminal blocker without inventing a guest report or cleanup.
internal sealed record PilotExecutionResult(string AdmissionId,string AttemptId,string Status,
    string? ReceiptSha256,string? Reason,int? Passed,int? Completed,bool CleanupConfirmed,
    bool Current,bool ExecutionEnabled=false,bool RuntimeQualified=false,bool GeneratedArtifactVerified=false);

internal sealed partial class PilotExecutionAdmissionStore
{
    private static readonly string[] SecurityCases=[
        "nonroot_seccomp_no_caps","no_host_mounts_or_socket","credentials_denied","system_write_denied",
        "symlink_write_denied","traversal_write_denied","outbound_and_host_service_denied","child_network_denied",
        "child_system_write_denied","git_remote_denied"];
    private static readonly string[] BuildCases=["exact_source_hashes","javascript_syntax","local_assets_linked","source_integrity_unchanged"];
    private static readonly string[] InfrastructureCases=["browser_sandbox_confirmed","independent_driver_confirmed","browser_closed","asset_server_closed"];
    private static readonly string[] FunctionalCases=["mouse_2_plus_3_equals_5","keyboard_12_5_divided_by_2",
        "decimal_0_1_plus_0_2","backspace_and_divide_by_zero","back_and_history_recall",
        "history_survives_page_reload","history_survives_browser_restart"];
    private static bool ExactResult(JsonElement item,params string[] fields)=>item.ValueKind==JsonValueKind.Object&&
        item.EnumerateObject().Count()==fields.Length&&item.EnumerateObject().Select(x=>x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(fields);
    private static bool ResultHash(JsonElement item)=>item.ValueKind==JsonValueKind.String&&Regex.IsMatch(item.GetString()!,"^[a-f0-9]{64}\\z");
    private static void ResultCheck(bool valid)
    {
        if(!valid)throw new StoreFailure("PILOT_RESULT_INVALID");
    }
    // Only called by the private host result path. Structural validation
    // is not authentication of the host process. No HTTP/model entry.
    internal static PilotExecutionResult ValidateResult(PilotExecutionAdmission item,JsonElement transport,JsonElement receipt)
    {
        try
        {
            ResultCheck(receipt.GetRawText().Length<=16000&&ExactResult(receipt,"schemaVersion","stage","status","attemptId","engineId",
                "policySha256","transportSha256","configurationAudited","attachClosed","containerRemoved","cleanupDebt",
                "archiveSha256","observations","reason","executionEnabled","runtimeQualified","generatedArtifactVerified"));
            ResultCheck(receipt.GetProperty("schemaVersion").GetInt32()==1&&receipt.GetProperty("stage").GetString()=="AET-8-candidate-dispatch-lifecycle"&&
                receipt.GetProperty("attemptId").GetString()==item.AttemptId&&receipt.GetProperty("transportSha256").GetString()==item.TransportSha256&&
                new[]{"attemptId","engineId","policySha256","transportSha256"}.All(k=>receipt.GetProperty(k).GetString()==transport.GetProperty(k).GetString())&&
                new[]{"executionEnabled","runtimeQualified","generatedArtifactVerified"}.All(k=>receipt.GetProperty(k).ValueKind==JsonValueKind.False));
            bool Bool(string k)=>receipt.GetProperty(k).GetBoolean();
            var audited=Bool("configurationAudited");var closed=Bool("attachClosed");
            var removed=Bool("containerRemoved");var debt=Bool("cleanupDebt");
            var archive=receipt.GetProperty("archiveSha256");var observations=receipt.GetProperty("observations");
            var status=receipt.GetProperty("status").GetString();
            var failure=receipt.GetProperty("reason");
            if(status=="VERIFICATION_BLOCKED")
            {
                ResultCheck(observations.ValueKind==JsonValueKind.Null&&archive.ValueKind==JsonValueKind.Null&&
                    (failure.ValueKind==JsonValueKind.String&&Regex.IsMatch(failure.GetString()!,"^[A-Z_]{3,90}\\z")||
                     failure.ValueKind==JsonValueKind.Null&&debt));
                return new(item.Id,item.AttemptId,status,Sha(receipt.GetRawText()),failure.GetString(),null,null,
                    closed&&removed&&!debt,false);
            }
            ResultCheck(audited&&closed&&removed&&!debt&&ResultHash(archive)&&failure.ValueKind==JsonValueKind.Null&&
                ExactResult(observations,"status","passed","completed","required","cases","security","build","infrastructure",
                    "executionEnabled","runtimeQualified","generatedArtifactVerified")&&
                new[]{"executionEnabled","runtimeQualified","generatedArtifactVerified"}.All(k=>observations.GetProperty(k).ValueKind==JsonValueKind.False));
            JsonElement Group(string name,string[] keys,bool nullable=false)
            {
                var value=observations.GetProperty(name);
                ResultCheck(ExactResult(value,keys)&&value.EnumerateObject().All(p=>p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False||
                    nullable&&p.Value.ValueKind==JsonValueKind.Null));
                return value;
            }
            var security=Group("security",SecurityCases);
            var build=Group("build",BuildCases);
            var infrastructure=Group("infrastructure",InfrastructureCases,true);
            var cases=Group("cases",FunctionalCases,true);
            bool All(JsonElement group)=>group.EnumerateObject().All(p=>p.Value.ValueKind==JsonValueKind.True);
            ResultCheck(All(security)&&build.GetProperty("exact_source_hashes").GetBoolean()&&build.GetProperty("source_integrity_unchanged").GetBoolean());
            var passed=cases.EnumerateObject().Count(p=>p.Value.ValueKind==JsonValueKind.True);
            var completed=cases.EnumerateObject().Count(p=>p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False);
            string classified;
            if(!build.GetProperty("javascript_syntax").GetBoolean()||!build.GetProperty("local_assets_linked").GetBoolean())
            {
                ResultCheck(completed==0&&infrastructure.EnumerateObject().All(p=>p.Value.ValueKind==JsonValueKind.Null));
                classified="BUILD_FAILED";
            }
            else
            {
                ResultCheck(All(infrastructure)&&completed==FunctionalCases.Length);
                classified=passed==FunctionalCases.Length?"CHECKS_PASSED_NOT_VERIFIED":"TESTS_FAILED";
            }
            ResultCheck(status==classified&&observations.GetProperty("status").GetString()==classified&&
                observations.GetProperty("passed").GetInt32()==passed&&observations.GetProperty("completed").GetInt32()==completed&&
                observations.GetProperty("required").GetInt32()==FunctionalCases.Length);
            return new(item.Id,item.AttemptId,classified,Sha(receipt.GetRawText()),null,passed,completed,true,false);
        }
        catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {throw new StoreFailure("PILOT_RESULT_INVALID");}
    }
    private static JsonElement StoredTransport(SqliteConnection db,SqliteTransaction tx,PilotExecutionAdmission item)
    {
        var raw=TaskStore.Scalar(db,tx,"SELECT transport_json FROM pilot_execution_admissions WHERE id=@id",("@id",item.Id));
        if(raw is null||raw.Length>16000)throw new StoreFailure("PILOT_RESULT_BINDING_CHANGED");
        using var json=JsonDocument.Parse(raw);
        return json.RootElement.Clone();
    }
    private static bool ResultCurrent(SqliteConnection db,SqliteTransaction tx,PilotExecutionAdmission item,JsonElement transport)
    {
        try
        {
            var identity=Identity(db,tx,item.TaskId,item.ProposalId,transport);
            return identity.Consent==item.ConsentSha256&&identity.Attempt==item.AttemptId&&identity.Transport==item.TransportSha256;
        }
        catch(Exception e)when(e is StoreFailure or JsonException or InvalidOperationException or KeyNotFoundException){return false;}
    }
    private static PilotExecutionResult? ReadResult(SqliteConnection db,SqliteTransaction tx,PilotExecutionAdmission item,JsonElement transport)
    {
        using var cmd=TaskStore.Command(db,tx,"SELECT status,receipt_json,receipt_sha256,reason FROM pilot_execution_results WHERE admission_id=@id",("@id",item.Id));
        using var row=cmd.ExecuteReader();
        if(!row.Read())return null;
        var status=row.GetString(0);var raw=row.IsDBNull(1)?null:row.GetString(1);
        var hash=row.IsDBNull(2)?null:row.GetString(2);var reason=row.IsDBNull(3)?null:row.GetString(3);row.Close();
        if(raw is null)
        {
            ResultCheck(hash is null&&status=="VERIFICATION_BLOCKED"&&(reason is "PILOT_RESULT_INTERRUPTED" or "PILOT_RESULT_REVOKED"));
            return new(item.Id,item.AttemptId,status,null,reason,null,null,false,false);
        }
        ResultCheck(raw.Length<=16000&&Sha(raw)==hash);
        using var json=JsonDocument.Parse(raw);
        var result=ValidateResult(item,transport,json.RootElement);
        ResultCheck(result.Status==status&&result.Reason==reason);
        return result with {Current=ResultCurrent(db,tx,item,transport)};
    }
    // Durable receipt persistence is not native attestation. Only the private
    // dispatcher may call this after establishing live host provenance.
    // Model-free tests exercise the ledger via reflection in a disposable DB.
    private PilotExecutionResult RecordResult(string taskId,string id,JsonElement receipt)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State!="Consumed")throw new StoreFailure("PILOT_RESULT_NOT_CONSUMED");
        var transport=StoredTransport(db,tx,item);
        var result=ValidateResult(item,transport,receipt);
        var prior=ReadResult(db,tx,item,transport);
        if(prior is not null)
        {
            if(prior.ReceiptSha256!=result.ReceiptSha256)throw new StoreFailure("PILOT_RESULT_ALREADY_RECORDED");
            tx.Commit();return prior;
        }
        if(!ResultCurrent(db,tx,item,transport))throw new StoreFailure("PILOT_RESULT_BINDING_CHANGED");
        // A consumed row alone is not dispatch provenance. A new result
        // must have the matching durable native reservation and launch
        // claim. This is a necessary identity check, not native attestation.
        if(TaskStore.Scalar(db,tx,"""
            SELECT d.admission_id FROM pilot_native_dispatches d
            JOIN pilot_native_launch_claims l ON l.admission_id=d.admission_id
            JOIN pilot_execution_transitions t ON t.admission_id=d.admission_id AND t.state='Consumed'
            WHERE d.admission_id=@id AND d.attempt_id=@attempt AND d.consent_sha256=@consent
                AND d.transport_sha256=@transport AND d.job_name=@job
                AND t.attempt_id=d.attempt_id AND t.consent_sha256=d.consent_sha256
                AND t.transport_sha256=d.transport_sha256
            """,("@id",item.Id),("@attempt",item.AttemptId),("@consent",item.ConsentSha256),
            ("@transport",item.TransportSha256),("@job",@"Local\CodingAssistant-Pilot-"+item.AttemptId)) is null)
            throw new StoreFailure("PILOT_NATIVE_RESULT_UNCLAIMED");
        TaskStore.Execute(db,tx,"""
            INSERT INTO pilot_execution_results(admission_id,status,receipt_json,receipt_sha256,reason,recorded_utc)
            VALUES(@id,@status,@json,@hash,@reason,@now)
            """,("@id",id),("@status",result.Status),("@json",receipt.GetRawText()),("@hash",result.ReceiptSha256!),
            ("@reason",(object?)result.Reason??DBNull.Value),("@now",Now()));
        tx.Commit();return result with {Current=true};
    }
    internal PilotExecutionResult? GetResult(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var item=Find(db,tx,taskId,id);
        if(item.State!="Consumed"){tx.Commit();return null;}
        var result=ReadResult(db,tx,item,StoredTransport(db,tx,item));
        tx.Commit();return result;
    }
}
