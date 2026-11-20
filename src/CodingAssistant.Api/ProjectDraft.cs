using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

/// <summary>
/// Specification-driven source proposal, NOT qualified agent execution.
/// A local model proposes arbitrary named source files as TEXT; the host stores
/// them privately and never executes the generated content or writes it into
/// a real project. The approved interview spec is bound to every draft.
/// </summary>
public sealed record ProjectDraftFile(string Path, string Content, string Sha256);
public sealed record ProjectDraftEvent(int Sequence, string Kind, string CreatedUtc);
public sealed record ProjectDraftView(
    string Id, string TaskId, int SpecificationVersion, string SpecificationHash,
    string Model, string State, string? ErrorCode, string CreatedUtc,
    IReadOnlyList<ProjectDraftEvent> Events,
    IReadOnlyList<ProjectDraftFile> Files);
public sealed record StartProjectDraftRequest(int SpecificationVersion, string? SpecificationHash);

public interface IProjectDraftModel
{
    Task<IReadOnlyList<ProjectDraftFile>> GenerateAsync(
        string objective, string approvedSpecification, string model,
        CancellationToken cancellation);
}

public static class ProjectDraftValidation
{
    private static readonly Regex SafeName = new(
        @"^[A-Za-z0-9_-][A-Za-z0-9_./-]{0,119}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> Extensions = new(
        [".cs", ".csproj", ".sln", ".slnx", ".ts", ".tsx", ".js", ".jsx",
         ".css", ".html", ".json", ".md", ".txt", ".py", ".go", ".rs",
         ".sql", ".yml", ".yaml"], StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ProjectDraftFile> Check(IReadOnlyList<ProjectDraftFile> files)
    {
        if (files.Count is < 1 or > 8)
            throw new StoreFailure("DRAFT_OUTPUT_REJECTED");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ProjectDraftFile>();
        var total = 0;
        foreach (var file in files)
        {
            if (file.Path is null || !SafeName.IsMatch(file.Path) ||
                file.Path.StartsWith('/') || file.Path.Contains('\\') ||
                file.Path.Split('/').Any(p => p is "" or "." or ".." ||
                                            p.StartsWith('.')) ||
                file.Path.Split('/').Length > 5 ||
                file.Path.Contains(':') ||
                !Extensions.Contains(Path.GetExtension(file.Path)) ||
                !seen.Add(file.Path) ||
                file.Content is null ||
                file.Content.Length is < 1 or > 12000 ||
                file.Content.Any(c => c == '\0' ||
                     (char.IsControl(c) && c is not ('\n' or '\r' or '\t'))))
                throw new StoreFailure("DRAFT_OUTPUT_REJECTED");
            total += Encoding.UTF8.GetByteCount(file.Content);
            if (total > 48000)
                throw new StoreFailure("DRAFT_OUTPUT_REJECTED");
            result.Add(new ProjectDraftFile(
                file.Path, file.Content,
                Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(file.Content))).ToLowerInvariant()));
        }
        return result;
    }

    public static byte[] ToZip(ProjectDraftView draft)
    {
        if (draft.State != "Ready")
            throw new StoreFailure("DRAFT_NOT_READY");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in Check(draft.Files))
            {
                var entry = zip.CreateEntry(file.Path, CompressionLevel.Fastest);
                using var stream = entry.Open();
                var source = Encoding.UTF8.GetBytes(file.Content);
                stream.Write(source);
            }
        return buffer.ToArray();
    }
}

public interface IProjectDraftStream : IProjectDraftModel
{
    Task<IReadOnlyList<ProjectDraftFile>> GenerateStreamingAsync(
        string objective, string approvedSpecification, string model,
        Func<Task> onVerifiedChunk, CancellationToken cancellation);
}

public sealed class LocalProjectDraftModel : IProjectDraftStream
{
    public Task<IReadOnlyList<ProjectDraftFile>> GenerateAsync(
        string objective, string approvedSpecification, string model,
        CancellationToken cancellation) =>
        GenerateStreamingAsync(objective, approvedSpecification, model,
            () => Task.CompletedTask, cancellation);

