using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// These bindings are supplied by the trusted invocation host, never by
// deserializing a model response. Only Draft is model-proposed data.
public sealed record AgentCommunicationContext(string TaskId, string RunId,
    string WorkItemId, long AssignmentSequence, string FromAgent,
    string SourceSha256, int PolicyRevision, string PolicySha256,
    string MessageId);
public sealed record AgentCommunicationDraft(string ToAgent, string Kind,
    string Summary, long? ReplyTo = null, long? EvidenceSequence = null,
    string? IssueId = null, string? ArchitectureChoiceSha256 = null);
public sealed record AgentCommunication(long Sequence, string MessageId,
    string TaskId, string RunId, string WorkItemId, long AssignmentSequence,
    string FromAgent, string ToAgent, string Kind, string Summary,
    long? ReplyTo, long? EvidenceSequence, int Depth, string PlanSha256,
    string SourceSha256, int PolicyRevision, string PolicySha256, string CreatedUtc);

/// <summary>Durable agent inbox/outbox. Typed proposals are separate from
/// host-observed receipts and cannot themselves execute any operation.
/// Only the application calls Publish, after a validated role invocation.
/// There is intentionally no HTTP endpoint for publishing or impersonation.</summary>
public sealed class AgentCommunicationStore(TaskStore tasks)
{
    public const int MaxConversationMessages = 64;
    public const int MaxReplyDepth = 8;
    private static readonly Regex Id = new("^[0-9a-f]{32}\\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static void Initialize(SqliteConnection db) =>
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS engineering_agent_communications (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                message_id TEXT NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                run_id TEXT NOT NULL,
                work_item_id TEXT NOT NULL,
                assignment_sequence INTEGER NOT NULL,
                from_agent TEXT NOT NULL,
                to_agent TEXT NOT NULL,
                kind TEXT NOT NULL,
                summary TEXT NOT NULL,
                reply_to INTEGER,
                evidence_sequence INTEGER,
                depth INTEGER NOT NULL CHECK(depth>=0),
                plan_sha256 TEXT NOT NULL,
                source_sha256 TEXT NOT NULL,
                policy_revision INTEGER NOT NULL,
                policy_sha256 TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE(task_id,message_id)
            );
            CREATE INDEX IF NOT EXISTS engineering_agent_inbox
                ON engineering_agent_communications(task_id,to_agent,sequence);
            CREATE INDEX IF NOT EXISTS engineering_agent_outbox
                ON engineering_agent_communications(task_id,from_agent,sequence);
            CREATE INDEX IF NOT EXISTS engineering_agent_conversation
                ON engineering_agent_communications(task_id,assignment_sequence,sequence);
            """);

    private const string Columns = """
        SELECT sequence,message_id,task_id,run_id,work_item_id,assignment_sequence,
            from_agent,to_agent,kind,summary,reply_to,evidence_sequence,depth,
            plan_sha256,source_sha256,policy_revision,policy_sha256,created_utc
        FROM engineering_agent_communications
        """;

    private static AgentCommunication Decode(SqliteDataReader reader) => new(
        reader.GetInt64(0),reader.GetString(1),reader.GetString(2),
        reader.GetString(3),reader.GetString(4),reader.GetInt64(5),
        reader.GetString(6),reader.GetString(7),reader.GetString(8),
        reader.GetString(9),reader.IsDBNull(10) ? null : reader.GetInt64(10),
        reader.IsDBNull(11) ? null : reader.GetInt64(11),reader.GetInt32(12),
        reader.GetString(13),reader.GetString(14),reader.GetInt32(15),
        reader.GetString(16),reader.GetString(17));

    private static AgentCommunication? Find(SqliteConnection db, SqliteTransaction tx,
        string taskId, string messageId)
    {
        using var cmd = TaskStore.Command(db, tx,
            Columns + " WHERE task_id=@task AND message_id=@message",
            ("@task",taskId),("@message",messageId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Decode(reader) : null;
    }

    private static bool KindAllowed(string from, string kind) => kind switch
    {
        "ClarificationRequested" or "ClarificationProvided" or
            "Recommendation" or "BlockerRaised" => true,
        "ReviewRequested" => from is "TechnicalLead" or "SoftwareEngineer",
        "FindingRaised" => from is "LeadSoftwareEngineer" or "QaEngineer",
        "FindingChallenged" => from is "SoftwareEngineer",
        "RepairRequested" => from is "TechnicalLead",
        "CompletionRequested" => from is "TechnicalLead" or "ProductOwner",
        _ => false // Assignment, completion, verification and decisions are host evidence.
    };

    public AgentCommunication Publish(AgentCommunicationContext context,
        AgentCommunicationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(draft);
        if (context.MessageId is null || !Id.IsMatch(context.MessageId) ||
            context.AssignmentSequence < 1 || context.PolicyRevision < 1 ||
            draft.Summary is null || draft.Summary.Length is < 1 or > 1200 ||
            string.IsNullOrWhiteSpace(draft.Summary) ||
            draft.Summary.Any(char.IsControl) || draft.ReplyTo is <= 0 ||
            draft.EvidenceSequence is <= 0 || context.FromAgent == draft.ToAgent ||
            !KindAllowed(context.FromAgent,draft.Kind) ||
            (draft.Kind=="FindingChallenged"
                ? string.IsNullOrWhiteSpace(draft.IssueId) || string.IsNullOrWhiteSpace(draft.ArchitectureChoiceSha256)
                : draft.IssueId is not null || draft.ArchitectureChoiceSha256 is not null))
            throw new StoreFailure("INVALID_AGENT_MESSAGE");
        EngineeringRoleCapabilities.Require(context.FromAgent,EngineeringCapability.SendMessage);
        EngineeringRoleCapabilities.Require(draft.ToAgent,EngineeringCapability.SendMessage);

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = EngineeringRunStore.Find(db, tx, context.RunId);
        if (job is null || job.TaskId != context.TaskId)
            throw new StoreFailure("NOT_FOUND");
        EngineeringRunStore.RequireBinding(db, tx, job);
        if (TaskStore.Scalar(db, tx, """
            SELECT id FROM engineering_runs WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task",context.TaskId)) != job.Id ||
            EngineeringMessageStore.SourceFingerprint(db,tx,job.Id) != context.SourceSha256)
            throw new StoreFailure("STALE_SOURCE_RUN");
        EngineeringDelegationStore.Require(db,tx,context.TaskId,
            context.PolicyRevision,context.PolicySha256,context.FromAgent,"SendMessage");

