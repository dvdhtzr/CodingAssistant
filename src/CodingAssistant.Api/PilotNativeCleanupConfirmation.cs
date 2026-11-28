using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace CodingAssistant.Api;

internal sealed partial class PilotExecutionAdmissionStore
{
    // Persistence primitive, not a public or model-accessible attestation
    // endpoint. A host-owned exclusive recovery scope must establish
    // provenance before calling this method. No production caller yet.
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