    public async Task<IReadOnlyList<ProjectDraftFile>> GenerateStreamingAsync(
        string objective, string approvedSpecification, string model,
        Func<Task> onVerifiedChunk, CancellationToken cancellation)
    {
        if (!LocalInterviewModel.AllowedModel(model) ||
            objective.Length is < 1 or > 4000 ||
            approvedSpecification.Length is < 180 or > 16000)
            throw new StoreFailure("INVALID_INPUT");
        using var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromMinutes(10)
        };
        var payload = new
        {
            model, stream = true, think = false, format = "json",
            messages = new object[]
            {
                new { role = "system", content = """
                    You are proposing a SMALL, concrete software project from
                    an owner-APPROVED specification. This is a source drafting
                    task, not an instruction to execute code or invoke tools.
                    Return ONLY JSON, exactly shaped:
                    {"files":[{"path":"README.md","content":"..."}]}
                    Produce 1-8 coherent real source files, 12000 characters per
                    file maximum, total under 48000 UTF-8 bytes. Choose file
                    names, directories, programming languages and content
                    FROM THE SPECIFICATION; do not use a fixed template.
                    Include a README.md with intended build/test commands and
                    any omissions. Never invent verification results or claim
                    commands were run. Do not include secrets, credentials,
                    remote resource references, binary data or hidden paths.
                    This output will be shown for OWNER REVIEW only.
                    If requirements exceed this first source-draft budget,
                    make a coherent smallest implementable vertical slice and
                    document deferred functionality in README.md.
                    """.ToString() },
                new { role = "user", content =
                    "OWNER OBJECTIVE:\n" + objective +
                    "\n\nAPPROVED SPECIFICATION:\n" + approvedSpecification }
            },
            options = new { temperature = 0.1, num_predict = 5500, num_ctx = 8192 }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload),
                                        Encoding.UTF8, "application/json")
        };
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new StoreFailure("MODEL_NOT_INSTALLED");
            if (!response.IsSuccessStatusCode)
                throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
            await using var responseStream =
                await response.Content.ReadAsStreamAsync(cancellation);
            using var reader = new StreamReader(responseStream, Encoding.UTF8);
            var raw = new StringBuilder();
            var chunks = 0;
            var finished = false;
            string? line;
            while ((line = await reader.ReadLineAsync(cancellation)) is not null)
            {
                if (line.Length > 20000)
                    throw new StoreFailure("DRAFT_OUTPUT_REJECTED");
                if (line.Length == 0) continue;
                using var update = JsonDocument.Parse(line);
                var root = update.RootElement;
                if (root.TryGetProperty("error", out _))
                    throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
                if (root.TryGetProperty("message", out var message) &&
                    message.TryGetProperty("content", out var part))
                {
                    var text = part.GetString();
                    if (text is not null)
                    {
                        raw.Append(text);
                        if (raw.Length > 100000)
                            throw new StoreFailure("DRAFT_OUTPUT_REJECTED");
                        ++chunks;
                        // Only actual received local-model chunks produce an
                        // event, at a bounded frequency. Never store raw text
                        // or claim a proposed file has compiled.
                        if (chunks % 70 == 0 && chunks <= 4200)
                            await onVerifiedChunk();
                    }
                }
                if (root.TryGetProperty("done", out var done) &&
                    done.ValueKind == JsonValueKind.True)
                {
                    finished = true;
                    break;
                }
            }
            if (!finished)
                throw new StoreFailure("DRAFT_OUTPUT_REJECTED");
            using var proposed = JsonDocument.Parse(raw.ToString());
            var files = proposed.RootElement.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array ||
                files.GetArrayLength() > 8)
                throw new StoreFailure("DRAFT_OUTPUT_REJECTED");
            var list = new List<ProjectDraftFile>();
            foreach (var file in files.EnumerateArray())
                list.Add(new ProjectDraftFile(
                    file.GetProperty("path").GetString() ?? "",
                    file.GetProperty("content").GetString() ?? "", ""));
            return ProjectDraftValidation.Check(list);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
                                   or InvalidOperationException)
        {
            throw new StoreFailure("DRAFT_OUTPUT_REJECTED");
        }
    }
}

