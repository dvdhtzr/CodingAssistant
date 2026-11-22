using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record AtlasImportGitHubRequest(
    string? RepositoryUrl, string? CommitSha);
public sealed record AtlasImportedRepository(
    string Id, string TaskId, string RepositoryUrl, string CommitSha,
    string SourceRevision, string Coverage, int IndexedFiles,
    int ExcludedFiles, string CreatedUtc);
public sealed record AtlasImportedRepositoryGraph(
    AtlasImportedRepository Repository, AtlasSnapshot Graph);
public sealed record AtlasImportedFile(
    string Path, string Sha256, string Content);

public interface IAtlasCommitArchiveDownloader
{
    Task<byte[]> DownloadAsync(string owner, string repo,
        string commitSha, CancellationToken cancellation);
}

/// <summary>
/// Fixed-origin public GitHub archive downloader. A caller cannot select a
/// different host, an HTTP redirect, port, path, credentials or command.
/// Private repositories are deliberately NOT supported without a separately
/// authorized credential design.
/// </summary>
public sealed class PublicGitHubCommitArchiveDownloader
    : IAtlasCommitArchiveDownloader
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseDefaultCredentials = false
    })
    {
        Timeout = TimeSpan.FromSeconds(45)
    };

    public async Task<byte[]> DownloadAsync(string owner, string repo,
        string commitSha, CancellationToken cancellation)
    {
        var url = new Uri("https://codeload.github.com/" + owner + "/" +
            repo + "/zip/" + commitSha, UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("CodingAssistant-CodeAtlas/1.0");
        using var response = await Http.SendAsync(request,
            HttpCompletionOption.ResponseHeadersRead, cancellation);
        if (response.StatusCode == HttpStatusCode.NotFound ||
            response.StatusCode == HttpStatusCode.Forbidden)
            throw new StoreFailure("ATLAS_REPOSITORY_NOT_ACCESSIBLE");
        if (response.StatusCode != HttpStatusCode.OK)
            throw new StoreFailure("ATLAS_REPOSITORY_DOWNLOAD_FAILED");
        if (response.Content.Headers.ContentLength is long length &&
            length > CodeAtlasRepositoryArchive.MaxArchiveBytes)
            throw new StoreFailure("ATLAS_ARCHIVE_SIZE_INVALID");
        await using var remote = await response.Content
            .ReadAsStreamAsync(cancellation);
        using var bytes = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = await remote.ReadAsync(buffer, cancellation)) > 0)
        {
            if (bytes.Length + read >
                CodeAtlasRepositoryArchive.MaxArchiveBytes)
                throw new StoreFailure("ATLAS_ARCHIVE_SIZE_INVALID");
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }
}

