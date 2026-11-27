using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// This is the durable single-attempt ledger, not an executor. The real
// qualification gate must be implemented before Authorized/Consumed
// can be reached. No request/model route supplies transport descriptors.
internal sealed record PilotExecutionAdmission(string Id,string TaskId,string ProposalId,string AttemptId,
    string ConsentSha256,string TransportSha256,string State,bool Current,
    bool ExecutionEnabled=false,bool RuntimeQualified=false);

internal sealed partial class PilotExecutionAdmissionStore(TaskStore tasks)
{
    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS pilot_execution_admissions(
            id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            proposal_id TEXT NOT NULL UNIQUE REFERENCES evaluation_execution_proposals(id) ON DELETE CASCADE,
            attempt_id TEXT NOT NULL UNIQUE,
            consent_sha256 TEXT NOT NULL UNIQUE,transport_sha256 TEXT NOT NULL,
            transport_json TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Prepared','Authorized','Consumed','Revoked','Interrupted')),
            created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS pilot_execution_results(
            admission_id TEXT PRIMARY KEY REFERENCES pilot_execution_admissions(id) ON DELETE CASCADE,
            status TEXT NOT NULL CHECK(status IN ('BUILD_FAILED','TESTS_FAILED','CHECKS_PASSED_NOT_VERIFIED','VERIFICATION_BLOCKED')),
            receipt_json TEXT,receipt_sha256 TEXT,reason TEXT,recorded_utc TEXT NOT NULL
        );
        """);
    private const string Columns="""
        SELECT id,task_id,proposal_id,attempt_id,consent_sha256,transport_sha256,state
        FROM pilot_execution_admissions
        """;
    private static string Sha(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Now()=>DateTimeOffset.UtcNow.ToString("O");
    private static PilotExecutionAdmission Read(SqliteDataReader row)=>
        new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),row.GetString(5),row.GetString(6),false);
    private static PilotExecutionAdmission Find(SqliteConnection db,SqliteTransaction tx,string taskId,string id)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(id);
        using var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND id=@id",("@task",taskId),("@id",id));
        using var row=cmd.ExecuteReader();
        return row.Read()?Read(row):throw new StoreFailure("NOT_FOUND");
    }
    private static (string Consent,string Transport,string Attempt) Identity(SqliteConnection db,SqliteTransaction tx,
        string taskId,string proposalId,JsonElement transport)
    {
        var captured=EvaluationExecutionAuthorizationStore.CaptureBoundTransport(db,tx,taskId,proposalId);
        using var historical=JsonDocument.Parse(captured.Binding.GetProperty("evidenceJson").GetString()!);
        var evidence=historical.RootElement;
        if(!evidence.TryGetProperty("buildQualification",out var build))
            throw new StoreFailure("PILOT_BUILD_EVIDENCE_REQUIRED");
        if(!evidence.TryGetProperty("dispatchQualification",out var dispatch))
            throw new StoreFailure("PILOT_DISPATCH_EVIDENCE_REQUIRED");
        try
        {
            var attempt=transport.GetProperty("attemptId").GetString()??"";
            ExecutionStore.CheckId(attempt);
            var used=evidence.GetProperty("attempts").EnumerateObject().Select(x=>x.Value.GetProperty("attemptId").GetString())
                .Append(evidence.GetProperty("boundary").GetProperty("attemptId").GetString())
                .Concat(build.GetProperty("attempts").EnumerateObject().Select(x=>x.Value.GetProperty("attemptId").GetString()))
                .Concat(dispatch.GetProperty("attempts").EnumerateObject().Select(x=>x.Value.GetProperty("attemptId").GetString()));
            if(used.Contains(attempt,StringComparer.Ordinal))throw new StoreFailure("PILOT_ADMISSION_ATTEMPT_REUSED");
            var selection=JsonSerializer.SerializeToElement(new {
                attemptId=attempt,admissionSha256=captured.Descriptor.GetProperty("admissionSha256").GetString(),
                candidateSha256=captured.Descriptor.GetProperty("candidateSha256").GetString(),
                archiveSha256=captured.Input.ArchiveSha256,
                completeEvidenceSha256=evidence.GetProperty("dispatchEvidenceSha256").GetString(),
                policySha256=build.GetProperty("policySha256").GetString(),
                adapterSha256=build.GetProperty("policy").GetProperty("adapterSha256").GetString()
            });
            PilotCandidateTransportPlanner.Validate(transport,selection,captured.Descriptor.GetProperty("evaluatorSha256").GetString()!,
                evidence.GetProperty("engineId").GetString()!,captured.Input.Scope.SourceFingerprint!,captured.Input.Scope.Files.Count);
            var hash=Sha(transport.GetRawText());
            return (Sha(JsonSerializer.Serialize(new {schemaVersion=1,kind="single-attempt-pilot-consent",
                taskId,proposalId,captured.Proposal.AuthorizationSha256,captured.Proposal.PilotBindingSha256,
                attemptId=attempt,transportDescriptorSha256=hash})),transport.GetProperty("transportSha256").GetString()!,attempt);
        }
        catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {throw new StoreFailure("PILOT_TRANSPORT_RESPONSE_INVALID");}
    }
    internal PilotExecutionAdmission Stage(string taskId,string proposalId,JsonElement transport)
    {
        if(transport.GetRawText().Length>16000)throw new StoreFailure("PILOT_TRANSPORT_RESPONSE_INVALID");
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var identity=Identity(db,tx,taskId,proposalId,transport);
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE proposal_id=@proposal",("@proposal",proposalId)))
        using(var row=cmd.ExecuteReader())
            if(row.Read())
            {
                var item=Read(row);row.Close();
                if(item.TaskId!=taskId||item.ConsentSha256!=identity.Consent||item.TransportSha256!=identity.Transport||
                    item.AttemptId!=identity.Attempt)
                    throw new StoreFailure("PILOT_ADMISSION_ALREADY_RESERVED");
                var current=Current(db,tx,item);tx.Commit();return item with {Current=current};
            }
        if(TaskStore.Scalar(db,tx,"SELECT id FROM pilot_execution_admissions WHERE attempt_id=@attempt",("@attempt",identity.Attempt)) is not null)
            throw new StoreFailure("PILOT_ADMISSION_ATTEMPT_REUSED");
        var id=Guid.NewGuid().ToString("N");var now=Now();
        TaskStore.Execute(db,tx,"""
            INSERT INTO pilot_execution_admissions(id,task_id,proposal_id,attempt_id,consent_sha256,transport_sha256,
                transport_json,state,created_utc,updated_utc)
            VALUES(@id,@task,@proposal,@attempt,@consent,@transport,@json,'Prepared',@now,@now)
            """,("@id",id),("@task",taskId),("@proposal",proposalId),("@attempt",identity.Attempt),
            ("@consent",identity.Consent),("@transport",identity.Transport),("@json",transport.GetRawText()),("@now",now));
        tx.Commit();return new(id,taskId,proposalId,identity.Attempt,identity.Consent,identity.Transport,"Prepared",true);
    }
    private static bool Current(SqliteConnection db,SqliteTransaction tx,PilotExecutionAdmission item)
    {
        if(item.State is not ("Prepared" or "Authorized"))return false;
        try
        {
            var raw=TaskStore.Scalar(db,tx,"SELECT transport_json FROM pilot_execution_admissions WHERE id=@id",("@id",item.Id));
            if(raw is null||raw.Length>16000)return false;
            using var document=JsonDocument.Parse(raw);
            var identity=Identity(db,tx,item.TaskId,item.ProposalId,document.RootElement);
            return identity.Consent==item.ConsentSha256&&identity.Attempt==item.AttemptId&&identity.Transport==item.TransportSha256;
        }
        catch(Exception e)when(e is StoreFailure or JsonException or InvalidOperationException or KeyNotFoundException){return false;}
    }
    internal PilotExecutionAdmission Get(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var item=Find(db,tx,taskId,id);
        var current=Current(db,tx,item);tx.Commit();return item with {Current=current};
    }
    internal PilotExecutionAdmission? FindForProposal(string taskId,string proposalId)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(proposalId);
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        using var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND proposal_id=@proposal",
            ("@task",taskId),("@proposal",proposalId));
        using var row=cmd.ExecuteReader();
        if(!row.Read()){row.Close();tx.Commit();return null;}
        var item=Read(row);row.Close();
        var current=Current(db,tx,item);tx.Commit();return item with {Current=current};
    }
    // No authority may be injected by a caller or synthetic evidence.
    // Deliberately closed even if a future registry flag changes.
    private static void RequireExecutablePolicy()
    {
        var profile=RuntimeProfileRegistry.Find("static-web");
        if(profile is null||profile.QualificationStatus!="QUALIFIED"||!profile.ExecutableViaRegistry)
            throw new StoreFailure("EVALUATION_RUNTIME_UNQUALIFIED");
        throw new StoreFailure("EVALUATION_EXECUTION_ADAPTER_NOT_QUALIFIED");
    }
    internal PilotExecutionAdmission Authorize(string taskId,string id,string confirmation)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State!="Prepared"||!Current(db,tx,item))throw new StoreFailure("PILOT_ADMISSION_NOT_CURRENT");
        if(confirmation!="AUTHORIZE PILOT "+item.ConsentSha256)
            throw new StoreFailure("PILOT_ADMISSION_CONFIRMATION_REQUIRED");
        RequireExecutablePolicy();
        if(TaskStore.Execute(db,tx,"UPDATE pilot_execution_admissions SET state='Authorized',updated_utc=@now WHERE id=@id AND state='Prepared'",
            ("@id",id),("@now",Now()))!=1)throw new StoreFailure("PILOT_ADMISSION_NOT_CURRENT");
        tx.Commit();return item with {State="Authorized",Current=true};
    }
    // Fresh trusted transport preparation must reproduce the entire exact
    // descriptor, including the original attempt ID. Consumption is atomic
    // and terminal; future cancellation is separate from replay prevention.
    internal PilotExecutionAdmission Consume(string taskId,string id,JsonElement freshTransport)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State!="Authorized"||!Current(db,tx,item))throw new StoreFailure("PILOT_ADMISSION_NOT_AUTHORIZED");
        var identity=Identity(db,tx,taskId,item.ProposalId,freshTransport);
        if(identity.Consent!=item.ConsentSha256||identity.Attempt!=item.AttemptId||identity.Transport!=item.TransportSha256)
            throw new StoreFailure("PILOT_ADMISSION_TRANSPORT_CHANGED");
        RequireExecutablePolicy();
        if(TaskStore.Execute(db,tx,"UPDATE pilot_execution_admissions SET state='Consumed',updated_utc=@now WHERE id=@id AND state='Authorized'",
            ("@id",id),("@now",Now()))!=1)throw new StoreFailure("PILOT_ADMISSION_NOT_AUTHORIZED");
        tx.Commit();return item with {State="Consumed",Current=false};
    }
    internal PilotExecutionAdmission Revoke(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State is "Prepared" or "Authorized")
        {
            TaskStore.Execute(db,tx,"UPDATE pilot_execution_admissions SET state='Revoked',updated_utc=@now WHERE id=@id AND state IN ('Prepared','Authorized')",
                ("@id",id),("@now",Now()));
            item=item with {State="Revoked"};
        }
        else if(item.State=="Consumed")
        {
            // Consumption remains terminal; revocation prevents a late
            // result from replacing the blocked record.
            TaskStore.Execute(db,tx,"""
                INSERT OR IGNORE INTO pilot_execution_results(admission_id,status,reason,recorded_utc)
                VALUES(@id,'VERIFICATION_BLOCKED','PILOT_RESULT_REVOKED',@now)
                """,("@id",id),("@now",Now()));
        }
        tx.Commit();return item with {Current=false};
    }
    internal static void RevokeProposal(SqliteConnection db,SqliteTransaction tx,string proposalId)
    {
        TaskStore.Execute(db,tx,"UPDATE pilot_execution_admissions SET state='Revoked',updated_utc=@now WHERE proposal_id=@proposal AND state IN ('Prepared','Authorized')",
            ("@proposal",proposalId),("@now",Now()));
        TaskStore.Execute(db,tx,"""
            INSERT OR IGNORE INTO pilot_execution_results(admission_id,status,reason,recorded_utc)
            SELECT id,'VERIFICATION_BLOCKED','PILOT_RESULT_REVOKED',@now
            FROM pilot_execution_admissions WHERE proposal_id=@proposal AND state='Consumed'
            """,("@proposal",proposalId),("@now",Now()));
    }
    internal static void Recover(SqliteConnection db,SqliteTransaction tx)
    {
        TaskStore.Execute(db,tx,"UPDATE pilot_execution_admissions SET state='Interrupted',updated_utc=@now WHERE state IN ('Prepared','Authorized')",("@now",Now()));
        TaskStore.Execute(db,tx,"""
            INSERT OR IGNORE INTO pilot_execution_results(admission_id,status,reason,recorded_utc)
            SELECT id,'VERIFICATION_BLOCKED','PILOT_RESULT_INTERRUPTED',@now
            FROM pilot_execution_admissions WHERE state='Consumed'
            """,("@now",Now()));
    }
}
