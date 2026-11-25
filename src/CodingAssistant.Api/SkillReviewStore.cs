using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record SkillReviewRequest(int SchemaVersion, int ExpectedRevision,
    string? ReviewSha256, string? Decision, string? Confirmation,
    bool LocalUsePermissionConfirmed);

public sealed record SkillReviewEvent(int Revision, string SkillName, string Role,
    string Version, string ContentSha256, string License, string ReviewSha256,
    string Decision, bool LocalUsePermissionConfirmed, string CreatedUtc);

public sealed record ProjectSkillReview(SkillCatalogEntry Skill, string ReviewSha256,
    int Revision, string ReviewStatus, bool ApprovedForLocalInstructionReview,
    bool Active, IReadOnlyList<SkillReviewEvent> History);

/// <summary>
/// Durable project-scoped owner review only. A review is not model activation,
/// execution approval or a redistribution license. No model consumes this store.
/// </summary>
public sealed class SkillReviewStore(TaskStore tasks)
{
    public const int SchemaVersion = 1;
    private const int MaxRevisions = 100;
    private static readonly Regex Id = new("^[0-9a-f]{32}\\z",
        RegexOptions.CultureInvariant);

    private static void CheckTask(SqliteConnection db, SqliteTransaction? tx, string taskId)
    {
        if (taskId is null || !Id.IsMatch(taskId))
            throw new StoreFailure("INVALID_ID");
        if (TaskStore.FindTask(db, tx, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
    }

    public static string ReviewSha256(string taskId, SkillCatalogEntry skill)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new {
            schemaVersion = SchemaVersion, taskId, skill.Name, skill.Version,
            skill.SourceUrl, skill.SourceCommit, skill.ContentSha256, skill.License,
            skill.Compatibility, skill.Role, skill.Scope, skill.MaximumBytes,
            skill.RequiredToolCategories, policy = "local-instruction-review-only"
        });
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static IReadOnlyList<SkillReviewEvent> History(SqliteConnection db,
        SqliteTransaction? tx, string taskId, string name)
    {
        using var command = TaskStore.Command(db, tx, """
            SELECT revision,skill_name,role,version,content_sha256,license,
                   review_sha256,decision,local_use_confirmed,created_utc
            FROM skill_review_events WHERE task_id=@task AND skill_name=@name
            ORDER BY revision
            """, ("@task", taskId), ("@name", name));
        using var reader = command.ExecuteReader();
        var result = new List<SkillReviewEvent>();
        while (reader.Read())
            result.Add(new(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetString(3), reader.GetString(4), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt32(8) == 1,
                reader.GetString(9)));
        return result.AsReadOnly();
    }

    private static ProjectSkillReview View(SqliteConnection db, SqliteTransaction? tx,
        string taskId, SkillCatalogEntry skill)
    {
        var hash = ReviewSha256(taskId, skill);
        var history = History(db, tx, taskId, skill.Name);
        var latest = history.LastOrDefault();
        var current = latest?.ReviewSha256 == hash &&
            latest.SkillName == skill.Name && latest.Role == skill.Role &&
            latest.Version == skill.Version &&
            latest.ContentSha256 == skill.ContentSha256 && latest.License == skill.License;
        var approved = current && latest!.Decision == "ApproveLocalUse" &&
            latest.LocalUsePermissionConfirmed;
        var status = latest is null ? "NOT_REVIEWED" :
            !current ? "STALE_REVIEW" :
            approved ? "APPROVED_LOCAL_REVIEW_NOT_ACTIVE" :
            latest.Decision == "Revoke" ? "REVOKED" : "NOT_REVIEWED";
        return new(skill, hash, latest?.Revision ?? 0, status,
            approved, false, history);
    }

    public IReadOnlyList<ProjectSkillReview> Read(string taskId)
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        CheckTask(db, tx, taskId);
        var result = SkillCatalog.Discover().Select(skill =>
            View(db, tx, taskId, skill)).ToArray();
        tx.Commit();
        return result;
    }

    public ProjectSkillReview Review(string taskId, string name, SkillReviewRequest request)
    {
        if (request.SchemaVersion != SchemaVersion || request.ExpectedRevision < 0 ||
            request.Decision is not ("ApproveLocalUse" or "Revoke") ||
            request.ReviewSha256 is null)
            throw new StoreFailure("INVALID_INPUT");
        // Match fixed catalog identities, never construct a path from a request.
        var skill = SkillCatalog.Discover().SingleOrDefault(s => s.Name == name)
            ?? throw new StoreFailure("NOT_FOUND");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        CheckTask(db, tx, taskId);
        var before = View(db, tx, taskId, skill);
        if (request.ExpectedRevision != before.Revision ||
            request.ReviewSha256 != before.ReviewSha256)
            throw new StoreFailure("STALE_SKILL_REVIEW");
        var approve = request.Decision == "ApproveLocalUse";
        var phrase = (approve ? "APPROVE LOCAL USE " : "REVOKE ") + before.ReviewSha256;
        if (request.Confirmation != phrase || request.LocalUsePermissionConfirmed != approve)
            throw new StoreFailure("APPROVAL_MISMATCH");
        if (approve == before.ApprovedForLocalInstructionReview)
            throw new StoreFailure("INVALID_STATE");
        if (before.Revision >= MaxRevisions)
            throw new StoreFailure("SKILL_REVIEW_LIMIT");
        TaskStore.Execute(db, tx, """
            INSERT INTO skill_review_events(task_id,skill_name,revision,role,version,
                content_sha256,license,review_sha256,decision,local_use_confirmed,created_utc)
            VALUES(@task,@name,@revision,@role,@version,@content,@license,@hash,
                @decision,@permission,@utc)
            """, ("@task", taskId), ("@name", skill.Name),
            ("@revision", before.Revision + 1), ("@role", skill.Role),
            ("@version", skill.Version), ("@content", skill.ContentSha256),
            ("@license", skill.License), ("@hash", before.ReviewSha256),
            ("@decision", request.Decision), ("@permission", approve ? 1 : 0),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        var result = View(db, tx, taskId, skill);
        tx.Commit();
        return result;
    }
}
