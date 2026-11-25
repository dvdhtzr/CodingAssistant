using System.Security.Cryptography;
using System.Text.Json;

namespace CodingAssistant.Api;

public sealed record ProductOwnerSkillRequest(string? Name, int ReviewRevision,
    string? ReviewSha256, string? RequestId);

public sealed class ProductOwnerSkillContext
{
    internal ProductOwnerSkillContext(string requestId, string taskId, string operation,
        string model, string objective, IReadOnlyList<InterviewMessage> messages,
        string instructions, string contentSha256, string contextSha256)
    {
        RequestId = requestId;
        TaskId = taskId;
        Operation = operation;
        Model = model;
        Objective = objective;
        Messages = messages;
        Instructions = instructions;
        ContentSha256 = contentSha256;
        ContextSha256 = contextSha256;
    }
    public string RequestId { get; }
    public string TaskId { get; }
    public string Operation { get; }
    public string Model { get; }
    public string ContentSha256 { get; }
    public string ContextSha256 { get; }
    internal string Objective { get; }
    internal IReadOnlyList<InterviewMessage> Messages { get; }
    internal string Instructions { get; }
    private int _consumed;
    internal void Consume(string operation)
    {
        if (Operation != operation)
            throw new StoreFailure("SKILL_SCOPE_DENIED");
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new StoreFailure("IDEMPOTENCY_CONFLICT");
    }
}

public sealed record DiscoverySkillUseEvent(string RequestId, string SkillName, string Role,
    int ReviewRevision, string ReviewSha256, string ContentSha256, string Topic,
    int ExpectedSequence, string ContextSha256, string Model, string Status,
    string CreatedUtc, string? FinishedUtc);

public sealed partial class SkillReviewStore
{
    public const string DiscoverySkill = "requirements-discovery";

    // The host reconstructs the exact context from persisted transcript and
    // the pending owner answer inside the same admission transaction.
    public ProductOwnerSkillContext AdmitDiscovery(string taskId,
        ProductOwnerSkillRequest use, string operation, int expectedSequence,
        string? ownerMessage, string model)
    {
        if (use.Name != DiscoverySkill || use.RequestId is null || !Id.IsMatch(use.RequestId) ||
            use.ReviewRevision < 1 || use.ReviewSha256 is null || !Hash.IsMatch(use.ReviewSha256) ||
            operation is not ("interview-turn" or "specification-draft") ||
            expectedSequence < 0 || !LocalInterviewModel.AllowedModel(model))
            throw new StoreFailure("INVALID_INPUT");
        var skill = SkillCatalog.Discover().Single(s => s.Name == DiscoverySkill);
        if (skill.Role != "ProductOwner" || skill.Scope != "product-discovery" ||
            skill.RequiredToolCategories.Count != 0)
            throw new StoreFailure("SKILL_SCOPE_DENIED");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        CheckTask(db, tx, taskId);
        var review = View(db, tx, taskId, skill);
        if (!review.ApprovedForLocalInstructionReview ||
            review.Revision != use.ReviewRevision || review.ReviewSha256 != use.ReviewSha256)
            throw new StoreFailure("STALE_SKILL_REVIEW");
        var task = TaskStore.FindTask(db, tx, taskId)!;
        var messages = InterviewStore.Messages(db, tx, taskId).ToList();
        var session = InterviewStore.Session(db, tx, taskId);
        var last = messages.LastOrDefault()?.Sequence ?? 0;
        var answers = messages.Count(m => m.Role == "owner");
        if (last != expectedSequence)
            throw new StoreFailure("STALE_INTERVIEW");
        if (operation == "interview-turn")
        {
            if (answers >= InterviewStore.ModelDrivenAnswerLimit ||
                (last == 0 && ownerMessage is not null) ||
                (last > 0 && messages[^1].Role != "assistant") ||
                (session is not null && session.Value.Model != model))
                throw new StoreFailure("STALE_INTERVIEW");
            if (last > 0)
                messages.Add(new InterviewMessage(last + 1, "owner",
                    InterviewStore.ValidOwnerMessage(ownerMessage), DateTimeOffset.UtcNow.ToString("O")));
        }
        else if (ownerMessage is not null || answers < 1 || session is null)
            throw new StoreFailure("STALE_INTERVIEW");
        if (TaskStore.Scalar(db, tx, """
            SELECT request_id FROM discovery_skill_use_events
            WHERE task_id=@task AND request_id=@request
            UNION ALL SELECT request_id FROM skill_use_events
            WHERE task_id=@task AND request_id=@request
            """, ("@task", taskId), ("@request", use.RequestId)) is not null)
            throw new StoreFailure("IDEMPOTENCY_CONFLICT");
        var count = TaskStore.Scalar(db, tx,
            "SELECT COUNT(*) FROM discovery_skill_use_events WHERE task_id=@task", ("@task", taskId));
        if (!int.TryParse(count, out var total) || total >= MaxSkillUses)
            throw new StoreFailure("SKILL_USE_LIMIT");
        var digest = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { taskId, operation, expectedSequence, task.Objective, model,
                messages = messages.Select(m => new { m.Sequence, m.Role, m.Content, m.Choice }) })));
        TaskStore.Execute(db, tx, """
            INSERT INTO discovery_skill_use_events(task_id,request_id,skill_name,role,
                review_revision,review_sha256,content_sha256,topic,expected_sequence,
                context_sha256,model,status,created_utc)
            VALUES(@task,@request,@skill,@role,@revision,@review,@content,@topic,
                @sequence,@context,@model,'Admitted',@utc)
            """, ("@task", taskId), ("@request", use.RequestId),
            ("@skill", skill.Name), ("@role", skill.Role),
            ("@revision", review.Revision), ("@review", review.ReviewSha256),
            ("@content", skill.ContentSha256), ("@topic", operation),
            ("@sequence", expectedSequence), ("@context", digest), ("@model", model),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
        return new ProductOwnerSkillContext(use.RequestId, taskId, operation, model,
            task.Objective, messages.AsReadOnly(), skill.Instructions, skill.ContentSha256, digest);
    }

    public void FinishDiscovery(ProductOwnerSkillContext context, bool returned)
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var changed = TaskStore.Execute(db, tx, """
            UPDATE discovery_skill_use_events SET status=@status,finished_utc=@utc
            WHERE task_id=@task AND request_id=@request AND status='Admitted'
                AND context_sha256=@context AND content_sha256=@content AND model=@model
            """, ("@task", context.TaskId), ("@request", context.RequestId),
            ("@context", context.ContextSha256), ("@content", context.ContentSha256),
            ("@model", context.Model), ("@status", returned ? "ResponseReturned" : "CallFailed"),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        if (changed != 1)
            throw new StoreFailure("INVALID_STATE");
        tx.Commit();
    }

    public IReadOnlyList<DiscoverySkillUseEvent> ReadDiscoveryUses(string taskId)
    {
        using var db = tasks.Open();
        CheckTask(db, null, taskId);
        using var command = TaskStore.Command(db, null, """
            SELECT request_id,skill_name,role,review_revision,review_sha256,
                content_sha256,topic,expected_sequence,context_sha256,
                model,status,created_utc,finished_utc
            FROM discovery_skill_use_events WHERE task_id=@task ORDER BY created_utc,request_id
            """, ("@task", taskId));
        using var reader = command.ExecuteReader();
        var result = new List<DiscoverySkillUseEvent>();
        while (reader.Read())
            result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.GetInt32(3), reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt32(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetString(11), reader.IsDBNull(12) ? null : reader.GetString(12)));
        return result.AsReadOnly();
    }
}
