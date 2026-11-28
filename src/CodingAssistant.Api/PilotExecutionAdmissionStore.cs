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

internal sealed record PilotNativeCleanupObligation(string AdmissionId,string AttemptId,
    string ConsentSha256,string TransportSha256,string JobName);

internal sealed record PilotNativeRecoveryIdentity(PilotNativeCleanupObligation Obligation,
    string EngineId,string PolicySha256,string ImageId,string CandidateSha256,string ArchiveSha256);

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
        CREATE TABLE IF NOT EXISTS pilot_execution_transitions(
            admission_id TEXT NOT NULL REFERENCES pilot_execution_admissions(id) ON DELETE CASCADE,
            state TEXT NOT NULL CHECK(state IN ('Authorized','Consumed')),
            consent_sha256 TEXT NOT NULL,attempt_id TEXT NOT NULL,transport_sha256 TEXT NOT NULL,
            recorded_utc TEXT NOT NULL,PRIMARY KEY(admission_id,state)
        );
        CREATE TABLE IF NOT EXISTS pilot_native_dispatches(
            admission_id TEXT PRIMARY KEY REFERENCES pilot_execution_admissions(id) ON DELETE CASCADE,
            attempt_id TEXT NOT NULL UNIQUE,consent_sha256 TEXT NOT NULL,transport_sha256 TEXT NOT NULL,
            job_name TEXT NOT NULL UNIQUE,created_utc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS pilot_native_launch_claims(
            admission_id TEXT PRIMARY KEY REFERENCES pilot_native_dispatches(admission_id) ON DELETE CASCADE,
            claimed_utc TEXT NOT NULL
        );
        -- Independent of task deletion and terminal result persistence.
        -- A claim is a possible launch, not proof that cleanup occurred.
        CREATE TABLE IF NOT EXISTS pilot_native_cleanup_obligations(
            admission_id TEXT PRIMARY KEY,
            attempt_id TEXT NOT NULL UNIQUE,
            consent_sha256 TEXT NOT NULL,
            transport_sha256 TEXT NOT NULL,
            job_name TEXT NOT NULL UNIQUE,
            created_utc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS pilot_native_recovery_identities(
            admission_id TEXT PRIMARY KEY REFERENCES pilot_native_cleanup_obligations(admission_id),
            engine_id TEXT NOT NULL,policy_sha256 TEXT NOT NULL,image_id TEXT NOT NULL,
            candidate_sha256 TEXT NOT NULL,archive_sha256 TEXT NOT NULL
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
        if(!evidence.TryGetProperty("dispatchQualification",out _))
            throw new StoreFailure("PILOT_DISPATCH_EVIDENCE_REQUIRED");
        if(!evidence.TryGetProperty("jobQualification",out _)||
            !evidence.TryGetProperty("jobIoQualification",out _))
            throw new StoreFailure("PILOT_JOB_IO_EVIDENCE_REQUIRED");
        try
        {
            var attempt=transport.GetProperty("attemptId").GetString()??"";
            ExecutionStore.CheckId(attempt);
            RequireFreshHistoricalAttempt(evidence,attempt);
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
            return (Sha(JsonSerializer.Serialize(new {schemaVersion=2,kind="single-attempt-pilot-consent",
                taskId,proposalId,captured.Proposal.AuthorizationSha256,captured.Proposal.PilotBindingSha256,
                completeHistoricalEvidenceSha256=evidence.GetProperty("jobIoEvidenceSha256").GetString(),
                attemptId=attempt,transportDescriptorSha256=hash})),transport.GetProperty("transportSha256").GetString()!,attempt);
        }
        catch(Exception e)when(e is InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        {throw new StoreFailure("PILOT_TRANSPORT_RESPONSE_INVALID");}
    }
    internal static void RequireFreshHistoricalAttempt(JsonElement evidence,string attempt)
    {
        ExecutionStore.CheckId(attempt);
        if(!evidence.TryGetProperty("jobQualification",out _)||
            !evidence.TryGetProperty("jobIoQualification",out _))
            throw new StoreFailure("PILOT_JOB_IO_EVIDENCE_REQUIRED");
        var used=evidence.GetProperty("attempts").EnumerateObject().Select(x=>x.Value.GetProperty("attemptId").GetString())
            .Append(evidence.GetProperty("boundary").GetProperty("attemptId").GetString())
            .Concat(new[]{"buildQualification","dispatchQualification","jobQualification","jobIoQualification"}
                .SelectMany(k=>evidence.GetProperty(k).GetProperty("attempts").EnumerateObject()
                    .Select(x=>x.Value.GetProperty("attemptId").GetString()))).ToArray();
        if(used.Length!=23||used.Distinct(StringComparer.Ordinal).Count()!=23)
            throw new StoreFailure("PILOT_HOST_RESPONSE_INVALID");
        if(used.Contains(attempt,StringComparer.Ordinal))throw new StoreFailure("PILOT_ADMISSION_ATTEMPT_REUSED");
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
    // Internal database transition primitive; never a grant or process launch.
    // Production callers must pass the closed executable policy first.
    private static void RecordTransition(SqliteConnection db,SqliteTransaction tx,
        PilotExecutionAdmission item,string next)
    {
        var previous=next switch {"Authorized"=>"Prepared","Consumed"=>"Authorized",
            _=>throw new StoreFailure("PILOT_ADMISSION_TRANSITION_INVALID")};
        var error=next=="Authorized"?"PILOT_ADMISSION_NOT_CURRENT":"PILOT_ADMISSION_NOT_AUTHORIZED";
        if(next=="Consumed"&&TaskStore.Scalar(db,tx,"""
            SELECT admission_id FROM pilot_execution_transitions
            WHERE admission_id=@id AND state='Authorized' AND consent_sha256=@consent
                AND attempt_id=@attempt AND transport_sha256=@transport
            """,("@id",item.Id),("@consent",item.ConsentSha256),("@attempt",item.AttemptId),
            ("@transport",item.TransportSha256)) is null)
            throw new StoreFailure(error);
        var now=Now();
        if(TaskStore.Execute(db,tx,"""
            UPDATE pilot_execution_admissions SET state=@next,updated_utc=@now
            WHERE id=@id AND task_id=@task AND state=@previous AND consent_sha256=@consent
                AND attempt_id=@attempt AND transport_sha256=@transport
            """,("@next",next),("@now",now),("@id",item.Id),("@task",item.TaskId),("@previous",previous),
            ("@consent",item.ConsentSha256),("@attempt",item.AttemptId),("@transport",item.TransportSha256))!=1)
            throw new StoreFailure(error);
        TaskStore.Execute(db,tx,"""
            INSERT INTO pilot_execution_transitions(admission_id,state,consent_sha256,attempt_id,transport_sha256,recorded_utc)
            VALUES(@id,@next,@consent,@attempt,@transport,@now)
            """,("@id",item.Id),("@next",next),("@consent",item.ConsentSha256),
            ("@attempt",item.AttemptId),("@transport",item.TransportSha256),("@now",now));
    }
    internal PilotExecutionAdmission Authorize(string taskId,string id,string confirmation)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State!="Prepared"||!Current(db,tx,item))throw new StoreFailure("PILOT_ADMISSION_NOT_CURRENT");
        if(confirmation!="AUTHORIZE PILOT "+item.ConsentSha256)
            throw new StoreFailure("PILOT_ADMISSION_CONFIRMATION_REQUIRED");
        RequireExecutablePolicy();
        RecordTransition(db,tx,item,"Authorized");
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
        RecordTransition(db,tx,item,"Consumed");
        ReserveNativeDispatch(db,tx,item);
        tx.Commit();return item with {State="Consumed",Current=false};
    }
    // Reservation is a durable identity, not proof of process creation.
    // Record it in the same transaction as consumption, before any launch.
    private static void ReserveNativeDispatch(SqliteConnection db,SqliteTransaction tx,PilotExecutionAdmission item)
    {
        var jobName=@"Local\CodingAssistant-Pilot-"+item.AttemptId;
        if(TaskStore.Scalar(db,tx,"""
            SELECT admission_id FROM pilot_execution_transitions
            WHERE admission_id=@id AND state='Consumed' AND consent_sha256=@consent
                AND attempt_id=@attempt AND transport_sha256=@transport
            """,("@id",item.Id),("@consent",item.ConsentSha256),("@attempt",item.AttemptId),
            ("@transport",item.TransportSha256)) is null)
            throw new StoreFailure("PILOT_ADMISSION_NOT_AUTHORIZED");
        TaskStore.Execute(db,tx,"""
            INSERT INTO pilot_native_dispatches(admission_id,attempt_id,consent_sha256,transport_sha256,job_name,created_utc)
            VALUES(@id,@attempt,@consent,@transport,@job,@now)
            """,("@id",item.Id),("@attempt",item.AttemptId),("@consent",item.ConsentSha256),
            ("@transport",item.TransportSha256),("@job",jobName),("@now",Now()));
    }
    // Claim once, before attempting to create a job. A claim is never
    // reusable, including when a process was not created or cleanup failed.
    // Neither a reservation nor a claim establishes native qualification.
    private static string ClaimNativeLaunch(SqliteConnection db,SqliteTransaction tx,PilotExecutionAdmission item)
    {
        var jobName=@"Local\CodingAssistant-Pilot-"+item.AttemptId;
        if(TaskStore.Scalar(db,tx,"""
            SELECT d.job_name FROM pilot_native_dispatches d
            JOIN pilot_execution_admissions a ON a.id=d.admission_id
            JOIN pilot_execution_transitions t ON t.admission_id=a.id AND t.state='Consumed'
            WHERE a.id=@id AND a.task_id=@task AND a.state='Consumed'
                AND a.attempt_id=@attempt AND a.consent_sha256=@consent AND a.transport_sha256=@transport
                AND d.attempt_id=a.attempt_id AND d.consent_sha256=a.consent_sha256 AND d.transport_sha256=a.transport_sha256
                AND t.attempt_id=a.attempt_id AND t.consent_sha256=a.consent_sha256 AND t.transport_sha256=a.transport_sha256
                AND NOT EXISTS(SELECT 1 FROM pilot_execution_results r WHERE r.admission_id=a.id)
            """,("@id",item.Id),("@task",item.TaskId),("@attempt",item.AttemptId),
            ("@consent",item.ConsentSha256),("@transport",item.TransportSha256))!=jobName)
            throw new StoreFailure("PILOT_NATIVE_DISPATCH_NOT_CURRENT");
        if(!ResultCurrent(db,tx,item,StoredTransport(db,tx,item)))
            throw new StoreFailure("PILOT_NATIVE_DISPATCH_NOT_CURRENT");
        // Existing claims retain their replay-specific failure. Different
        // attempts cannot launch while any earlier cleanup is unresolved.
        if(TaskStore.Scalar(db,tx,"SELECT admission_id FROM pilot_native_launch_claims WHERE admission_id=@id",
            ("@id",item.Id)) is not null)
            throw new StoreFailure("PILOT_NATIVE_LAUNCH_ALREADY_CLAIMED");
        RequireNoNativeCleanupDebt(db,tx);
        if(TaskStore.Execute(db,tx,"""
            INSERT OR IGNORE INTO pilot_native_launch_claims(admission_id,claimed_utc) VALUES(@id,@now)
            """,("@id",item.Id),("@now",Now()))!=1)
            throw new StoreFailure("PILOT_NATIVE_LAUNCH_ALREADY_CLAIMED");
        TaskStore.Execute(db,tx,"""
            INSERT INTO pilot_native_cleanup_obligations(admission_id,attempt_id,consent_sha256,transport_sha256,job_name,created_utc)
            VALUES(@id,@attempt,@consent,@transport,@job,@now)
            """,("@id",item.Id),("@attempt",item.AttemptId),("@consent",item.ConsentSha256),
            ("@transport",item.TransportSha256),("@job",jobName),("@now",Now()));
        var captured=EvaluationExecutionAuthorizationStore.CaptureBoundTransport(db,tx,item.TaskId,item.ProposalId);
        using var evidence=JsonDocument.Parse(captured.Binding.GetProperty("evidenceJson").GetString()!);
        var transport=StoredTransport(db,tx,item);
        var recovery=new PilotNativeRecoveryIdentity(new(item.Id,item.AttemptId,item.ConsentSha256,item.TransportSha256,jobName),
            transport.GetProperty("engineId").GetString()!,transport.GetProperty("policySha256").GetString()!,
            evidence.RootElement.GetProperty("buildQualification").GetProperty("policy").GetProperty("imageId").GetString()!,
            transport.GetProperty("candidateSha256").GetString()!,transport.GetProperty("archiveSha256").GetString()!);
        ValidateRecoveryIdentity(recovery);
        TaskStore.Execute(db,tx,"""
            INSERT INTO pilot_native_recovery_identities(admission_id,engine_id,policy_sha256,image_id,candidate_sha256,archive_sha256)
            VALUES(@id,@engine,@policy,@image,@candidate,@archive)
            """,("@id",item.Id),("@engine",recovery.EngineId),("@policy",recovery.PolicySha256),
            ("@image",recovery.ImageId),("@candidate",recovery.CandidateSha256),("@archive",recovery.ArchiveSha256));
        return jobName;
    }
    private static void RequireNoNativeCleanupDebt(SqliteConnection db,SqliteTransaction tx)
    {
        if(TaskStore.Scalar(db,tx,"SELECT admission_id FROM pilot_native_cleanup_obligations LIMIT 1") is not null)
            throw new StoreFailure("PILOT_PRIOR_CLEANUP_REQUIRED");
    }
    // Host-private inventory, independent of current projects and results.
    // These rows identify recovery work; they do not certify resource absence.
    internal IReadOnlyList<PilotNativeCleanupObligation> PendingNativeCleanup()
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        using var cmd=TaskStore.Command(db,tx,"""
            SELECT admission_id,attempt_id,consent_sha256,transport_sha256,job_name
            FROM pilot_native_cleanup_obligations ORDER BY admission_id LIMIT 129
            """);
        using var reader=cmd.ExecuteReader();
        var pending=new List<PilotNativeCleanupObligation>();
        while(reader.Read())
        {
            var item=new PilotNativeCleanupObligation(reader.GetString(0),reader.GetString(1),
                reader.GetString(2),reader.GetString(3),reader.GetString(4));
            ExecutionStore.CheckId(item.AdmissionId);ExecutionStore.CheckId(item.AttemptId);
            if(item.JobName!=@"Local\CodingAssistant-Pilot-"+item.AttemptId||
                !System.Text.RegularExpressions.Regex.IsMatch(item.ConsentSha256,@"^[a-f0-9]{64}\z")||
                !System.Text.RegularExpressions.Regex.IsMatch(item.TransportSha256,@"^[a-f0-9]{64}\z")||
                pending.Count>=128)
                throw new StoreFailure("PILOT_NATIVE_CLEANUP_IDENTITY_INVALID");
            pending.Add(item);
        }
        reader.Close();tx.Commit();return pending.AsReadOnly();
    }

    private static void ValidateRecoveryIdentity(PilotNativeRecoveryIdentity value)
    {
        static bool Digest(string text)=>System.Text.RegularExpressions.Regex.IsMatch(text,@"^[a-f0-9]{64}\z");
        if(!System.Text.RegularExpressions.Regex.IsMatch(value.EngineId,@"^[A-Za-z0-9:_-]{1,128}\z")||
            !Digest(value.PolicySha256)||!Digest(value.CandidateSha256)||!Digest(value.ArchiveSha256)||
            !value.ImageId.StartsWith("sha256:",StringComparison.Ordinal)||!Digest(value.ImageId[7..]))
            throw new StoreFailure("PILOT_NATIVE_CLEANUP_IDENTITY_INVALID");
    }
    // Legacy claims without a complete snapshot remain blocked; never
    // reconstruct cleanup authority from a changed proposal or source.
    internal PilotNativeRecoveryIdentity GetNativeRecoveryIdentity(PilotNativeCleanupObligation obligation)
    {
        if(!PendingNativeCleanup().Contains(obligation))
            throw new StoreFailure("PILOT_NATIVE_CLEANUP_IDENTITY_INVALID");
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        using var cmd=TaskStore.Command(db,tx,"""
            SELECT i.engine_id,i.policy_sha256,i.image_id,i.candidate_sha256,i.archive_sha256
            FROM pilot_native_recovery_identities i JOIN pilot_native_cleanup_obligations o ON o.admission_id=i.admission_id
            WHERE o.admission_id=@id AND o.attempt_id=@attempt AND o.consent_sha256=@consent
                AND o.transport_sha256=@transport AND o.job_name=@job
            """,("@id",obligation.AdmissionId),("@attempt",obligation.AttemptId),("@consent",obligation.ConsentSha256),
            ("@transport",obligation.TransportSha256),("@job",obligation.JobName));
        using var reader=cmd.ExecuteReader();
        if(!reader.Read())throw new StoreFailure("PILOT_NATIVE_RECOVERY_IDENTITY_REQUIRED");
        var identity=new PilotNativeRecoveryIdentity(obligation,reader.GetString(0),reader.GetString(1),
            reader.GetString(2),reader.GetString(3),reader.GetString(4));
        ValidateRecoveryIdentity(identity);reader.Close();tx.Commit();return identity;
    }

    // Absence of a named Windows job is a necessary observation only.
    // Do not terminate a job opened by name: its identity alone does not
    // establish ownership, and a live owner may still be dispatching.
    internal void RequireNativeJobAbsent(PilotNativeCleanupObligation obligation)
    {
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        if(!PendingNativeCleanup().Contains(obligation))
            throw new StoreFailure("PILOT_NATIVE_CLEANUP_IDENTITY_INVALID");
        if(PilotWindowsJob.Exists(obligation.JobName))
            throw new StoreFailure("PILOT_NATIVE_JOB_STILL_ACTIVE");
        // Deliberately no discharge: Docker may still have an in-flight
        // create or an exact container requiring independent recovery.
    }

    // This host-only seam deliberately cannot reach the native launcher
    // until executable policy is separately qualified. No caller-provided
    // callback, command, or script may substitute for that policy.
    internal string ClaimNativeDispatch(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State!="Consumed")throw new StoreFailure("PILOT_NATIVE_DISPATCH_NOT_CURRENT");
        RequireExecutablePolicy();
        var jobName=ClaimNativeLaunch(db,tx,item);
        tx.Commit();return jobName;
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
        // Conservatively enroll pre-migration claims, including those with
        // terminal receipts. Receipt shape alone cannot discharge cleanup.
        TaskStore.Execute(db,tx,"""
            INSERT INTO pilot_native_cleanup_obligations(admission_id,attempt_id,consent_sha256,transport_sha256,job_name,created_utc)
            SELECT d.admission_id,d.attempt_id,d.consent_sha256,d.transport_sha256,d.job_name,l.claimed_utc
            FROM pilot_native_dispatches d JOIN pilot_native_launch_claims l ON l.admission_id=d.admission_id
            WHERE NOT EXISTS(SELECT 1 FROM pilot_native_cleanup_obligations o WHERE o.admission_id=d.admission_id)
            """);
        TaskStore.Execute(db,tx,"UPDATE pilot_execution_admissions SET state='Interrupted',updated_utc=@now WHERE state IN ('Prepared','Authorized')",("@now",Now()));
        TaskStore.Execute(db,tx,"""
            INSERT OR IGNORE INTO pilot_execution_results(admission_id,status,reason,recorded_utc)
            SELECT id,'VERIFICATION_BLOCKED','PILOT_RESULT_INTERRUPTED',@now
            FROM pilot_execution_admissions WHERE state='Consumed'
            """,("@now",Now()));
    }
}
