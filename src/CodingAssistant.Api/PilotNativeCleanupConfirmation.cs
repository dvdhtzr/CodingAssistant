using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodingAssistant.Api;

internal sealed partial class PilotExecutionAdmissionStore
{
    // Owner-local orchestration. No caller-provided observations or commands.
    internal Task<PilotNativeCleanupObservation> ConfirmArchivedNativeCleanupAsync(
        PilotNativeCleanupObligation obligation,CancellationToken token)=>
        ConfirmNativeCleanupAsync(obligation,false,token);

    // Deliberately not wired to HTTP, startup, or dispatch until shared
    // dispatch ownership and current native qualification are established.
    internal Task<PilotNativeCleanupObservation> RecoverInterruptedNativeCleanupAsync(
        PilotNativeCleanupObligation obligation,CancellationToken token)=>
        ConfirmNativeCleanupAsync(obligation,true,token);

    private async Task<PilotNativeCleanupObservation> ConfirmNativeCleanupAsync(
        PilotNativeCleanupObligation obligation,bool active,CancellationToken token)
    {
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        token.ThrowIfCancellationRequested();
        using var connection=tasks.Open();
        var directory=Path.GetDirectoryName(connection.DataSource)
            ??throw new StoreFailure("PILOT_NATIVE_OWNER_DIRECTORY_INVALID");
        using var lease=PilotNativeOwnerLease.Acquire(directory);
        RequireNativeJobAbsent(obligation);
        var identity=GetNativeRecoveryIdentity(obligation);
        (EvaluationPlanningHostConfiguration Config,string BridgeSha256,string AdapterSha256,string? ActiveBridgeSha256)
            LoadPins()
        {
            if(active)return EvaluationPlanningHostConfiguration.LoadNativeActiveRecovery(directory);
            var observation=EvaluationPlanningHostConfiguration.LoadNativeRecovery(directory);
            return (observation.Config,observation.BridgeSha256,observation.AdapterSha256,null);
        }
        var pins=LoadPins();
        void RequireCurrentPins()
        {
            var current=LoadPins();
            if(PilotProposalBinding.ConfigurationSha256(current.Config)!=PilotProposalBinding.ConfigurationSha256(pins.Config)||
                current.BridgeSha256!=pins.BridgeSha256||current.AdapterSha256!=pins.AdapterSha256||
                current.ActiveBridgeSha256!=pins.ActiveBridgeSha256)
                throw new StoreFailure("PILOT_HOST_CONFIGURATION_CHANGED");
        }
        string Request(string operation)=>NativeCleanupRequest(identity,operation);
        if(active)
        {
            // Refuse mutation before a terminal result or deleted admission.
            using(var preflight=connection.BeginTransaction())
            {
                RequireTerminalNativeAdmission(connection,preflight,obligation);
                preflight.Commit();
            }
            RequireCurrentPins();
            token.ThrowIfCancellationRequested();
            RequireNativeJobAbsent(obligation);
            using(var recoveryJob=PilotWindowsJob.Create(@"Local\CodingAssistant-Pilot-"+Guid.NewGuid().ToString("N")))
            {
                var recovered=await recoveryJob.InvokeActiveCleanupAsync(pins.Config,pins.ActiveBridgeSha256!,
                    pins.AdapterSha256,Request("recover-exact-cleanup"),TimeSpan.FromSeconds(90),token);
                if(recovered.ExitCode!=0)throw new StoreFailure("PILOT_NATIVE_CLEANUP_BLOCKED");
                recoveryJob.ConfirmEmpty();
                // Validate the response, but never persist it as observation evidence.
                _=PilotNativeCleanupResponse.Validate(identity,recovered.Response);
            }
            RequireCurrentPins();
            token.ThrowIfCancellationRequested();
            RequireNativeJobAbsent(obligation);
        }
        var (response,validated)=await ObserveNativeCleanupUnderLeaseAsync(identity,pins.Config,
            pins.BridgeSha256,pins.AdapterSha256,RequireCurrentPins,token);
        using var tx=connection.BeginTransaction();
        // Do not certify an attempt which has not reached a terminal
        // result (unless the original admission was already deleted).
        RequireTerminalNativeAdmission(connection,tx,obligation);
        RecordNativeCleanupConfirmation(connection,tx,identity,response);
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return validated;
    }

    private static string NativeCleanupRequest(PilotNativeRecoveryIdentity identity,string operation)=>
        JsonSerializer.Serialize(new {operation,identity=new {
            attemptId=identity.Obligation.AttemptId,engineId=identity.EngineId,policySha256=identity.PolicySha256,
            imageId=identity.ImageId,candidateSha256=identity.CandidateSha256,archiveSha256=identity.ArchiveSha256}});

