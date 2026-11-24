using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record EngineeringActivity(long Sequence, string TaskId,
    string Category, string Kind, string Label, string Origin, string AgentId,
    string? ToAgent, string ReferenceType, string ReferenceId,
    long? ReferenceSequence, string? SourceSha256, string? Model, string CreatedUtc);
public sealed record EngineeringActivityPage(string TaskId, long LastSequence,
    IReadOnlyList<EngineeringActivity> Events);

/// <summary>Project-scoped, sequence-ordered projection of committed evidence.
/// Labels are host constants, not model messages, prompt contents, source,
/// credentials, tool arguments or private reasoning. No execution authority.</summary>
public sealed class EngineeringActivityStore(TaskStore tasks)
{
    internal static void Initialize(SqliteConnection db) =>
        TaskStore.Execute(db,null,"""
            CREATE TABLE IF NOT EXISTS engineering_activity (
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                sequence INTEGER NOT NULL,
                category TEXT NOT NULL,
                kind TEXT NOT NULL,
                label TEXT NOT NULL,
                origin TEXT NOT NULL,
                agent_id TEXT NOT NULL,
                to_agent TEXT,
                reference_type TEXT NOT NULL,
                reference_id TEXT NOT NULL,
                reference_sequence INTEGER,
                source_sha256 TEXT,
                model TEXT,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(task_id,sequence)
            );
            """);

    private static (string Category,string Label)? Describe(string kind) => kind switch
    {
        "SpecificationHandoff" => ("Team","Approved specification handed to architect"),
        "ArchitectureProposalSaved" => ("Team","Architecture proposal saved for owner"),
        "PlanProposed" => ("Team","Implementation plan proposed"),
        "PlanApproved" => ("Team","Exact implementation plan approved"),
        "DelegationProposed" => ("Team","Delegation proposed; not approved"),
        "DelegationApproved" => ("Team","Exact delegation policy approved"),
        "DelegationRevoked" => ("Team","Delegation revoked"),
        "TaskAssignment" => ("Team","Approved assignment dispatched"),
        "TaskCompleted" => ("Team","Source saved; not tested"),
        "EngineeringRunAdmitted" => ("Engineering","Source-only engineering run admitted"),
        "EngineeringRunResumed" => ("Engineering","Owner-authorized source run resumed"),
        "EngineeringRunCompleted" => ("Engineering","Source generation completed; not tested"),
        "EngineeringSubpackageStarted" => ("Team","Software Engineer received bounded child package"),
        "EngineeringSubpackageCompleted" => ("Engineering","Child package source committed; not tested"),
        "EngineeringRunFailed" => ("Diagnostics","Engineering run stopped safely"),
        "EngineeringRunInterrupted" => ("Diagnostics","Engineering run interrupted"),
        "EngineerAtlasInspected" => ("Engineering","Read-only Code Atlas inspection recorded"),
        "EngineerOutputRejected" => ("Diagnostics","Engineer output rejected; source unchanged"),
        "OllamaRequestStarted" => ("Engineering","Local Ollama request started"),
        "OllamaResponseAccepted" => ("Engineering","Local response accepted by transport; not source verification"),
        "OllamaRequestCancelled" => ("Diagnostics","Local Ollama request cancelled"),
        "OllamaRequestStopped" => ("Diagnostics","Local Ollama request failed or response rejected"),
        "LeadReviewAdmitted" => ("Engineering","Independent lead source review admitted"),
        "LeadFileReviewed" => ("Engineering","Independent architecture review saved; not tested"),
        "LeadAtlasInspected" => ("Engineering","Lead reviewer read-only Code Atlas inspection"),
        "LeadReviewCompleted" => ("Engineering","Lead static review completed; not tested"),
        "LeadReviewFailed" => ("Diagnostics","Lead static review stopped safely"),
        "LeadReviewInterrupted" => ("Diagnostics","Lead static review interrupted"),
        "QaReviewAdmitted" => ("Engineering","Independent static QA admitted"),
        "QaReceivedEngineerInspectionEvidence" => ("Team","QA received recorded inspection evidence"),
        "QaFileReviewed" => ("Engineering","Static file review saved; not tested"),
        "QaAtlasInspected" => ("Engineering","QA Code Atlas inspection recorded"),
        "QaReviewCompleted" => ("Engineering","Static QA completed; no tests executed"),
        "QaReviewFailed" => ("Diagnostics","Static QA stopped safely"),
        "QaReviewInterrupted" => ("Diagnostics","Static QA interrupted"),
        "RepairRunAdmitted" => ("Team","Owner-authorized repair admitted"),
        "RepairGenerated" => ("Engineering","Repair source generated; not tested"),
        "RepairQaRunning" => ("Team","Independent static QA of repair started"),
        "RepairQaReviewed" => ("Engineering","Repair statically reviewed; not tested"),
        "RepairFailed" => ("Diagnostics","Repair stopped safely"),
        "RepairInterrupted" => ("Diagnostics","Repair interrupted"),
        "ClarificationRequested" => ("Team","Clarification requested"),
        "ClarificationProvided" => ("Team","Clarification proposed"),
        "Recommendation" => ("Team","Agent recommendation recorded"),
        "BlockerRaised" => ("Diagnostics","Agent blocker reported"),
        "ReviewRequested" => ("Team","Agent requested review"),
        "FindingRaised" => ("Team","Agent proposed a finding"),
        "FindingChallenged" => ("Team","Agent challenged a finding"),
        "IssueDecisionRecorded" => ("Team","Technical Lead recorded issue classification; no execution authorized"),
        "WorkPlanRevised" => ("Team","Technical Lead revised operational work schedule within approved scope"),
        "IssueStaticRecheckRecorded" => ("Engineering","Independent finding recheck recorded; no functional tests executed"),
        "RepairRequested" => ("Team","Agent requested repair; not authorized by message"),
        "CompletionRequested" => ("Team","Agent requested completion assessment"),
        _ => null
    };

