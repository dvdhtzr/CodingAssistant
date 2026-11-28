using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodingAssistant.Api;

internal sealed partial class PilotExecutionAdmissionStore
{
    // Owner-local orchestration for archived attempts only. Active
    // journals remain blocked until the shared dispatch lease and actual
    // recovery path are qualified. Never accept a caller-provided response.
    internal async Task<PilotNativeCleanupObservation> ConfirmArchivedNativeCleanupAsync(
        PilotNativeCleanupObligation obligation,CancellationToken token)
    {
        if(!OperatingSystem.IsWindows())throw new StoreFailure("PILOT_LOCAL_WINDOWS_ONLY");
        token.ThrowIfCancellationRequested();
        using var connection=tasks.Open();
        var directory=Path.GetDirectoryName(connection.DataSource)
            ??throw new StoreFailure("PILOT_NATIVE_OWNER_DIRECTORY_INVALID");
        using var lease=PilotNativeOwnerLease.Acquire(directory);
        RequireNativeJobAbsent(obligation);
        var identity=GetNativeRecoveryIdentity(obligation);
        var pins=EvaluationPlanningHostConfiguration.LoadNativeRecovery(directory);
        var request=JsonSerializer.Serialize(new {operation="inspect-exact-cleanup",identity=new {
            attemptId=obligation.AttemptId,engineId=identity.EngineId,policySha256=identity.PolicySha256,
            imageId=identity.ImageId,candidateSha256=identity.CandidateSha256,archiveSha256=identity.ArchiveSha256}});
        JsonElement response;
        using(var job=PilotWindowsJob.Create(@"Local\CodingAssistant-Pilot-"+Guid.NewGuid().ToString("N")))
        {
            var observed=await job.InvokeCleanupObservationAsync(pins.Config,pins.BridgeSha256,pins.AdapterSha256,
                request,TimeSpan.FromSeconds(90),token);
            if(observed.ExitCode!=0)throw new StoreFailure("PILOT_NATIVE_CLEANUP_BLOCKED");
            response=observed.Response;
            job.ConfirmEmpty();
        }
        var validated=PilotNativeCleanupResponse.Validate(identity,response);
        var current=EvaluationPlanningHostConfiguration.LoadNativeRecovery(directory);
        if(PilotProposalBinding.ConfigurationSha256(current.Config)!=PilotProposalBinding.ConfigurationSha256(pins.Config)||
            current.BridgeSha256!=pins.BridgeSha256||current.AdapterSha256!=pins.AdapterSha256)
            throw new StoreFailure("PILOT_HOST_CONFIGURATION_CHANGED");
        token.ThrowIfCancellationRequested();
        RequireNativeJobAbsent(obligation);
        using var tx=connection.BeginTransaction();
        // Do not certify an attempt which has not reached a terminal
        // result (unless the original admission was already deleted).
        if(TaskStore.Scalar(connection,tx,"""
            SELECT a.id FROM pilot_execution_admissions a WHERE a.id=@id
                AND NOT EXISTS(SELECT 1 FROM pilot_execution_results r WHERE r.admission_id=a.id)
            """,("@id",obligation.AdmissionId)) is not null)
            throw new StoreFailure("PILOT_NATIVE_CLEANUP_NOT_TERMINAL");
        RecordNativeCleanupConfirmation(connection,tx,identity,response);
        token.ThrowIfCancellationRequested();
        tx.Commit();
        return validated;
    }

    // Persistence primitive, not a public or model-accessible attestation
    // endpoint. A host-owned exclusive recovery scope must establish
    // provenance before calling this method. Only the owner-local archived observation path may call it.
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
