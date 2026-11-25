using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// A single explicitly selected instruction context. Only the host store can
// create it; the model never supplies instruction text or a tool declaration.
public sealed record ArchitectureSkillRequest(string? Name, int ReviewRevision,
    string? ReviewSha256, string? RequestId);

public sealed class ArchitectureSkillContext
{
    internal ArchitectureSkillContext(string requestId, string taskId, string topic,
        string model, int specificationVersion, string specificationSha256,
        string contentSha256, string instructions)
    {
        RequestId = requestId;
        TaskId = taskId;
        Topic = topic;
        Model = model;
        SpecificationVersion = specificationVersion;
        SpecificationSha256 = specificationSha256;
        ContentSha256 = contentSha256;
        Instructions = instructions;
    }
    public string RequestId { get; }
    public string TaskId { get; }
    public string Topic { get; }
    public string Model { get; }
    public string ContentSha256 { get; }
    public int SpecificationVersion { get; }
    public string SpecificationSha256 { get; }
    internal string Instructions { get; }
    private int _consumed;
    internal void Consume()
    {
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new StoreFailure("IDEMPOTENCY_CONFLICT");
    }
}

public sealed record SkillUseEvent(string RequestId, string SkillName, string Role,
    int ReviewRevision, string ReviewSha256, string ContentSha256,
    int SpecificationVersion, string SpecificationSha256, string Topic,
    int ExpectedDecisionRevision, string Model, string Status,
    string CreatedUtc, string? FinishedUtc);

public sealed partial class SkillReviewStore
{
    public const string ArchitectureSkill = "architecture-tradeoffs";
    private const int MaxSkillUses = 200;
    private static readonly Regex Hash = new("^[0-9a-f]{64}\\z",
        RegexOptions.CultureInvariant);

