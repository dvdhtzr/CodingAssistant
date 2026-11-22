using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// Every interview and specification is bound to an existing authenticated
// owner task. These records are planning-only; none can invoke M1 or modify
// an approved workspace.
// Optional, model-proposed answer affordance. The question text remains in
// Content and free-text answers always remain valid and owner-controlled.
public sealed record InterviewChoice(string Kind, IReadOnlyList<string> Options);
public sealed record InterviewMessage(
    int Sequence, string Role, string Content, string CreatedUtc,
    InterviewChoice? Choice = null);
public sealed record InterviewReply(string Message, bool ReadyForSpecification,
    InterviewChoice? Choice = null);
public sealed record InterviewSpecification(
    int Version, string Model, string Content, string Sha256, string Status,
    string CreatedUtc, string? ApprovedUtc);
public sealed record InterviewSnapshot(
    string TaskId, string? Model, int LastSequence, bool ReadyForSpecification,
    IReadOnlyList<InterviewMessage> Messages, InterviewSpecification? Specification);
public sealed record InterviewTurnRequest(
    int ExpectedSequence, string? Message, string? Model);
public sealed record SpecificationRequest(int ExpectedSequence);
public sealed record ApproveSpecificationRequest(int Version, string? Sha256);

public interface IInterviewModel
{
    Task<InterviewReply> AskAsync(
        string objective, IReadOnlyList<InterviewMessage> messages, string model,
        CancellationToken cancellation);
    Task<string> DraftAsync(
        string objective, IReadOnlyList<InterviewMessage> messages, string model,
        CancellationToken cancellation);
}

public sealed class InterviewStore(TaskStore tasks)
{
    private static readonly Regex IdPattern = new(
        "^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex HashPattern = new(
        "^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    // The model may pose a last question after the twelfth owner answer.
    // Allow its answer to be saved before ending discovery, but do not ask
    // the model another question (the interview context is capped at 25 turns).
    public const int ModelDrivenAnswerLimit = 12;
    public const int MaxOwnerAnswers = ModelDrivenAnswerLimit + 1;
    // Every persisted answer and Product Owner message is retained when
    // drafting. The last answer also receives a deterministic handoff.
    public const int MaxTranscriptMessages = MaxOwnerAnswers * 2 + 1;

    public static InterviewReply FinalAnswerAcknowledgement() => new(
        "Your final answer has been saved. Product discovery is complete. " +
        "You can now draft a first specification and review all requirements, " +
        "assumptions, and remaining open questions before approving anything.",
        true);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS interview_sessions(
                task_id TEXT PRIMARY KEY NOT NULL REFERENCES tasks(id),
                model TEXT NOT NULL,
                ready INTEGER NOT NULL DEFAULT 0 CHECK(ready IN (0,1))
            );
            CREATE TABLE IF NOT EXISTS interview_messages(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                sequence INTEGER NOT NULL CHECK(sequence > 0),
                role TEXT NOT NULL CHECK(role IN ('owner','assistant')),
                content TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(task_id,sequence)
            );
            CREATE TABLE IF NOT EXISTS interview_message_choices(
                task_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                kind TEXT NOT NULL CHECK(kind IN ('single','multiple')),
                options_json TEXT NOT NULL,
                PRIMARY KEY(task_id,sequence),
                FOREIGN KEY(task_id,sequence) REFERENCES interview_messages(task_id,sequence)
                    ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS interview_specs(
                task_id TEXT NOT NULL REFERENCES tasks(id),
                version INTEGER NOT NULL CHECK(version > 0),
                model TEXT NOT NULL,
                content TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('Draft','Approved','Superseded')),
                created_utc TEXT NOT NULL,
                approved_utc TEXT,
                PRIMARY KEY(task_id,version)
            );
            """);
    }

    private static void ValidateId(string taskId)
    {
        if (!IdPattern.IsMatch(taskId))
            throw new StoreFailure("INVALID_ID");
    }

    public static string ValidOwnerMessage(string? message)
    {
        if (message is null || message.Trim().Length is < 1 or > 2400 ||
            message.Any(ch => char.IsControl(ch) &&
                ch is not ('\r' or '\n' or '\t')))
            throw new StoreFailure("INVALID_INPUT");
        return message.Trim();
    }

    // Malformed optional LLM suggestions degrade to the regular text composer;
    // they never invalidate an otherwise useful question or create a new gate.
    public static InterviewChoice? ValidChoice(InterviewChoice? choice)
    {
        if (choice is null) return null;
        if (choice.Kind is not ("single" or "multiple") ||
            choice.Options is null || choice.Options.Count is < 2 or > 5)
            return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var raw in choice.Options)
        {
            var value = raw?.Trim();
            if (value is null || value.Length is < 2 or > 120 ||
                value.Any(char.IsControl) || !seen.Add(value))
                return null;
            normalized.Add(value);
        }
        return new InterviewChoice(choice.Kind, normalized);
    }

    public static string ValidAssistantMessage(string? message)
    {
        if (message is null || message.Trim().Length is < 10 or > 3000 ||
            message.Any(ch => char.IsControl(ch) &&
                ch is not ('\r' or '\n' or '\t')))
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        return message.Trim();
    }

    public static string ValidSpecification(string? content)
    {
        if (content is null || content.Trim().Length is < 180 or > 16000 ||
            content.Any(ch => ch == '\0' || (char.IsControl(ch) &&
                ch is not ('\r' or '\n' or '\t'))))
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        return content.Trim();
    }

    private static IReadOnlyList<InterviewMessage> Messages(
        SqliteConnection db, SqliteTransaction? tx, string taskId)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT m.sequence,m.role,m.content,m.created_utc,c.kind,c.options_json
            FROM interview_messages AS m
            LEFT JOIN interview_message_choices AS c
              ON c.task_id=m.task_id AND c.sequence=m.sequence
            WHERE m.task_id=@task ORDER BY m.sequence
            """, ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        var messages = new List<InterviewMessage>();
        while (reader.Read())
        {
            var choice = reader.IsDBNull(4) ? null : ValidChoice(new InterviewChoice(
                reader.GetString(4),
                JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? []));
            messages.Add(new InterviewMessage(reader.GetInt32(0),
                reader.GetString(1), reader.GetString(2), reader.GetString(3),
                choice));
        }
        return messages;
    }