    internal static void Record(SqliteConnection db, SqliteTransaction tx,
        string taskId, string kind, string agentId, string referenceType,
        string referenceId, long? referenceSequence = null, string? toAgent = null,
        string? sourceSha256 = null, string? model = null, bool agentProposal = false)
    {
        var descriptor = Describe(kind);
        if (descriptor is null) return;
        var (category,label) = descriptor.Value;
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_activity(task_id,sequence,category,kind,label,
                origin,agent_id,to_agent,reference_type,reference_id,reference_sequence,
                source_sha256,model,created_utc)
            VALUES(@task,(SELECT COALESCE(MAX(sequence),0)+1 FROM engineering_activity
                WHERE task_id=@task),@category,@kind,@label,@origin,@agent,@to,@type,@ref,
                @refseq,@source,@model,@utc)
            """,("@task",taskId),("@category",category),("@kind",kind),("@label",label),
            ("@origin",agentProposal ? "AgentProposal" : "HostObservation"),
            ("@agent",agentId),("@to",(object?)toAgent ?? DBNull.Value),
            ("@type",referenceType),("@ref",referenceId),
            ("@refseq",(object?)referenceSequence ?? DBNull.Value),
            ("@source",(object?)sourceSha256 ?? DBNull.Value),
            ("@model",model is not null && LocalInterviewModel.AllowedModel(model)
                ? (object)model : DBNull.Value),("@utc",DateTimeOffset.UtcNow.ToString("O")));
    }

    public EngineeringActivityPage Read(string taskId,long after = 0,int limit = 200)
    {
        if (after < 0 || limit is < 1 or > 200)
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction(deferred:true);
        if (TaskStore.FindTask(db,tx,taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        var last = long.Parse(TaskStore.Scalar(db,tx,"""
            SELECT COALESCE(MAX(sequence),0) FROM engineering_activity WHERE task_id=@task
            """,("@task",taskId))!);
        var events = new List<EngineeringActivity>();
        using (var cmd = TaskStore.Command(db,tx,"""
            SELECT sequence,task_id,category,kind,label,origin,agent_id,to_agent,
                reference_type,reference_id,reference_sequence,source_sha256,model,created_utc
            FROM engineering_activity WHERE task_id=@task AND sequence>@after
            ORDER BY sequence LIMIT @limit
            """,("@task",taskId),("@after",after),("@limit",limit)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                events.Add(new(reader.GetInt64(0),reader.GetString(1),reader.GetString(2),
                    reader.GetString(3),reader.GetString(4),reader.GetString(5),reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),reader.GetString(8),
                    reader.GetString(9),reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),reader.GetString(13)));
        tx.Commit();
        return new(taskId,last,events.AsReadOnly());
    }
}