        if (TaskStore.Scalar(db,tx, """
            SELECT 1 FROM engineering_messages WHERE sequence=@sequence AND
                task_id=@task AND run_id=@run AND work_item_id=@item
                AND kind='TaskAssignment' AND plan_sha256=@plan
            """, ("@sequence",context.AssignmentSequence),("@task",context.TaskId),
            ("@run",job.Id),("@item",context.WorkItemId),("@plan",job.PlanSha256)) is null)
            throw new StoreFailure("INVALID_AGENT_MESSAGE");
        if (draft.EvidenceSequence is long evidence &&
            TaskStore.Scalar(db,tx, """
                SELECT 1 FROM engineering_messages WHERE sequence=@sequence AND
                    task_id=@task AND run_id=@run AND source_sha256=@source
                """, ("@sequence",evidence),("@task",context.TaskId),
                ("@run",job.Id),("@source",context.SourceSha256)) is null)
            throw new StoreFailure("INVALID_AGENT_MESSAGE");

        // A challenge is an evidence-bound proposal, never a finding dismissal.
        // The trusted invocation context already pins project, role and assignment.
        if(draft.Kind=="FindingChallenged")
        {
            var issue=EngineeringIssueStore.Read(db,tx,context.TaskId,job.Id).Issues
                .SingleOrDefault(x=>x.Id==draft.IssueId);
            if(issue is null || !issue.CurrentSource ||
                (draft.ToAgent!="TechnicalLead" && draft.ToAgent!=issue.Origin) ||
                TaskStore.Scalar(db,tx,"""
                    SELECT work_item_id FROM engineering_run_files WHERE run_id=@run AND path=@path
                    """,("@run",job.Id),("@path",issue.Path))!=context.WorkItemId)
                throw new StoreFailure("INVALID_AGENT_MESSAGE");
            var architecture=DraftArchitecturePolicy.Capture(db,tx,context.TaskId,
                job.SpecificationVersion,job.SpecificationSha256);
            if(architecture?.Sha256!=job.ArchitectureSha256 ||
                !architecture!.Choices.Any(c=>c.ProposalSha256==draft.ArchitectureChoiceSha256))
                throw new StoreFailure("INVALID_AGENT_MESSAGE");
        }

