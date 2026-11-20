using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// A preview build generates SOURCE TEXT only. It does not invoke agents,
// compilers, package managers or model-supplied commands. Its output is NOT
// evaluated or approved for execution; all files remain in owner-private DB.
public sealed record PlannedProjectFile(string Path, string Purpose);
public sealed record ProjectDraftFile(
    string Path, string Purpose, string Content, string Sha256, string CreatedUtc);
public sealed record ProjectDraftEvent(
    int Sequence, string Kind, string Detail, string CreatedUtc);
public sealed record ProjectDraftJob(
    string Id, string TaskId, int SpecificationVersion, string SpecificationSha256,
    string Model, string State, string Phase, string? ErrorCode,
    string CreatedUtc, string UpdatedUtc);
public sealed record ProjectDraftSnapshot(
    ProjectDraftJob Job, IReadOnlyList<ProjectDraftEvent> Events,
    IReadOnlyList<ProjectDraftFile> Files,
    DraftArchitectureContract? Architecture = null);
public sealed record StartProjectDraftRequest(int SpecificationVersion,
    string? SpecificationSha256, string? ArchitectureSha256 = null);

public interface IProjectDraftModel
{
    Task<IReadOnlyList<PlannedProjectFile>> PlanAsync(
        string objective, string approvedSpecification, string model,
        CancellationToken cancellation);
    Task<string> WriteAsync(
        string objective, string approvedSpecification, string model,
        PlannedProjectFile file, IReadOnlyList<ProjectDraftFile> previous,
        CancellationToken cancellation);
}

public interface IStreamingProjectDraftModel : IProjectDraftModel
{
    Task<IReadOnlyList<PlannedProjectFile>> PlanWithProgressAsync(
        string objective, string approvedSpecification, string model,
        Func<Task> onOutputChunkBatch, CancellationToken cancellation);
    Task<string> WriteWithProgressAsync(
        string objective, string approvedSpecification, string model,
        PlannedProjectFile file, IReadOnlyList<ProjectDraftFile> previous,
        Func<Task> onOutputChunkBatch, CancellationToken cancellation);
}

