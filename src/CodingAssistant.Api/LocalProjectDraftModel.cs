using System.Net;
using System.Text;
using System.Text.Json;

namespace CodingAssistant.Api;

/// <summary>
/// Generates a bounded, task-specific source bundle from an owner-approved
/// specification through local Ollama, without tool use or code execution.
/// </summary>
public sealed class LocalProjectDraftModel : IProjectDraftModel
{
    public async Task<IReadOnlyList<PlannedProjectFile>> PlanAsync(
        string objective, string approvedSpecification, string model,
        CancellationToken cancellation)
    {
        var prompt = """
            You are a software engineer planning an implementation from an
            explicitly approved specification. Choose up to FIVE essential
            text source/project files for an initial working draft. Pick paths
            and technologies appropriate to the actual specification, NEVER
            use a hard-coded C# repair fixture or welcome-page template.
            Return ONLY a JSON object with this exact shape:
            {"files":[{"path":"relative/source.ext","purpose":"why this file is required"}]}
            Paths must be relative, plain ASCII, no leading dot, no parent
            traversal; up to 4 directory components. Valid extensions:
            .cs .csproj .ts .tsx .js .jsx .css .html .json .md .txt
            .py .sln .yml .yaml. Do not include scripts, credentials,
            environment files, binaries or package installs.
            Choose a coherent minimal implementable slice instead of
            pretending that a complex full application fits five files.
            """;
        var result = await ChatAsync(model, prompt,
            "PROJECT OBJECTIVE:\n" + objective +
            "\n\nAPPROVED SPECIFICATION:\n" + approvedSpecification,
            json: true, maxTokens: 650, cancellation);
        try
        {
            using var parsed = JsonDocument.Parse(result);
            var files = parsed.RootElement.GetProperty("files");
            if (files.ValueKind != JsonValueKind.Array)
                throw new StoreFailure("INVALID_PROJECT_PLAN");
            var entries = new List<PlannedProjectFile>();
            foreach (var item in files.EnumerateArray())
            {
                entries.Add(new PlannedProjectFile(
                    item.GetProperty("path").GetString() ?? "",
                    item.GetProperty("purpose").GetString() ?? ""));
                if (entries.Count > ProjectDraftStore.MaxFiles)
                    throw new StoreFailure("INVALID_PROJECT_PLAN");
            }
            return ProjectDraftStore.ValidatePlan(entries);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException
            or KeyNotFoundException)
        {
            throw new StoreFailure("INVALID_PROJECT_PLAN");
        }
    }

    public async Task<string> WriteAsync(
        string objective, string approvedSpecification, string model,
        PlannedProjectFile file, IReadOnlyList<ProjectDraftFile> previous,
        CancellationToken cancellation)
    {
        var path = ProjectDraftStore.SafePath(file.Path);
        if (previous.Count >= ProjectDraftStore.MaxFiles)
            throw new StoreFailure("PROJECT_SIZE_LIMIT");
        var earlier = string.Join("\n", previous.Select(
            f => "FILE " + f.Path + " (first 900 chars):\n" +
                 f.Content[..Math.Min(900, f.Content.Length)]));
        var system = """
            You are implementing ONE project source file from an approved
            specification. Return ONLY the complete source text for the
            requested file. No surrounding explanation, no tool calls,
            no shell commands, no Markdown code fence, no placeholders,
            no TODO-only stubs and no invented verification claims. The
            generated output is an UNTRUSTED DRAFT that will not be
            executed or compiled by this workflow. Keep it below 15000
            characters and make it internally consistent with earlier files.
            """;
        var prompt = "PROJECT OBJECTIVE:\n" + objective +
                     "\n\nAPPROVED SPECIFICATION:\n" + approvedSpecification +
                     "\n\nFILE TO CREATE:\n" + path +
                     "\nPURPOSE:\n" + file.Purpose +
                     "\n\nPREVIOUS FILE CONTEXT:\n" + earlier;
        var source = await ChatAsync(model, system, prompt, json: false,
            maxTokens: 3000, cancellation);
        return ProjectDraftStore.ValidateSource(path, source);
    }

    private static async Task<string> ChatAsync(
        string model, string policy, string prompt,
        bool json, int maxTokens, CancellationToken cancellation)
    {
        if (!LocalInterviewModel.AllowedModel(model) ||
            prompt.Length is < 15 or > 32000)
            throw new StoreFailure("INVALID_INPUT");

        // No ambient proxy, no redirects, no provider substitution.
        using var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromMinutes(7),
            MaxResponseContentBufferSize = 70_000
        };
        var requestBody = new
        {
            model,
            stream = false,
            think = false,
            format = json ? "json" : null,
            messages = new[]
            {
                new { role = "system", content = policy },
                new { role = "user", content = prompt }
            },
            options = new {
                temperature = 0.15,
                num_predict = maxTokens,
                num_ctx = 8192
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody),
                Encoding.UTF8, "application/json")
        };
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new StoreFailure("MODEL_NOT_INSTALLED");
            if (!response.IsSuccessStatusCode)
                throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
            var jsonText = await response.Content.ReadAsStringAsync(cancellation);
            if (jsonText.Length > 70000)
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            using var decoded = JsonDocument.Parse(jsonText);
            return decoded.RootElement.GetProperty("message")
                .GetProperty("content").GetString() ??
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
        }
        catch (JsonException)
        {
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        }
    }
}