        var previous = Find(db,tx,context.TaskId,context.MessageId);
        if (previous is not null)
        {
            if (previous.RunId != job.Id || previous.WorkItemId != context.WorkItemId ||
                previous.AssignmentSequence != context.AssignmentSequence ||
                previous.FromAgent != context.FromAgent || previous.ToAgent != draft.ToAgent ||
                previous.Kind != draft.Kind || previous.Summary != draft.Summary ||
                previous.ReplyTo != draft.ReplyTo || previous.EvidenceSequence != draft.EvidenceSequence ||
                previous.SourceSha256 != context.SourceSha256 ||
                previous.PolicyRevision != context.PolicyRevision ||
                previous.PolicySha256 != context.PolicySha256)
                throw new StoreFailure("IDEMPOTENCY_CONFLICT");
            if(draft.Kind=="FindingChallenged" &&
                TaskStore.Scalar(db,tx,"""
                    SELECT 1 FROM engineering_issue_challenges
                    WHERE task_id=@task AND message_id=@message AND issue_id=@issue
                    AND architecture_choice_sha256=@choice
                    """,("@task",context.TaskId),("@message",context.MessageId),
                    ("@issue",draft.IssueId!),("@choice",draft.ArchitectureChoiceSha256!)) is null)
                throw new StoreFailure("IDEMPOTENCY_CONFLICT");
            tx.Commit();
            return previous;
        }
        var depth = 0;
        if (draft.ReplyTo is long reply)
        {
            using var cmd = TaskStore.Command(db,tx, """
                SELECT depth FROM engineering_agent_communications
                WHERE task_id=@task AND assignment_sequence=@assignment
                    AND sequence=@reply AND source_sha256=@source
                    AND (from_agent=@from OR to_agent=@from)
                """, ("@task",context.TaskId),("@assignment",context.AssignmentSequence),
                ("@reply",reply),("@source",context.SourceSha256),("@from",context.FromAgent));
            var priorDepth = cmd.ExecuteScalar();
            if (priorDepth is null) throw new StoreFailure("INVALID_AGENT_MESSAGE");
            depth = Convert.ToInt32(priorDepth) + 1;
        }
        if (depth > MaxReplyDepth || int.Parse(TaskStore.Scalar(db,tx, """
            SELECT COUNT(*) FROM engineering_agent_communications
            WHERE task_id=@task AND assignment_sequence=@assignment
                AND source_sha256=@source
            """, ("@task",context.TaskId),("@assignment",context.AssignmentSequence),
            ("@source",context.SourceSha256))!) >= MaxConversationMessages)
            throw new StoreFailure("AGENT_CONVERSATION_LIMIT");

        TaskStore.Execute(db,tx, """
            INSERT INTO engineering_agent_communications(message_id,task_id,run_id,
                work_item_id,assignment_sequence,from_agent,to_agent,kind,summary,
                reply_to,evidence_sequence,depth,plan_sha256,source_sha256,
                policy_revision,policy_sha256,created_utc)
            VALUES(@message,@task,@run,@item,@assignment,@from,@to,@kind,@summary,
                @reply,@evidence,@depth,@plan,@source,@policy,@sha,@utc)
            """, ("@message",context.MessageId),("@task",context.TaskId),
            ("@run",job.Id),("@item",context.WorkItemId),
            ("@assignment",context.AssignmentSequence),("@from",context.FromAgent),
            ("@to",draft.ToAgent),("@kind",draft.Kind),("@summary",draft.Summary),
            ("@reply",(object?)draft.ReplyTo ?? DBNull.Value),
            ("@evidence",(object?)draft.EvidenceSequence ?? DBNull.Value),("@depth",depth),
            ("@plan",job.PlanSha256),("@source",context.SourceSha256),
            ("@policy",context.PolicyRevision),("@sha",context.PolicySha256),
            ("@utc",DateTimeOffset.UtcNow.ToString("O")));
        if(draft.Kind=="FindingChallenged")
            TaskStore.Execute(db,tx,"""
                INSERT INTO engineering_issue_challenges(task_id,message_id,issue_id,architecture_choice_sha256)
                VALUES(@task,@message,@issue,@choice)
                """,("@task",context.TaskId),("@message",context.MessageId),
                ("@issue",draft.IssueId!),("@choice",draft.ArchitectureChoiceSha256!));
        var result = Find(db,tx,context.TaskId,context.MessageId)!;
        EngineeringActivityStore.Record(db,tx,context.TaskId,draft.Kind,context.FromAgent,
            "AgentCommunication",result.MessageId,result.Sequence,toAgent:draft.ToAgent,
            sourceSha256:context.SourceSha256,agentProposal:true);
        tx.Commit();
        tasks.SignalEngineeringActivity(context.TaskId);
        return result;
    }

    public IReadOnlyList<AgentCommunication> List(string taskId, long after = 0,
        string? role = null, string direction = "all")
    {
        if (after < 0 || direction is not ("all" or "inbox" or "outbox") ||
            (direction != "all" && role is null) ||
            (role is not null && !EngineeringRoleCapabilities.Allows(role,
                EngineeringCapability.SendMessage)))
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        if (TaskStore.FindTask(db,null,taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        using var cmd = TaskStore.Command(db,null,Columns + """
             WHERE task_id=@task AND sequence>@after AND
                (@role='' OR
                    (@direction='all' AND (from_agent=@role OR to_agent=@role)) OR
                    (@direction='inbox' AND to_agent=@role) OR
                    (@direction='outbox' AND from_agent=@role))
             ORDER BY sequence LIMIT 200
            """, ("@task",taskId),("@after",after),("@role",role ?? ""),
            ("@direction",direction));
        using var reader = cmd.ExecuteReader();
        var result = new List<AgentCommunication>();
        while (reader.Read()) result.Add(Decode(reader));
        return result.AsReadOnly();
    }
}