public sealed class ProjectDraftStore(TaskStore tasks, InterviewStore interviews)
{
    private static readonly Regex Id = new(
        "^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Hash = new(
        "^[0-9a-f]{64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS project_drafts(
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                spec_version INTEGER NOT NULL,
                spec_hash TEXT NOT NULL,
                model TEXT NOT NULL,
                state TEXT NOT NULL,
                error_code TEXT,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS project_draft_events(
                draft_id TEXT NOT NULL REFERENCES project_drafts(id),
                sequence INTEGER NOT NULL,
                kind TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                PRIMARY KEY(draft_id,sequence)
            );
            CREATE TABLE IF NOT EXISTS project_draft_files(
                draft_id TEXT NOT NULL REFERENCES project_drafts(id),
                path TEXT NOT NULL,
                content TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                PRIMARY KEY(draft_id,path)
            );
            CREATE UNIQUE INDEX IF NOT EXISTS draft_active_task
              ON project_drafts(task_id)
              WHERE state IN ('Queued','Generating','Validating');
            """);
    }

    public void MarkInterrupted()
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        using var cmd = TaskStore.Command(db, tx, """
            SELECT id FROM project_drafts
            WHERE state IN ('Queued','Generating','Validating')
            """);
        var ids = new List<string>();
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) ids.Add(reader.GetString(0));
        foreach (var id in ids)
        {
            TaskStore.Execute(db, tx,
                "UPDATE project_drafts SET state='Interrupted', error_code='HOST_RESTARTED' WHERE id=@id",
                ("@id", id));
            Append(db, tx, id, "Interrupted");
        }
        tx.Commit();
    }

    private static void Append(
        SqliteConnection db, SqliteTransaction tx, string draftId, string kind)
    {
        TaskStore.Execute(db, tx, """
            INSERT INTO project_draft_events(draft_id,sequence,kind,created_utc)
            VALUES(@id, (SELECT COALESCE(MAX(sequence),0)+1
                         FROM project_draft_events WHERE draft_id=@id),
                   @kind,@utc)
            """, ("@id", draftId), ("@kind", kind),
            ("@utc", DateTimeOffset.UtcNow.ToString("O")));
    }

    public ProjectDraftView Create(string taskId, StartProjectDraftRequest request)
    {
        if (!Id.IsMatch(taskId) || request.SpecificationVersion < 1 ||
            request.SpecificationHash is null || !Hash.IsMatch(request.SpecificationHash))
            throw new StoreFailure("INVALID_INPUT");
        var snapshot = interviews.Read(taskId);
        var spec = snapshot.Specification;
        if (spec is null || spec.Status != "Approved" ||
            spec.Version != request.SpecificationVersion ||
            spec.Sha256 != request.SpecificationHash)
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var id = Guid.NewGuid().ToString("N");
        try
        {
            TaskStore.Execute(db, tx, """
                INSERT INTO project_drafts(
                    id,task_id,spec_version,spec_hash,model,state,created_utc)
                VALUES(@id,@task,@version,@hash,@model,'Queued',@utc)
                """, ("@id", id), ("@task", taskId),
                ("@version", spec.Version), ("@hash", spec.Sha256),
                ("@model", spec.Model),
                ("@utc", DateTimeOffset.UtcNow.ToString("O")));
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new StoreFailure("BUILD_BUSY");
        }
        Append(db, tx, id, "Queued");
        tx.Commit();
        return Read(id);
    }

    public ProjectDraftView Read(string draftId)
    {
        if (!Id.IsMatch(draftId)) throw new StoreFailure("INVALID_ID");
        using var db = tasks.Open();
        using var cmd = TaskStore.Command(db, null, """
            SELECT id,task_id,spec_version,spec_hash,model,state,error_code,created_utc
            FROM project_drafts WHERE id=@id
            """, ("@id", draftId));
        string id, task, hash, model, state, created;
        int version;
        string? error;
        using (var reader = cmd.ExecuteReader())
        {
            if (!reader.Read()) throw new StoreFailure("NOT_FOUND");
            id = reader.GetString(0); task = reader.GetString(1);
            version = reader.GetInt32(2); hash = reader.GetString(3);
            model = reader.GetString(4); state = reader.GetString(5);
            error = reader.IsDBNull(6) ? null : reader.GetString(6);
            created = reader.GetString(7);
        }
        var events = new List<ProjectDraftEvent>();
        using (var ec = TaskStore.Command(db, null, """
            SELECT sequence,kind,created_utc
            FROM project_draft_events WHERE draft_id=@id ORDER BY sequence
            """, ("@id", draftId)))
        using (var er = ec.ExecuteReader())
            while (er.Read())
                events.Add(new ProjectDraftEvent(
                    er.GetInt32(0), er.GetString(1), er.GetString(2)));
        var files = new List<ProjectDraftFile>();
        using (var fc = TaskStore.Command(db, null, """
            SELECT path,content,sha256
            FROM project_draft_files WHERE draft_id=@id ORDER BY path
            """, ("@id", draftId)))
        using (var fr = fc.ExecuteReader())
            while (fr.Read())
                files.Add(new ProjectDraftFile(
                    fr.GetString(0), fr.GetString(1), fr.GetString(2)));
        return new ProjectDraftView(id, task, version, hash, model,
            state, error, created, events, files);
    }

    public ProjectDraftView? LatestForTask(string taskId)
    {
        if (!Id.IsMatch(taskId)) throw new StoreFailure("INVALID_ID");
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        var id = TaskStore.Scalar(db, null, """
            SELECT id FROM project_drafts
            WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        return id is null ? null : Read(id);
    }

