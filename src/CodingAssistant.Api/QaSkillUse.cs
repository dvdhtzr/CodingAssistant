using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record QaSkillRequest(string? Name, int ReviewRevision,
    string? ReviewSha256, string? RequestId);

public sealed class QaSkillContext
{
    private readonly QaReviewRun _job;
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase);
    internal QaSkillContext(QaReviewRun job, string instructions)
    {
        _job = job;
        Instructions = instructions;
    }
    internal string Instructions { get; }
    internal void Consume(QaReviewInput input)
    {
        if (input.SourceRun.TaskId != _job.TaskId || input.SourceRun.Id != _job.EngineeringRunId ||
            input.Plan.TaskId != _job.TaskId || input.Plan.PlanSha256 != _job.PlanSha256 ||
            input.Plan.Revision != _job.PlanRevision ||
            input.Plan.SpecificationSha256 != _job.SpecificationSha256 ||
            input.Architecture.ArchitectureSha256 != _job.ArchitectureSha256 ||
            (input.AgentModel ?? input.Plan.Model) != _job.Model ||
            QaReviewStore.Fingerprint(input.AllFiles) != _job.SourceSha256 ||
            !input.AllFiles.Any(f => f.Path == input.File.Path && f.Sha256 == input.File.Sha256 &&
                f.Content == input.File.Content))
            throw new StoreFailure("SKILL_SCOPE_DENIED");
        lock (_used)
            if (!_used.Add(input.File.Path))
                throw new StoreFailure("IDEMPOTENCY_CONFLICT");
    }
}

public sealed record QaSkillUseEvent(string RequestId, string SkillName, string Role,
    int ReviewRevision, string ReviewSha256, string ContentSha256, string Topic,
    string QaReviewId, string EngineeringRunId, string SourceSha256, string PlanSha256,
    string Model, string Status, int ReportsSaved, string CreatedUtc);

public sealed partial class SkillReviewStore
{
    public const string QaSkill = "static-web-verification";

    // Called inside the existing QA admission transaction, after the normal
    // source/plan binding checks. Failure rolls back the QA job as well.
    internal static QaSkillContext AdmitQa(SqliteConnection db, SqliteTransaction tx,
        QaReviewRun job, QaSkillRequest use)
    {
        if (use.Name != QaSkill || use.RequestId is null || !Id.IsMatch(use.RequestId) ||
            use.ReviewRevision < 1 || use.ReviewSha256 is null || !Hash.IsMatch(use.ReviewSha256) ||
            !LocalInterviewModel.AllowedModel(job.Model))
            throw new StoreFailure("INVALID_INPUT");
        var skill = SkillCatalog.Discover().Single(s => s.Name == QaSkill);
        if (skill.Role != "QaEngineer" || skill.Scope != "static-code-review" ||
            skill.RequiredToolCategories.Count != 0)
            throw new StoreFailure("SKILL_SCOPE_DENIED");
        CheckTask(db, tx, job.TaskId);
        var review = View(db, tx, job.TaskId, skill);
        if (!review.ApprovedForLocalInstructionReview ||
            review.Revision != use.ReviewRevision || review.ReviewSha256 != use.ReviewSha256)
            throw new StoreFailure("STALE_SKILL_REVIEW");
        if (TaskStore.Scalar(db, tx, """
            SELECT request_id FROM qa_skill_use_events WHERE task_id=@task AND request_id=@request
            UNION ALL SELECT request_id FROM skill_use_events WHERE task_id=@task AND request_id=@request
            UNION ALL SELECT request_id FROM discovery_skill_use_events
                WHERE task_id=@task AND request_id=@request
            """, ("@task", job.TaskId), ("@request", use.RequestId)) is not null)
            throw new StoreFailure("IDEMPOTENCY_CONFLICT");
        var count = TaskStore.Scalar(db, tx,
            "SELECT COUNT(*) FROM qa_skill_use_events WHERE task_id=@task", ("@task", job.TaskId));
        if (!int.TryParse(count, out var total) || total >= MaxSkillUses)
            throw new StoreFailure("SKILL_USE_LIMIT");
        TaskStore.Execute(db, tx, """
            INSERT INTO qa_skill_use_events(task_id,request_id,qa_id,skill_name,role,
                review_revision,review_sha256,content_sha256,created_utc)
            VALUES(@task,@request,@qa,@skill,@role,@revision,@review,@content,@utc)
            """, ("@task", job.TaskId), ("@request", use.RequestId), ("@qa", job.Id),
            ("@skill", skill.Name), ("@role", skill.Role), ("@revision", review.Revision),
            ("@review", review.ReviewSha256), ("@content", skill.ContentSha256),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        return new QaSkillContext(job, skill.Instructions);
    }

    public IReadOnlyList<QaSkillUseEvent> ReadQaUses(string taskId)
    {
        using var db = tasks.Open();
        CheckTask(db, null, taskId);
        using var command = TaskStore.Command(db, null, """
            SELECT e.request_id,e.skill_name,e.role,e.review_revision,e.review_sha256,
                e.content_sha256,q.id,q.engineering_run_id,q.source_sha256,q.plan_sha256,
                q.model,q.state,(SELECT COUNT(*) FROM qa_review_files f WHERE f.qa_id=q.id),
                e.created_utc
            FROM qa_skill_use_events e JOIN qa_reviews q ON q.id=e.qa_id
                AND q.task_id=e.task_id
            WHERE e.task_id=@task ORDER BY e.created_utc,e.request_id
            """, ("@task", taskId));
        using var reader = command.ExecuteReader();
        var result = new List<QaSkillUseEvent>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetString(5), "static-source-review",
                reader.GetString(6), reader.GetString(7), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetString(11), reader.GetInt32(12), reader.GetString(13)));
        return result.AsReadOnly();
    }
}