public sealed class ProjectDraftStore(TaskStore tasks)
{
    private static readonly Regex Id = new(
        "^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha = new(
        "^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Segment = new(
        "^[A-Za-z0-9][A-Za-z0-9_.-]{0,70}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> Extensions = new(
        StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".ts", ".tsx", ".js", ".jsx", ".css",
        ".html", ".json", ".md", ".txt", ".py", ".sln", ".yml", ".yaml"
    };
    private static readonly HashSet<string> Events = new(
        StringComparer.Ordinal)
    {
        "BuildAccepted", "ModelPlanning", "PlanAccepted",
        "FileStarted", "FileSaved", "StaticChecking", "ModelOutputReceived",
        "DraftGenerated", "DraftFailed", "DraftInterrupted", "ArchitectureBound"
    };
    public const int MaxFiles = 5;
    public const int MaxFileCharacters = 16000;
    public const int MaxTotalCharacters = 60000;

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS project_drafts (
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                spec_version INTEGER NOT NULL,
                spec_sha256 TEXT NOT NULL,
                model TEXT NOT NULL,
                state TEXT NOT NULL CHECK(state IN
                    ('Running','Generated','Failed','Interrupted')),
                phase TEXT NOT NULL,
                error_code TEXT,
                created_utc TEXT NOT NULL,
                updated_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS project_draft_active
                ON project_drafts(task_id) WHERE state='Running';
            CREATE INDEX IF NOT EXISTS project_draft_recent
                ON project_drafts(task_id,created_utc DESC,id DESC);
            CREATE TABLE IF NOT EXISTS project_draft_events (
                draft_id TEXT NOT NULL REFERENCES project_drafts(id),
                sequence INTEGER NOT NULL CHECK(sequence > 0),
                kind TEXT NOT NULL,
                detail TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(draft_id,sequence)
            );
            CREATE TABLE IF NOT EXISTS project_draft_architecture (
                draft_id TEXT PRIMARY KEY NOT NULL REFERENCES project_drafts(id),
                contract_sha256 TEXT NOT NULL,
                contract_json TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS project_draft_files (
                draft_id TEXT NOT NULL REFERENCES project_drafts(id),
                path TEXT NOT NULL,
                purpose TEXT NOT NULL,
                content TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(draft_id,path)
            );
            """);
    }

    public static string SafePath(string? path)
    {
        if (path is null || path.Length is < 3 or > 140 ||
            path.StartsWith('/') || path.Contains('\\') ||
            path.Contains(':') || path.Contains("//"))
            throw new StoreFailure("INVALID_PROJECT_PATH");
        var parts = path.Split('/');
        if (parts.Length > 4 || parts.Any(part =>
            !Segment.IsMatch(part) || part is "." or ".." ||
            part.StartsWith('.') || part.EndsWith('.') ||
            part.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
            part.Equals("artifacts", StringComparison.OrdinalIgnoreCase)))
            throw new StoreFailure("INVALID_PROJECT_PATH");
        if (!Extensions.Contains(System.IO.Path.GetExtension(path)))
            throw new StoreFailure("INVALID_PROJECT_PATH");
        return path;
    }

    public static IReadOnlyList<PlannedProjectFile> ValidatePlan(
        IReadOnlyList<PlannedProjectFile>? plan)
    {
        if (plan is null || plan.Count is < 1 or > MaxFiles)
            throw new StoreFailure("INVALID_PROJECT_PLAN");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var accepted = new List<PlannedProjectFile>();
        foreach (var file in plan)
        {
            var path = SafePath(file.Path);
            var purpose = file.Purpose?.Trim();
            if (purpose is null || purpose.Length is < 5 or > 240 ||
                !paths.Add(path) ||
                purpose.Any(ch => char.IsControl(ch) &&
                    ch is not ('\r' or '\n' or '\t')))
                throw new StoreFailure("INVALID_PROJECT_PLAN");
            accepted.Add(new PlannedProjectFile(path, purpose));
        }
        return accepted;
    }

    public static string ValidateSource(string path, string? source)
    {
        _ = SafePath(path);
        if (source is null || source.Length is < 4 or > MaxFileCharacters ||
            source.IndexOf('\0') >= 0)
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        var value = source.Trim('\r', '\n');
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = value.IndexOf('\n');
            if (newline >= 0 && value.EndsWith("```", StringComparison.Ordinal))
                value = value[(newline + 1)..^3].Trim('\r', '\n');
        }
        if (value.Length is < 4 or > MaxFileCharacters)
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try { using var _ = JsonDocument.Parse(value); }
            catch (JsonException) { throw new StoreFailure("STATIC_VALIDATION_FAILED"); }
        }
        return value + "\n";
    }

    private static void ValidateId(string id)
    {
        if (!Id.IsMatch(id)) throw new StoreFailure("INVALID_ID");
    }

    public void MarkInterruptedOnStartup()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            UPDATE project_drafts SET state='Interrupted',
                phase='RestartInterrupted',error_code='PROCESS_INTERRUPTED',
                updated_utc=@utc WHERE state='Running'
            """, ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        // No automatic replay or approval of old draft jobs.
    }

    public ProjectDraftJob Start(
        string taskId, int specVersion, string specSha, string model,
        string? architectureSha256 = null)
    {
        ValidateId(taskId);
        if (specVersion < 1 || specSha is null || !Sha.IsMatch(specSha) ||
            !LocalInterviewModel.AllowedModel(model))
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT sha256 FROM interview_specs
            WHERE task_id=@task AND version=@version AND status='Approved'
            """, ("@task", taskId), ("@version", specVersion)))
        {
            var approved = cmd.ExecuteScalar()?.ToString();
            if (approved != specSha)
                throw new StoreFailure("SPECIFICATION_CONFLICT");
        }
        // Architecture capture and job admission share one SQLite transaction.
        // Any proposal/review concurrent with this transaction is serialized.
        var architecture = DraftArchitecturePolicy.Capture(
            db, tx, taskId, specVersion, specSha);
        if (architecture?.Sha256 != architectureSha256)
            throw new StoreFailure("ARCHITECTURE_CONFLICT");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var id = Guid.NewGuid().ToString("N");
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO project_drafts(
                    id,task_id,spec_version,spec_sha256,model,state,phase,
                    created_utc,updated_utc)
                VALUES(@id,@task,@version,@sha,@model,'Running','Queued',@utc,@utc)
                """, ("@id", id), ("@task", taskId),
                ("@version", specVersion), ("@sha", specSha),
                ("@model", model), ("@utc", now));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new StoreFailure("BUILD_BUSY");
        }
        TaskStore.Execute(db, tx, """
            INSERT INTO project_draft_events(
                draft_id,sequence,kind,detail,created_utc)
            VALUES(@id,1,'BuildAccepted',@detail,@utc)
            """, ("@id", id), ("@detail",
                architecture is null
                    ? "Legacy spec-only draft; no architecture decisions approved"
                    : "Approved spec and architecture bound; no code execution"),
            ("@utc", now));
        if (architecture is not null)
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO project_draft_architecture(
                    draft_id,contract_sha256,contract_json)
                VALUES(@id,@hash,@json)
                """, ("@id", id), ("@hash", architecture.Sha256),
                ("@json", JsonSerializer.Serialize(architecture)));
            TaskStore.Execute(db, tx, """
                INSERT INTO project_draft_events(
                    draft_id,sequence,kind,detail,created_utc)
                VALUES(@id,2,'ArchitectureBound',@detail,@utc)
                """, ("@id", id),
                ("@detail", "Owner-approved architecture SHA-256 " + architecture.Sha256),
                ("@utc", now));
        }
        tx.Commit();
        return Get(taskId, id).Job;
    }

    private static void RequireApprovedSpec(
        SqliteConnection db, SqliteTransaction tx, ProjectDraftJob job)
    {
        // This check runs in the SAME SQLite transaction as file persistence
        // and final completion. A revised interview invalidates the build;
        // previously approved text cannot be silently resurrected.
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT version,sha256,status FROM interview_specs
            WHERE task_id=@task ORDER BY version DESC LIMIT 1
            """, ("@task", job.TaskId))
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read() ||
                reader.GetInt32(0) != job.SpecificationVersion ||
                reader.GetString(1) != job.SpecificationSha256 ||
                reader.GetString(2) != "Approved")
                throw new StoreFailure("SPECIFICATION_CONFLICT");
        }
        var hash = TaskStore.Scalar(db, tx, """
            SELECT contract_sha256 FROM project_draft_architecture
            WHERE draft_id=@id
            """, ("@id", job.Id));
        DraftArchitecturePolicy.AssertCurrent(db, tx, job.TaskId,
            job.SpecificationVersion, job.SpecificationSha256, hash);
    }

    private static ProjectDraftJob ReadJob(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8), reader.GetString(9));

    private static ProjectDraftJob? Find(SqliteConnection db,
        SqliteTransaction? tx, string taskId, string id)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT id,task_id,spec_version,spec_sha256,model,state,phase,
                   error_code,created_utc,updated_utc
            FROM project_drafts WHERE task_id=@task AND id=@id
            """, ("@task", taskId), ("@id", id));
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadJob(reader) : null;
    }

    private static DraftArchitectureContract? ReadArchitecture(
        SqliteConnection db, SqliteTransaction? tx, string draftId)
    {
        var json = TaskStore.Scalar(db, tx, """
            SELECT contract_json FROM project_draft_architecture
            WHERE draft_id=@id
            """, ("@id", draftId));
        return json is null
            ? null : JsonSerializer.Deserialize<DraftArchitectureContract>(json);
    }

    public DraftArchitectureContract? GetArchitecture(string taskId, string id)
    {
        ValidateId(taskId);
        ValidateId(id);
        using var db = tasks.Open();
        _ = Find(db, null, taskId, id) ?? throw new StoreFailure("NOT_FOUND");
        return ReadArchitecture(db, null, id);
    }

    public void RequireCurrentArchitecture(string taskId, string id)
    {
        ValidateId(taskId);
        ValidateId(id);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, taskId, id) ?? throw new StoreFailure("NOT_FOUND");
        RequireApprovedSpec(db, tx, job);
        tx.Commit();
    }

    public ProjectDraftSnapshot? Latest(string taskId)
    {
        ValidateId(taskId);
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        using var cmd = TaskStore.Command(db, null, """
            SELECT id FROM project_drafts WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        var id = cmd.ExecuteScalar()?.ToString();
        return id is null ? null : Get(taskId, id);
    }

    public ProjectDraftSnapshot Get(string taskId, string id)
    {
        ValidateId(taskId);
        ValidateId(id);
        using var db = tasks.Open();
        var job = Find(db, null, taskId, id) ??
            throw new StoreFailure("NOT_FOUND");
        using var eventCmd = TaskStore.Command(db, null, """
            SELECT sequence,kind,detail,created_utc FROM project_draft_events
            WHERE draft_id=@id ORDER BY sequence
            """, ("@id", id));
        var events = new List<ProjectDraftEvent>();
        using (var reader = eventCmd.ExecuteReader())
            while (reader.Read())
                events.Add(new ProjectDraftEvent(
                    reader.GetInt32(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3)));
        using var fileCmd = TaskStore.Command(db, null, """
            SELECT path,purpose,content,sha256,created_utc
            FROM project_draft_files WHERE draft_id=@id ORDER BY path
            """, ("@id", id));
        var files = new List<ProjectDraftFile>();
        using (var reader = fileCmd.ExecuteReader())
            while (reader.Read())
                files.Add(new ProjectDraftFile(
                    reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3),
                    reader.GetString(4)));
        return new ProjectDraftSnapshot(job, events, files,
            ReadArchitecture(db, null, id));
    }

    public void Note(string taskId, string id, string kind, string detail)
    {
        if (!Events.Contains(kind) || string.IsNullOrWhiteSpace(detail) ||
            detail.Length > 160 || detail.Any(ch => char.IsControl(ch)))
            throw new StoreFailure("INVALID_BUILD_EVENT");
        Transition(taskId, id, kind, detail, null, null);
    }

    public void NoteModelProgress(string taskId, string id)
    {
        ValidateId(taskId);
        ValidateId(id);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, taskId, id) ??
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
        {
            tx.Commit();
            return;
        }
        var count = TaskStore.Scalar(db, tx, """
            SELECT COUNT(*) FROM project_draft_events
            WHERE draft_id=@id AND kind='ModelOutputReceived'
            """, ("@id", id));
        if (int.TryParse(count, out var n) && n < 50)
        {
            var seq = Convert.ToInt32(TaskStore.Scalar(db, tx, """
                SELECT COALESCE(MAX(sequence),0)+1 FROM project_draft_events
                WHERE draft_id=@id
                """, ("@id", id)) ?? "1");
            TaskStore.Execute(db, tx, """
                INSERT INTO project_draft_events(
                    draft_id,sequence,kind,detail,created_utc)
                VALUES(@id,@seq,'ModelOutputReceived',
                       'More output arrived from local Ollama (content withheld)',@utc)
                """, ("@id", id), ("@seq", seq),
                ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        }
        tx.Commit();
    }

    public void Finish(string taskId, string id, bool success, string? reason)
    {
        var detail = success ? "Source files generated; no tests executed" :
            "Draft generation stopped without execution";
        if (!success && (reason is null ||
            !Regex.IsMatch(reason, "^[A-Z_]{3,60}$")))
            reason = "GENERATION_FAILED";
        Transition(taskId, id,
            success ? "DraftGenerated" : "DraftFailed", detail,
            success ? "Generated" : "Failed", reason);
    }

    public void Interrupt(string taskId, string id)
    {
        Transition(taskId, id, "DraftInterrupted",
            "The local job was interrupted; nothing will auto-retry",
            "Interrupted", "PROCESS_INTERRUPTED");
    }

    private void Transition(string taskId, string id, string kind,
        string detail, string? finalState, string? reason)
    {
        ValidateId(taskId);
        ValidateId(id);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, taskId, id) ??
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
        {
            tx.Commit();
            return;
        }
        if (finalState == "Generated")
        {
            try { RequireApprovedSpec(db, tx, job); }
            catch (StoreFailure error) when (error.Code is
                "SPECIFICATION_CONFLICT" or "ARCHITECTURE_CONFLICT")
            {
                finalState = "Failed";
                kind = "DraftFailed";
                detail = "Approved inputs changed; stale output is not a completed build";
                reason = error.Code;
            }
        }
        var now = DateTimeOffset.UtcNow.ToString("O");
        var sequence = Convert.ToInt32(TaskStore.Scalar(db, tx, """
            SELECT COALESCE(MAX(sequence),0)+1 FROM project_draft_events
            WHERE draft_id=@id
            """, ("@id", id)) ?? "1");
        TaskStore.Execute(db, tx, """
            INSERT INTO project_draft_events(
                draft_id,sequence,kind,detail,created_utc)
            VALUES(@id,@seq,@kind,@detail,@utc)
            """, ("@id", id), ("@seq", sequence),
            ("@kind", kind), ("@detail", detail), ("@utc", now));
        TaskStore.Execute(db, tx, """
            UPDATE project_drafts SET state=@state,phase=@phase,
                error_code=@reason,updated_utc=@utc WHERE id=@id
            """, ("@id", id), ("@state", finalState ?? "Running"),
            ("@phase", kind), ("@reason", (object?)reason ?? DBNull.Value),
            ("@utc", now));
        tx.Commit();
    }

    public void SaveFile(string taskId, string id, PlannedProjectFile file,
                         string source)
    {
        var path = SafePath(file.Path);
        var content = ValidateSource(path, source);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var job = Find(db, tx, taskId, id) ??
            throw new StoreFailure("NOT_FOUND");
        if (job.State != "Running")
            throw new StoreFailure("INVALID_STATE");
        RequireApprovedSpec(db, tx, job);
        DraftArchitecturePolicy.ValidateSourcePath(
            ReadArchitecture(db, tx, id), path);
        var files = TaskStore.Scalar(db, tx, """
            SELECT COUNT(*) FROM project_draft_files WHERE draft_id=@id
            """, ("@id", id));
        var chars = TaskStore.Scalar(db, tx, """
            SELECT COALESCE(SUM(LENGTH(content)),0)
            FROM project_draft_files WHERE draft_id=@id
            """, ("@id", id));
        if (int.Parse(files ?? "0") >= MaxFiles ||
            int.Parse(chars ?? "0") + content.Length > MaxTotalCharacters)
            throw new StoreFailure("PROJECT_SIZE_LIMIT");
        var now = DateTimeOffset.UtcNow.ToString("O");
        var sha = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
        TaskStore.Execute(db, tx, """
            INSERT INTO project_draft_files(
                draft_id,path,purpose,content,sha256,created_utc)
            VALUES(@id,@path,@purpose,@content,@sha,@utc)
            """, ("@id", id), ("@path", path), ("@purpose", file.Purpose),
            ("@content", content), ("@sha", sha), ("@utc", now));
        tx.Commit();
        Note(taskId, id, "FileSaved", path + " saved (" +
             Encoding.UTF8.GetByteCount(content) + " bytes)");
    }

    public byte[] Zip(string taskId, string id)
    {
        var snapshot = Get(taskId, id);
        if (snapshot.Job.State != "Generated" ||
            snapshot.Files.Count is < 1 or > MaxFiles)
            throw new StoreFailure("BUILD_NOT_COMPLETE");
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            foreach (var file in snapshot.Files)
            {
                var entry = archive.CreateEntry(SafePath(file.Path),
                    CompressionLevel.Optimal);
                using var stream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(file.Content);
                stream.Write(bytes);
            }
        }
        return buffer.ToArray();
    }
}