    public void RecordModelCheckpoint(string draftId)
    {
        if (!Id.IsMatch(draftId)) throw new StoreFailure("INVALID_ID");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var state = TaskStore.Scalar(db, tx,
            "SELECT state FROM project_drafts WHERE id=@id", ("@id", draftId));
        if (state != "Generating") { tx.Commit(); return; }
        // Cap repeated progress events even when a model is verbose.
        var count = TaskStore.Scalar(db, tx, """
            SELECT COUNT(*) FROM project_draft_events
            WHERE draft_id=@id AND kind='ModelOutputReceived'
            """, ("@id", draftId));
        if (int.TryParse(count, out var n) && n < 60)
            Append(db, tx, draftId, "ModelOutputReceived");
        tx.Commit();
    }

    public void Advance(string draftId, string from, string state, string kind,
                        string? error = null,
                        IReadOnlyList<ProjectDraftFile>? files = null)
    {
        if (!Id.IsMatch(draftId) || !new[] {
                "Queued","Generating","Validating","Ready",
                "Failed","Interrupted","Superseded" }.Contains(state))
            throw new StoreFailure("INVALID_STATE");
        if (files is not null && state != "Ready")
            throw new StoreFailure("INVALID_STATE");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var current = TaskStore.Scalar(db, tx,
            "SELECT state FROM project_drafts WHERE id=@id", ("@id", draftId));
        if (current != from)
        {
            tx.Commit();
            return; // Cancellation/restart won the transition: never replay.
        }
        if (state == "Ready")
        {
            // Check the newest approved spec in the SAME SQLite transaction
            // that persists source files. A concurrent interview revision
            // cannot silently approve stale generated code.
            string task;
            int expectedVersion;
            string expectedHash;
            using (var contract = TaskStore.Command(db, tx, """
                SELECT task_id,spec_version,spec_hash
                FROM project_drafts WHERE id=@id
                """, ("@id", draftId)))
            using (var contractReader = contract.ExecuteReader())
            {
                if (!contractReader.Read()) throw new StoreFailure("NOT_FOUND");
                task = contractReader.GetString(0);
                expectedVersion = contractReader.GetInt32(1);
                expectedHash = contractReader.GetString(2);
            }
            int? currentVersion = null;
            string? currentHash = null;
            string? currentStatus = null;
            using (var specQuery = TaskStore.Command(db, tx, """
                SELECT version,sha256,status FROM interview_specs
                WHERE task_id=@task ORDER BY version DESC LIMIT 1
                """, ("@task", task)))
            using (var reader = specQuery.ExecuteReader())
            {
                if (reader.Read())
                {
                    currentVersion = reader.GetInt32(0);
                    currentHash = reader.GetString(1);
                    currentStatus = reader.GetString(2);
                }
            }
            if (currentVersion != expectedVersion || currentHash != expectedHash ||
                currentStatus != "Approved")
            {
                state = "Superseded"; kind = "SpecificationChanged";
                files = null; error = "SPECIFICATION_CONFLICT";
            }
            else files = ProjectDraftValidation.Check(files ??
                throw new StoreFailure("DRAFT_OUTPUT_REJECTED"));
        }
        TaskStore.Execute(db, tx, """
            UPDATE project_drafts SET state=@state,error_code=@error WHERE id=@id
            """, ("@state", state), ("@error", (object?)error ?? DBNull.Value),
            ("@id", draftId));
        if (files is not null)
            foreach (var file in files)
                TaskStore.Execute(db, tx, """
                    INSERT INTO project_draft_files(draft_id,path,content,sha256)
                    VALUES(@id,@path,@content,@sha)
                    """, ("@id", draftId), ("@path", file.Path),
                    ("@content", file.Content), ("@sha", file.Sha256));
        Append(db, tx, draftId, kind);
        tx.Commit();
    }
}