    // Only host orchestration already holding the exclusive native owner
    // lease may call this. Observation neither mutates containers nor
    // records confirmation; the caller owns the persistence boundary.
    private async Task<(JsonElement Response,PilotNativeCleanupObservation Observation)>
        ObserveNativeCleanupUnderLeaseAsync(PilotNativeRecoveryIdentity identity,
            EvaluationPlanningHostConfiguration config,string bridgeSha256,string adapterSha256,
            Action requireCurrentPins,CancellationToken token)
    {
        var obligation=identity.Obligation;
        requireCurrentPins();
        token.ThrowIfCancellationRequested();
        RequireNativeJobAbsent(obligation);
        var request=NativeCleanupRequest(identity,"inspect-exact-cleanup");
        JsonElement response;
        using(var job=PilotWindowsJob.Create(@"Local\CodingAssistant-Pilot-"+Guid.NewGuid().ToString("N")))
        {
            var observed=await job.InvokeCleanupObservationAsync(config,bridgeSha256,adapterSha256,
                request,TimeSpan.FromSeconds(90),token);
            if(observed.ExitCode!=0)throw new StoreFailure("PILOT_NATIVE_CLEANUP_BLOCKED");
            response=observed.Response;
            job.ConfirmEmpty();
        }
        var validated=PilotNativeCleanupResponse.Validate(identity,response);
        requireCurrentPins();
        token.ThrowIfCancellationRequested();
        RequireNativeJobAbsent(obligation);
        return (response,validated);
    }

    private static void RequireTerminalNativeAdmission(SqliteConnection connection,SqliteTransaction tx,
        PilotNativeCleanupObligation obligation)
    {
        if(TaskStore.Scalar(connection,tx,"""
            SELECT a.id FROM pilot_execution_admissions a WHERE a.id=@id
                AND NOT EXISTS(SELECT 1 FROM pilot_execution_results r WHERE r.admission_id=a.id)
            """,("@id",obligation.AdmissionId)) is not null)
            throw new StoreFailure("PILOT_NATIVE_CLEANUP_NOT_TERMINAL");
    }

    // Persistence primitive, not a public or model-accessible attestation
    // endpoint. A host-owned exclusive recovery scope must establish
    // provenance before calling this method. Only independent owner-local observation may call it.
    private static void RecordNativeCleanupConfirmation(SqliteConnection db,SqliteTransaction tx,
        PilotNativeRecoveryIdentity identity,JsonElement response)
    {
        ValidateRecoveryIdentity(identity);
        var observed=PilotNativeCleanupResponse.Validate(identity,response);
        var obligation=identity.Obligation;
        var hash=Sha(response.GetRawText());
        if(TaskStore.Scalar(db,tx,"""
            SELECT o.admission_id FROM pilot_native_cleanup_obligations o
            JOIN pilot_native_recovery_identities i ON i.admission_id=o.admission_id
            WHERE o.admission_id=@id AND o.attempt_id=@attempt AND o.consent_sha256=@consent
                AND o.transport_sha256=@transport AND o.job_name=@job
                AND i.engine_id=@engine AND i.policy_sha256=@policy AND i.image_id=@image
                AND i.candidate_sha256=@candidate AND i.archive_sha256=@archive
            """,("@id",obligation.AdmissionId),("@attempt",obligation.AttemptId),("@consent",obligation.ConsentSha256),
            ("@transport",obligation.TransportSha256),("@job",obligation.JobName),
            ("@engine",identity.EngineId),("@policy",identity.PolicySha256),("@image",identity.ImageId),
            ("@candidate",identity.CandidateSha256),("@archive",identity.ArchiveSha256)) is null)
            throw new StoreFailure("PILOT_NATIVE_CLEANUP_IDENTITY_INVALID");
        var prior=TaskStore.Scalar(db,tx,"SELECT observation_sha256 FROM pilot_native_cleanup_confirmations WHERE admission_id=@id",
            ("@id",obligation.AdmissionId));
        if(prior is not null)
        {
            if(prior!=hash)throw new StoreFailure("PILOT_NATIVE_CLEANUP_ALREADY_CONFIRMED");
            return;
        }
        TaskStore.Execute(db,tx,"""
            INSERT INTO pilot_native_cleanup_confirmations(admission_id,container_id,journal_sha256,observation_sha256,recorded_utc)
            VALUES(@id,@container,@journal,@hash,@now)
            """,("@id",obligation.AdmissionId),("@container",observed.ContainerId),
            ("@journal",observed.JournalSha256),("@hash",hash),("@now",Now()));
    }
}