public sealed class CodeAtlasRepositoryImportStore(
    TaskStore tasks, IAtlasCommitArchiveDownloader downloader)
{
    private const int MaxImportsPerTask = 4;
    private static readonly Regex RepoSegment = new(
        "^[A-Za-z0-9][A-Za-z0-9_.-]{0,99}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CommitHash = new(
        "^[a-f0-9]{40}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex IdPattern = new(
        "^[a-f0-9]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static (string Owner, string Repo, string Commit)
        ParseRequest(AtlasImportGitHubRequest request)
    {
        if (request.RepositoryUrl is null ||
            request.RepositoryUrl.Length > 250 ||
            request.CommitSha is null ||
            !CommitHash.IsMatch(request.CommitSha))
            throw new StoreFailure("ATLAS_IMPORT_INPUT_INVALID");
        if (!Uri.TryCreate(request.RepositoryUrl, UriKind.Absolute,
                out var url) ||
            url.Scheme != Uri.UriSchemeHttps ||
            url.Host != "github.com" ||
            !url.IsDefaultPort || url.UserInfo.Length != 0 ||
            url.Query.Length != 0 || url.Fragment.Length != 0)
            throw new StoreFailure("ATLAS_IMPORT_INPUT_INVALID");
        var segments = url.AbsolutePath.Trim('/').Split('/');
        if (segments.Length != 2)
            throw new StoreFailure("ATLAS_IMPORT_INPUT_INVALID");
        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.Ordinal)
            ? segments[1][..^4] : segments[1];
        if (!RepoSegment.IsMatch(owner) || !RepoSegment.IsMatch(repo) ||
            owner.StartsWith('.') || repo.StartsWith('.'))
            throw new StoreFailure("ATLAS_IMPORT_INPUT_INVALID");
        return (owner, repo, request.CommitSha);
    }

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS atlas_repository_imports (
                id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                owner TEXT NOT NULL, repo TEXT NOT NULL,
                commit_sha TEXT NOT NULL,
                source_revision TEXT NOT NULL,
                coverage TEXT NOT NULL,
                file_count INTEGER NOT NULL,
                excluded_files INTEGER NOT NULL,
                graph_json TEXT NOT NULL,
                created_utc TEXT NOT NULL,
                UNIQUE(task_id,owner,repo,commit_sha)
            );
            CREATE INDEX IF NOT EXISTS atlas_imports_by_task
                ON atlas_repository_imports(task_id,created_utc DESC,id DESC);
            CREATE TABLE IF NOT EXISTS atlas_repository_files (
                import_id TEXT NOT NULL REFERENCES atlas_repository_imports(id),
                path TEXT NOT NULL COLLATE NOCASE,
                sha256 TEXT NOT NULL,
                content TEXT NOT NULL,
                PRIMARY KEY(import_id,path)
            );
            """);
    }

    private static AtlasImportedRepository Decode(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1),
            "https://github.com/" + reader.GetString(2) + "/" +
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8),
            reader.GetString(9));

    private const string Columns = """
        SELECT id,task_id,owner,repo,commit_sha,source_revision,coverage,
            file_count,excluded_files,created_utc
        FROM atlas_repository_imports
        """;

    public IReadOnlyList<AtlasImportedRepository> List(string taskId)
    {
        _ = tasks.GetTask(taskId);
        var results = new List<AtlasImportedRepository>();
        using var db = tasks.Open();
        using var cmd = TaskStore.Command(db, null,
            Columns + " WHERE task_id=@task ORDER BY created_utc DESC,id DESC",
            ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) results.Add(Decode(reader));
        return results.AsReadOnly();
    }

    public AtlasImportedRepositoryGraph Get(string taskId, string importId)
    {
        if (!IdPattern.IsMatch(importId))
            throw new StoreFailure("INVALID_ID");
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        using var cmd = TaskStore.Command(db, null,
            Columns.Replace("FROM atlas_repository_imports",
                ",graph_json FROM atlas_repository_imports") +
            " WHERE task_id=@task AND id=@id",
            ("@task", taskId), ("@id", importId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new StoreFailure("NOT_FOUND");
        var info = Decode(reader);
        var graph = JsonSerializer.Deserialize<AtlasSnapshot>(
            reader.GetString(10)) ??
            throw new StoreFailure("ATLAS_SNAPSHOT_CORRUPT");
        if (graph.RunId != importId ||
            graph.SourceRevision != info.SourceRevision)
            throw new StoreFailure("ATLAS_SNAPSHOT_CORRUPT");
        return new(info, graph);
    }

    public AtlasImportedFile GetFile(string taskId,
        string importId, string path)
    {
        if (!IdPattern.IsMatch(importId) ||
            string.IsNullOrWhiteSpace(path) || path.Length > 240)
            throw new StoreFailure("INVALID_INPUT");
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        using var cmd = TaskStore.Command(db, null, """
            SELECT f.path,f.sha256,f.content
            FROM atlas_repository_files f
            JOIN atlas_repository_imports r ON r.id=f.import_id
            WHERE r.task_id=@task AND r.id=@id AND f.path=@path
            """, ("@task", taskId), ("@id", importId), ("@path", path));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) throw new StoreFailure("NOT_FOUND");
        return new(reader.GetString(0), reader.GetString(1),
            reader.GetString(2));
    }

    public async Task<AtlasImportedRepository> ImportAsync(
        string taskId, AtlasImportGitHubRequest request,
        CancellationToken cancellation)
    {
        _ = tasks.GetTask(taskId);
        var (owner, repo, commit) = ParseRequest(request);
        var existing = List(taskId).FirstOrDefault(record =>
            record.RepositoryUrl ==
                "https://github.com/" + owner + "/" + repo &&
            record.CommitSha == commit);
        if (existing is not null) return existing;
        if (List(taskId).Count >= MaxImportsPerTask)
            throw new StoreFailure("ATLAS_IMPORT_LIMIT");

        var bytes = await downloader.DownloadAsync(owner, repo,
            commit, cancellation);
        var parsed = CodeAtlasRepositoryArchive.Parse(bytes);
        var id = Guid.NewGuid().ToString("N");
        var graph = CodeAtlasIndexer.Index(id, parsed.Files,
            owner + "/" + repo);
        if (parsed.Diagnostics.Count > 0)
        {
            graph = graph with
            {
                Coverage = "PARTIAL_SOURCE:" + graph.Coverage,
                Diagnostics = graph.Diagnostics.Concat(parsed.Diagnostics).ToArray()
            };
        }
        var serialized = JsonSerializer.Serialize(graph);
        if (Encoding.UTF8.GetByteCount(serialized) > 12 * 1024 * 1024)
            throw new StoreFailure("ATLAS_GRAPH_SIZE_LIMIT");

        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var count = int.Parse(TaskStore.Scalar(db, tx, """
            SELECT COUNT(*) FROM atlas_repository_imports WHERE task_id=@task
            """, ("@task", taskId))!);
        if (count >= MaxImportsPerTask)
            throw new StoreFailure("ATLAS_IMPORT_LIMIT");
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            INSERT INTO atlas_repository_imports(id,task_id,owner,repo,
                commit_sha,source_revision,coverage,file_count,excluded_files,
                graph_json,created_utc)
            VALUES(@id,@task,@owner,@repo,@commit,@revision,@coverage,
                @count,@excluded,@graph,@now)
            """, ("@id", id), ("@task", taskId),
            ("@owner", owner), ("@repo", repo),
            ("@commit", commit), ("@revision", graph.SourceRevision),
            ("@coverage", graph.Coverage), ("@count", parsed.Files.Count),
            ("@excluded", parsed.ExcludedFiles),
            ("@graph", serialized), ("@now", now));
        foreach (var file in parsed.Files)
            TaskStore.Execute(db, tx, """
                INSERT INTO atlas_repository_files(import_id,path,sha256,content)
                VALUES(@id,@path,@hash,@content)
                """, ("@id", id), ("@path", file.Path),
                ("@hash", file.Sha256), ("@content", file.Content));
        tx.Commit();
        return new(id, taskId,
            "https://github.com/" + owner + "/" + repo, commit,
            graph.SourceRevision, graph.Coverage,
            parsed.Files.Count, parsed.ExcludedFiles, now);
    }
}