public sealed class ProjectDraftCoordinator(
    ProjectDraftStore drafts, InterviewStore interviews,
    TaskStore tasks, IProjectDraftModel model,
    IHostApplicationLifetime lifetime)
{
    private readonly SemaphoreSlim _limit = new(1, 1);

    public ProjectDraftView Start(string taskId, StartProjectDraftRequest request)
    {
        var draft = drafts.Create(taskId, request);
        _ = Task.Run(() => WorkAsync(draft.Id), CancellationToken.None);
        return draft;
    }

    private async Task WorkAsync(string id)
    {
        var acquired = false;
        try
        {
            await _limit.WaitAsync(lifetime.ApplicationStopping);
            acquired = true;
            drafts.Advance(id, "Queued", "Generating", "ModelRequestStarted");
            var run = drafts.Read(id);
            if (run.State != "Generating") return;
            var snapshot = interviews.Read(run.TaskId);
            var spec = snapshot.Specification;
            if (spec is null || spec.Status != "Approved" ||
                spec.Version != run.SpecificationVersion ||
                spec.Sha256 != run.SpecificationHash)
            {
                drafts.Advance(id, "Generating", "Superseded",
                               "SpecificationChanged", "SPECIFICATION_CONFLICT");
                return;
            }
            var objective = tasks.GetTask(run.TaskId).Task.Objective;
            var generated = model is IProjectDraftStream streaming
                ? await streaming.GenerateStreamingAsync(
                    objective, spec.Content, run.Model,
                    () => {
                        drafts.RecordModelCheckpoint(id);
                        return Task.CompletedTask;
                    }, lifetime.ApplicationStopping)
                : await model.GenerateAsync(
                    objective, spec.Content, run.Model,
                    lifetime.ApplicationStopping);
            drafts.Advance(id, "Generating", "Validating", "ModelResponseReceived");
            drafts.Advance(id, "Validating", "Ready", "DraftFilesSaved",
                           files: generated);
        }
        catch (OperationCanceledException)
        {
            Fail("Interrupted", "HOST_STOPPED");
        }
        catch (StoreFailure error)
        {
            Fail("Failed", error.Code);
        }
        catch (Exception)
        {
            Fail("Failed", "BUILD_UNAVAILABLE");
        }
        finally
        {
            if (acquired) _limit.Release();
        }

        void Fail(string state, string code)
        {
            foreach (var prior in new[] { "Queued", "Generating", "Validating" })
                try { drafts.Advance(id, prior, state, code, code); }
                catch (Exception) { /* No false verification on failed ledger. */ }
        }
    }
}
