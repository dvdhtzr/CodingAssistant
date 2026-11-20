using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// This is a deliberately narrow, safe first useful task: generate one static
// HTML file as TEXT using local Ollama. It neither runs an agent nor grants
// filesystem, shell, Git, network or project permissions to model output.
// It is independent of the frozen M1 C# repair security qualification.
public sealed record HtmlArtifact(
    string Id, string TaskId, string Model, string Html,
    string Sha256, string CreatedUtc);
public sealed record CreateHtmlPreviewRequest(string? Model);

public interface IHtmlPreviewGenerator
{
    Task<string> GenerateAsync(string objective, string model,
                               CancellationToken cancellation);
}

public sealed class LocalHtmlPreviewGenerator : IHtmlPreviewGenerator
{
    public const string Baseline = "qwen3.5:4b";
    public const string Coder = "qwen2.5-coder:3b";

    public static bool AllowedModel(string? model) => model is Baseline or Coder;

    public async Task<string> GenerateAsync(
        string objective, string model, CancellationToken cancellation)
    {
        if (!AllowedModel(model) || objective.Length is < 1 or > 4000)
            throw new StoreFailure("INVALID_INPUT");
        // Explicit IPv4 loopback only. Never respect a proxy or redirect.
        using var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            UseCookies = false,
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromMinutes(6),
            MaxResponseContentBufferSize = 24_000
        };
        var request = new
        {
            model,
            stream = false,
            think = false,
            messages = new object[]
            {
                new { role = "system", content =
                    "Return ONLY one complete, minimal, static index.html document, " +
                    "starting with <!DOCTYPE html>. Use inline CSS if useful. " +
                    "No JavaScript, network, remote resources, forms, images, " +
                    "iframes, links, SVG, embedded content or markdown. " +
                    "Make the user's requested visible text prominent. " +
                    "Keep the entire answer under 3500 characters." },
                new { role = "user", content = objective }
            },
            options = new
            {
                temperature = 0.1,
                num_predict = 650,
                num_ctx = 2048
            }
        };
        using var body = new StringContent(JsonSerializer.Serialize(request),
                                           Encoding.UTF8, "application/json");
        using var message = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = body
        };
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(
                message, HttpCompletionOption.ResponseContentRead, cancellation);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw new StoreFailure(response.StatusCode ==
                    System.Net.HttpStatusCode.NotFound
                    ? "MODEL_NOT_INSTALLED" : "LOCAL_MODEL_UNAVAILABLE");
            try
            {
                var data = await response.Content.ReadAsStringAsync(cancellation);
                if (data.Length > 24000)
                    throw new StoreFailure("MODEL_OUTPUT_REJECTED");
                using var document = JsonDocument.Parse(data);
                var code = document.RootElement.GetProperty("message")
                    .GetProperty("content").GetString() ?? "";
                return HtmlArtifactStore.Validate(code);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException
                or InvalidOperationException)
            {
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            }
        }
    }
}

public sealed class HtmlArtifactStore(TaskStore tasks)
{
    private static readonly Regex Id = new(
        "^[0-9a-f]{32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS html_artifacts (
                id TEXT PRIMARY KEY NOT NULL,
                task_id TEXT NOT NULL REFERENCES tasks(id),
                model TEXT NOT NULL,
                html TEXT NOT NULL,
                sha256 TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS html_artifact_by_task
                ON html_artifacts(task_id, created_utc DESC, id DESC);
            """);
    }

    public static string Validate(string generated)
    {
        if (string.IsNullOrWhiteSpace(generated))
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        var text = generated.Trim();
        // Some local models include Markdown wrappers despite the system prompt.
        if (text.StartsWith("```html", StringComparison.OrdinalIgnoreCase)
            && text.EndsWith("```", StringComparison.Ordinal))
        {
            var line = text.IndexOf('\n');
            if (line > 0)
                text = text[(line + 1)..^3].Trim();
        }
        if (text.Length is < 40 or > 8000 ||
            !text.StartsWith("<!doctype html>", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(text, @"<html(?:\s|>)", RegexOptions.IgnoreCase) ||
            !Regex.IsMatch(text, @"<body(?:\s|>)", RegexOptions.IgnoreCase) ||
            !text.Contains("</body>", StringComparison.OrdinalIgnoreCase) ||
            !text.Contains("</html>", StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(text,
                @"<\s*(script|iframe|frame|object|embed|link|form|img|audio|video|svg|math|base)\b|<meta\s+http-equiv\b|\bon[a-z]+\s*=|\b(href|src|srcset|action|formaction)\s*=|@import\b|url\s*\(|javascript\s*:",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            text.Contains('\0'))
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        return text + "\n";
    }

    public HtmlArtifact? Latest(string taskId)
    {
        if (!Id.IsMatch(taskId)) throw new StoreFailure("INVALID_ID");
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        using var query = TaskStore.Command(db, null, """
            SELECT id,task_id,model,html,sha256,created_utc
            FROM html_artifacts
            WHERE task_id=@task
            ORDER BY created_utc DESC,id DESC LIMIT 1
            """, ("@task", taskId));
        using var reader = query.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public HtmlArtifact Save(string taskId, string model, string html)
    {
        if (!Id.IsMatch(taskId) || !LocalHtmlPreviewGenerator.AllowedModel(model))
            throw new StoreFailure("INVALID_INPUT");
        var safe = Validate(html);
        _ = tasks.GetTask(taskId);
        var value = new HtmlArtifact(
            Guid.NewGuid().ToString("N"), taskId, model, safe,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(safe)))
                .ToLowerInvariant(), DateTimeOffset.UtcNow.ToString("O"));
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            INSERT INTO html_artifacts(id,task_id,model,html,sha256,created_utc)
            VALUES (@id,@task,@model,@html,@sha,@utc)
            """, ("@id", value.Id), ("@task", value.TaskId),
            ("@model", value.Model), ("@html", value.Html),
            ("@sha", value.Sha256), ("@utc", value.CreatedUtc));
        return value;
    }

    private static HtmlArtifact Read(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5));
}