    // Recheck review, specification and proposal revision in one transaction.
    // Admission is an immutable per-call snapshot: revocation blocks future
    // admissions, not a model request that has already been admitted.
    public ArchitectureSkillContext AdmitArchitecture(string taskId,
        RecommendArchitectureRequest request, string model)
    {
        var use = request.Skill;
        if (use is null || use.Name != ArchitectureSkill || use.RequestId is null ||
            !Id.IsMatch(use.RequestId) || use.ReviewRevision < 1 ||
            use.ReviewSha256 is null || !Hash.IsMatch(use.ReviewSha256) ||
            request.SpecificationSha256 is null || !Hash.IsMatch(request.SpecificationSha256) ||
            request.SchemaVersion != ArchitectureDecisionStore.SchemaVersion ||
            request.SpecificationVersion < 1 || request.ExpectedRevision < 0 ||
            !LocalArchitectureAdvisor.AllowedTopic(request.Topic) ||
            !LocalInterviewModel.AllowedModel(model))
            throw new StoreFailure("INVALID_INPUT");

        var skill = SkillCatalog.Discover().Single(s => s.Name == ArchitectureSkill);
        if (skill.Role != "SolutionArchitect" || skill.Scope != "architecture-review" ||
            skill.RequiredToolCategories.Count != 0)
            throw new StoreFailure("SKILL_SCOPE_DENIED");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        CheckTask(db, tx, taskId);
        var review = View(db, tx, taskId, skill);
        if (!review.ApprovedForLocalInstructionReview ||
            review.Revision != use.ReviewRevision || review.ReviewSha256 != use.ReviewSha256)
            throw new StoreFailure("STALE_SKILL_REVIEW");
        using (var command = TaskStore.Command(db, tx, """
            SELECT version,sha256,status FROM interview_specs
            WHERE task_id=@task ORDER BY version DESC LIMIT 1
            """, ("@task", taskId)))
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.GetInt32(0) != request.SpecificationVersion ||
                reader.GetString(1) != request.SpecificationSha256 ||
                reader.GetString(2) != "Approved")
                throw new StoreFailure("SPECIFICATION_CONFLICT");
        }
        var revision = TaskStore.Scalar(db, tx, """
            SELECT COALESCE(MAX(revision),0) FROM architecture_decisions
            WHERE task_id=@task AND topic=@topic
            """, ("@task", taskId), ("@topic", request.Topic!));
        if (revision != request.ExpectedRevision.ToString(
                System.Globalization.CultureInfo.InvariantCulture))
            throw new StoreFailure("STALE_DECISION");
        if (TaskStore.Scalar(db, tx, """
            SELECT request_id FROM skill_use_events WHERE task_id=@task AND request_id=@request
            UNION ALL SELECT request_id FROM discovery_skill_use_events
            WHERE task_id=@task AND request_id=@request
            UNION ALL SELECT request_id FROM qa_skill_use_events
            WHERE task_id=@task AND request_id=@request
            """, ("@task", taskId), ("@request", use.RequestId)) is not null)
            throw new StoreFailure("IDEMPOTENCY_CONFLICT");
        var count = TaskStore.Scalar(db, tx,
            "SELECT COUNT(*) FROM skill_use_events WHERE task_id=@task", ("@task", taskId));
        if (!int.TryParse(count, out var total) || total >= MaxSkillUses)
            throw new StoreFailure("SKILL_USE_LIMIT");
        TaskStore.Execute(db, tx, """
            INSERT INTO skill_use_events(task_id,request_id,skill_name,role,
                review_revision,review_sha256,content_sha256,spec_version,spec_sha256,
                topic,expected_decision_revision,model,status,created_utc)
            VALUES(@task,@request,@skill,@role,@revision,@review,@content,@version,@spec,
                @topic,@expected,@model,'Admitted',@utc)
            """, ("@task", taskId), ("@request", use.RequestId),
            ("@skill", skill.Name), ("@role", skill.Role),
            ("@revision", review.Revision), ("@review", review.ReviewSha256),
            ("@content", skill.ContentSha256), ("@version", request.SpecificationVersion),
            ("@spec", request.SpecificationSha256), ("@topic", request.Topic!),
            ("@expected", request.ExpectedRevision), ("@model", model),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
        return new ArchitectureSkillContext(use.RequestId, taskId, request.Topic!,
            model, request.SpecificationVersion, request.SpecificationSha256,
            skill.ContentSha256, skill.Instructions);
    }

    // "ProposalReturned" is a valid parsed response, not proof of persistence,
    // approval, execution, qualification or a successful build.
    public void FinishArchitecture(ArchitectureSkillContext context, bool returned)
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var changed = TaskStore.Execute(db, tx, """
            UPDATE skill_use_events SET status=@status,finished_utc=@utc
            WHERE task_id=@task AND request_id=@request AND status='Admitted'
                AND content_sha256=@content AND topic=@topic AND model=@model
            """, ("@task", context.TaskId), ("@request", context.RequestId),
            ("@content", context.ContentSha256), ("@topic", context.Topic),
            ("@model", context.Model),
            ("@status", returned ? "ProposalReturned" : "CallFailed"),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        if (changed != 1)
            throw new StoreFailure("INVALID_STATE");
        tx.Commit();
    }

    public IReadOnlyList<SkillUseEvent> ReadUses(string taskId)
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        CheckTask(db, tx, taskId);
        using var command = TaskStore.Command(db, tx, """
            SELECT request_id,skill_name,role,review_revision,review_sha256,
                content_sha256,spec_version,spec_sha256,topic,expected_decision_revision,
                model,status,created_utc,finished_utc
            FROM skill_use_events WHERE task_id=@task ORDER BY created_utc,request_id
            """, ("@task", taskId));
        using var reader = command.ExecuteReader();
        var result = new List<SkillUseEvent>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6), reader.GetString(7), reader.GetString(8),
                reader.GetInt32(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetString(13)));
        return result.AsReadOnly();
    }
}