    private static InterviewSpecification? LastSpec(
        SqliteConnection db, SqliteTransaction? tx, string taskId)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT version,model,content,sha256,status,created_utc,approved_utc
            FROM interview_specs WHERE task_id=@task
            ORDER BY version DESC LIMIT 1
            """, ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        return reader.Read()
            ? new InterviewSpecification(reader.GetInt32(0),
                reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6))
            : null;
    }

    private static (string Model, bool Ready)? Session(
        SqliteConnection db, SqliteTransaction? tx, string taskId)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT model,ready FROM interview_sessions WHERE task_id=@task
            """, ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetInt32(1) != 0)
                             : null;
    }

    public InterviewSnapshot Read(string taskId)
    {
        ValidateId(taskId);
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        var messages = Messages(db, null, taskId);
        var session = Session(db, null, taskId);
        return new InterviewSnapshot(taskId, session?.Model,
            messages.Count == 0 ? 0 : messages[^1].Sequence,
            session?.Ready ?? false, messages, LastSpec(db, null, taskId));
    }

    public InterviewSnapshot CommitTurn(
        string taskId, InterviewTurnRequest input, InterviewReply reply)
    {
        ValidateId(taskId);
        var assistantText = ValidAssistantMessage(reply.Message);
        var choice = ValidChoice(reply.Choice);
        var ownerMessage = input.ExpectedSequence == 0 ? null :
            ValidOwnerMessage(input.Message);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var messages = Messages(db, tx, taskId);
        var existing = Session(db, tx, taskId);
        var current = messages.Count == 0 ? 0 : messages[^1].Sequence;
        if (input.ExpectedSequence != current ||
            (current == 0 && ownerMessage is not null) ||
            (current > 0 && (ownerMessage is null ||
                messages[^1].Role != "assistant")) ||
            messages.Count(x => x.Role == "owner") >= MaxOwnerAnswers)
            throw new StoreFailure("STALE_INTERVIEW");
        var model = existing?.Model ?? input.Model;
        if (!LocalInterviewModel.AllowedModel(model) ||
            existing is not null && input.Model is not null &&
                existing.Value.Model != input.Model)
            throw new StoreFailure("INVALID_MODEL");
        var utc = DateTimeOffset.UtcNow.ToString("O");
        if (existing is null)
            TaskStore.Execute(db, tx, """
                INSERT INTO interview_sessions(task_id,model,ready)
                VALUES(@task,@model,0)
                """, ("@task", taskId), ("@model", model!));
        if (ownerMessage is not null)
            TaskStore.Execute(db, tx, """
                INSERT INTO interview_messages(task_id,sequence,role,content,created_utc)
                VALUES(@task,@sequence,'owner',@content,@utc)
                """, ("@task", taskId), ("@sequence", current + 1),
                ("@content", ownerMessage), ("@utc", utc));
        var next = current + (ownerMessage is null ? 1 : 2);
        TaskStore.Execute(db, tx, """
            INSERT INTO interview_messages(task_id,sequence,role,content,created_utc)
            VALUES(@task,@sequence,'assistant',@content,@utc)
            """, ("@task", taskId), ("@sequence", next),
            ("@content", assistantText), ("@utc", utc));
        if (choice is not null)
            TaskStore.Execute(db, tx, """
                INSERT INTO interview_message_choices(task_id,sequence,kind,options_json)
                VALUES(@task,@sequence,@kind,@options)
                """, ("@task", taskId), ("@sequence", next),
                ("@kind", choice.Kind),
                ("@options", JsonSerializer.Serialize(choice.Options)));
        var ready = reply.ReadyForSpecification ||
            messages.Count(x => x.Role == "owner") +
                (ownerMessage is null ? 0 : 1) >= MaxOwnerAnswers;
        TaskStore.Execute(db, tx, """
            UPDATE interview_sessions SET ready=@ready WHERE task_id=@task
            """, ("@task", taskId), ("@ready", ready ? 1 : 0));
        // Any subsequent answer invalidates prior drafted/approved text.
        if (ownerMessage is not null)
            TaskStore.Execute(db, tx, """
                UPDATE interview_specs SET status='Superseded',
                    approved_utc=NULL
                WHERE task_id=@task AND status IN ('Draft','Approved')
                """, ("@task", taskId));
        tx.Commit();
        return Read(taskId);
    }

    public InterviewSnapshot CommitSpecification(
        string taskId, int expectedSequence, string content)
    {
        ValidateId(taskId);
        var specification = ValidSpecification(content);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var messages = Messages(db, tx, taskId);
        var current = messages.Count == 0 ? 0 : messages[^1].Sequence;
        var session = Session(db, tx, taskId);
        if (current != expectedSequence || session is null ||
            !messages.Any(x => x.Role == "owner"))
            throw new StoreFailure("STALE_INTERVIEW");
        var prior = LastSpec(db, tx, taskId);
        var version = (prior?.Version ?? 0) + 1;
        var sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"codingassistant-spec-v1\n{taskId}\n{version}\n{specification}")))
            .ToLowerInvariant();
        TaskStore.Execute(db, tx, """
            UPDATE interview_specs SET status='Superseded',approved_utc=NULL
            WHERE task_id=@task AND status IN ('Draft','Approved')
            """, ("@task", taskId));
        TaskStore.Execute(db, tx, """
            INSERT INTO interview_specs(
                task_id,version,model,content,sha256,status,created_utc)
            VALUES(@task,@version,@model,@content,@sha,'Draft',@utc)
            """, ("@task", taskId), ("@version", version),
            ("@model", session.Value.Model),
            ("@content", specification), ("@sha", sha),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
        return Read(taskId);
    }

    public InterviewSnapshot Approve(string taskId, ApproveSpecificationRequest input)
    {
        ValidateId(taskId);
        if (input.Version < 1 || input.Sha256 is null ||
            !HashPattern.IsMatch(input.Sha256))
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var spec = LastSpec(db, tx, taskId) ??
            throw new StoreFailure("SPECIFICATION_NOT_FOUND");
        if (spec.Version != input.Version || spec.Sha256 != input.Sha256 ||
            spec.Status is not ("Draft" or "Approved"))
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        if (spec.Status == "Draft")
            TaskStore.Execute(db, tx, """
                UPDATE interview_specs SET status='Approved',approved_utc=@utc
                WHERE task_id=@task AND version=@version AND status='Draft'
                """, ("@task", taskId), ("@version", input.Version),
                ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
        return Read(taskId);
    }
}
