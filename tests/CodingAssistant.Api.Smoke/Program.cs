using System.Net;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodingAssistant.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Data.Sqlite;

const string OwnerToken = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
var checks = 0;

void Check(bool condition, string description)
{
    if (!condition)
        throw new Exception("M2 smoke assertion failed: " + description);
    checks++;
}

async Task<JsonDocument> ReadJson(HttpResponseMessage response)
{
    var body = await response.Content.ReadAsStringAsync();
    return JsonDocument.Parse(body);
}

async Task<HttpResponseMessage> Post(HttpClient client, string path, object body) =>
    await client.PostAsJsonAsync(path, body);

async Task<HttpClient> Start(ApiSettings settings, Action<WebApplication> saveApp,
    IM1ExecutionRunner? runner = null, IHtmlPreviewGenerator? htmlGenerator = null,
    IInterviewModel? interviewModel = null,
    IProjectDraftModel? projectModel = null,
    IOllamaModelCatalog? catalog = null)
{
    var app = ApiHost.Build([], settings, runner, htmlGenerator, interviewModel,
                            projectModel, catalog ?? new SmokeOllamaModelCatalog());
    await app.StartAsync();
    saveApp(app);
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;
    var address = addresses?.SingleOrDefault() ??
        throw new Exception("No bound server address");
    Check(address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
        "M2 bound only IPv4 loopback");
    return new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false
    })
    {
        BaseAddress = new Uri(address)
    };
}

// Windows Visual Studio F5 and the one-click launcher must share a stable,
// encrypted, owner-private credential without requiring manual environment setup.
if (OperatingSystem.IsWindows())
{
    var tokenDirectory = Path.Combine(Path.GetTempPath(),
        "ca-windows-token-" + Guid.NewGuid().ToString("N"));
    var originalToken = Environment.GetEnvironmentVariable(
        "CODINGASSISTANT_OWNER_TOKEN");
    var originalDirectory = Environment.GetEnvironmentVariable(
        "CODINGASSISTANT_DATA_DIR");
    try
    {
        var token = WindowsOwnerTokenStore.GetOrCreate(tokenDirectory);
        Check(System.Text.RegularExpressions.Regex.IsMatch(token, "^[0-9a-f]{64}$"),
            "Windows owner token is cryptographically generated in the correct format");
        Check(WindowsOwnerTokenStore.GetOrCreate(tokenDirectory) == token,
            "Windows F5 restart retains the same credential");
        var savedPath = Path.Combine(tokenDirectory, "owner-token.dpapi");
        var savedContent = File.ReadAllText(savedPath);
        Check(savedContent.Length > 32 &&
              !savedContent.Contains(token, StringComparison.Ordinal),
            "owner token encrypted with DPAPI, never plaintext at rest");

        Environment.SetEnvironmentVariable("CODINGASSISTANT_DATA_DIR", tokenDirectory);
        Environment.SetEnvironmentVariable("CODINGASSISTANT_OWNER_TOKEN", null);
        var fromF5 = ApiSettings.FromEnvironment();
        Check(fromF5.OwnerToken == token &&
              fromF5.DataDirectory == tokenDirectory,
            "Visual Studio F5 discovers saved credential with no env token");
        using (var startup = ApiHost.Build([],
            fromF5 with { BindUrl = "http://127.0.0.1:0" }))
            await startup.DisposeAsync();
        Check(true, "Visual Studio default API can start with generated owner token");

        // Windows PowerShell 5.1 reads the exact same DPAPI file as .NET 10.
        // Only a SHA-256 digest is passed to the test subprocess; never the token.
        var interopScript = Path.Combine(Directory.GetCurrentDirectory(),
            "tests", "Check-WindowsTokenInterop.ps1");
        var processArgs = new System.Diagnostics.ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        processArgs.ArgumentList.Add("-NoLogo");
        processArgs.ArgumentList.Add("-NoProfile");
        processArgs.ArgumentList.Add("-ExecutionPolicy");
        processArgs.ArgumentList.Add("Bypass");
        processArgs.ArgumentList.Add("-File");
        processArgs.ArgumentList.Add(interopScript);
        processArgs.ArgumentList.Add(tokenDirectory);
        processArgs.Environment["CODINGASSISTANT_TOKEN_TEST_SHA"] =
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
        using (var process = System.Diagnostics.Process.Start(processArgs) ??
            throw new Exception("Unable to start Windows PowerShell credential interop"))
        {
            if (!process.WaitForExit(15000))
            {
                process.Kill();
                throw new Exception("Windows DPAPI interoperability test timed out");
            }
            Check(process.ExitCode == 0,
                "Windows PowerShell and .NET decrypt the same DPAPI user token");
        }

        Environment.SetEnvironmentVariable("CODINGASSISTANT_OWNER_TOKEN", "");
        Check(ApiSettings.FromEnvironment().OwnerToken == token,
            "blank inherited owner token also bootstraps from Windows storage");
        Environment.SetEnvironmentVariable("CODINGASSISTANT_OWNER_TOKEN", "bad-token");
        Check(ApiSettings.FromEnvironment().OwnerToken == "bad-token",
            "explicit misconfigured token is not silently overridden");
        try
        {
            _ = ApiHost.Build([], ApiSettings.FromEnvironment()
                with { BindUrl = "http://127.0.0.1:0" });
            throw new Exception("Invalid explicit owner credential bypassed security");
        }
        catch (InvalidOperationException error) when (
            error.Message.Contains("64 random lowercase", StringComparison.Ordinal))
        {
            Check(true, "explicit invalid bearer token still fails closed");
        }

        File.WriteAllText(savedPath, "invalid-base64!");
        try
        {
            _ = WindowsOwnerTokenStore.GetOrCreate(tokenDirectory);
            throw new Exception("Corrupt persisted credential was rotated silently");
        }
        catch (InvalidOperationException)
        {
            Check(File.ReadAllText(savedPath) == "invalid-base64!",
                "corrupted owner credential fails closed, never silently resets");
        }
    }
    finally
    {
        Environment.SetEnvironmentVariable(
            "CODINGASSISTANT_OWNER_TOKEN", originalToken);
        Environment.SetEnvironmentVariable(
            "CODINGASSISTANT_DATA_DIR", originalDirectory);
        try { Directory.Delete(tokenDirectory, recursive: true); }
        catch (IOException) { /* Test cleanup can race Windows AV handles. */ }
    }
}

// The catalog must filter cloud aliases, unsafe names and non-downloaded entries.
{
    var models = LocalOllamaModelCatalog.ParseTags(
        """{"models":[{"name":"gemma4:latest","size":120000},{"name":"qwen3.5:4b","size":400},{"name":"gemma4:latest","size":120000},{"name":"gemma4:cloud","size":999},{"name":"some-model-cloud","size":100},{"name":"bad model","size":99},{"name":"not-downloaded:latest","size":0}]}""");
    Check(models.SequenceEqual(new[] { "gemma4:latest", "qwen3.5:4b" }),
        "catalog returns sorted, distinct, genuinely local and safe model names");
    Check(!LocalOllamaModelCatalog.ValidLocalName("gemma4:cloud") &&
          !LocalOllamaModelCatalog.ValidLocalName("cloud-model-cloud") &&
          !LocalOllamaModelCatalog.ValidLocalName("evil\nmodel") &&
          !LocalOllamaModelCatalog.ValidLocalName("https://ollama.com"),
        "cloud and malformed identifiers never become local inference targets");
}

// Small local models may ignore JSON-only prompts; validate strict schema,
// bounded plain-question recovery, and fail-closed malformed reasoning without
// making real Ollama requests in model-free CI.
{
    var structured = LocalInterviewModel.ParseInterviewReply(
        """{"message":"Who should use this dashboard?","readyForSpecification":false}""");
    Check(structured.Message.Contains("dashboard?", StringComparison.Ordinal) &&
          !structured.ReadyForSpecification,
          "schema-constrained interview response is parsed");

    var ready = LocalInterviewModel.ParseInterviewReply(
        "```json\n" +
        """{"message":"I can draft your specification now.","readyForSpecification":true}""" +
        "\n```");
    Check(ready.ReadyForSpecification &&
          ready.Message == "I can draft your specification now.",
          "clean fenced JSON is accepted");

    var conversational = LocalInterviewModel.ParseInterviewReply(
        "Who will use this project, and should it save data locally?");
    Check(!conversational.ReadyForSpecification &&
          conversational.Message.Contains("save data", StringComparison.Ordinal),
          "a plain project-specific question cannot prematurely approve readiness");

    foreach (var rejected in new[] {
        "", "{}", "{broken JSON",
        """{"message":"Who will use this?","readyForSpecification":"yes"}""",
        """{"message":"short","readyForSpecification":true}""",
        "Thinking about implementation steps: what is needed?",
        "<think>secret reasoning</think> What is needed?"
    })
    {
        try
        {
            _ = LocalInterviewModel.ParseInterviewReply(rejected);
            throw new Exception("Invalid local model output was accepted");
        }
        catch (StoreFailure ex) when (ex.Code == "MODEL_OUTPUT_REJECTED")
        {
            checks++;
        }
    }
}

// Contract preview binds the actual approved specification and dynamically
// selected project paths. This cannot authorize code execution.
{
    var now = DateTimeOffset.UtcNow.ToString("O");
    var task = new TaskItem(new string('a', 32), "Expenses",
        "Implement a local expense tracking API", "Draft", 0, now);
    var approvedText = "Create a .NET 10 expense tracking API, with endpoints " +
        "for adding, listing and filtering expenses. Include a local store and " +
        "small tests. No remote services. All data must remain private, and " +
        "the project should compile offline in a bounded temporary workspace.";
    var spec = new InterviewSpecification(1, TaskStore.Model,
        approvedText, new string('b', 64), "Approved", now, now);
    var job = new ProjectDraftJob(new string('c', 32), task.Id, 1, spec.Sha256,
        TaskStore.Model, "Generated", "DraftGenerated", null, now, now);
    var files = new[] {
        new ProjectDraftFile("Expenses/Expenses.csproj", "Project configuration",
            "<Project Sdk=\"Microsoft.NET.Sdk\" />", new string('d', 64), now),
        new ProjectDraftFile("Expenses/Program.cs", "Expense API",
            "var app = WebApplication.Create();", new string('e', 64), now)
    };
    var draft = new ProjectDraftSnapshot(job, [], files);
    var contract = TaskAgentContract.Preview(task, spec, draft);
    var json = JsonSerializer.SerializeToUtf8Bytes(contract.Contract);
    var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(json))
        .ToLowerInvariant();
    Check(sha == contract.ContractSha256, "task agent contract bytes have exact SHA256");
    Check(!contract.ExecutionEnabled, "preview is not executable approval");
    Check(contract.Contract.GetProperty("objective").GetString() == task.Objective &&
          contract.Contract.GetProperty("specification").GetString() == approvedText,
          "approved objective/specification reach new task contract");
    Check(contract.Contract.GetProperty("approvedSpecificationSha256").GetString()
          == spec.Sha256, "exact approved version bound");
    Check(contract.Contract.GetProperty("files")[0].GetString()
          == "Expenses/Expenses.csproj",
          "dynamically planned file scope reaches new agent contract");
    try {
        _ = TaskAgentContract.Preview(task, spec with { Version = 2 }, draft);
        throw new Exception("Stale task contract accepted");
    } catch (StoreFailure error) when (error.Code == "SPECIFICATION_CONFLICT") {
        checks++;
    }
    try {
        _ = TaskAgentContract.Preview(task, spec with { Model = "qwen2.5-coder:3b" }, draft);
        throw new Exception("Unqualified model was admitted");
    } catch (StoreFailure error) when (error.Code == "MODEL_NOT_QUALIFIED") {
        checks++;
    }
}

var root = Path.Combine(Path.GetTempPath(), "codingassistant-m2-smoke-" +
                        Guid.NewGuid().ToString("N"));
var data = Path.Combine(root, "owner-private");
Directory.CreateDirectory(root);
WebApplication? appInstance = null;
HttpClient? client = null;

try
{
    var settings = new ApiSettings(data, OwnerToken, "http://127.0.0.1:0");

    foreach (var invalid in new[]
    {
        new ApiSettings(data, "short", "http://127.0.0.1:0"),
        new ApiSettings(data, OwnerToken, "http://0.0.0.0:5098"),
        new ApiSettings(data, OwnerToken, "http://localhost:5098"),
        new ApiSettings(data, OwnerToken, "https://127.0.0.1:5098"),
        new ApiSettings(data, OwnerToken, "http://192.168.0.1:5098"),
    })
    {
        try
        {
            var forbidden = ApiHost.Build([], invalid);
            await forbidden.DisposeAsync();
            throw new Exception("Unsafe API settings were accepted");
        }
        catch (InvalidOperationException)
        {
            checks++;
        }
    }

    var htmlGenerator = new SmokeHtmlGenerator();
    client = await Start(settings, a => appInstance = a,
                         htmlGenerator: htmlGenerator);

    using (var unauthenticated = new HttpClient
    {
        BaseAddress = client.BaseAddress
    })
    {
        using var publicHealth = await unauthenticated.GetAsync("/healthz");
        Check(publicHealth.StatusCode == HttpStatusCode.OK,
            "model-free public health available");
        using var healthBody = await ReadJson(publicHealth);
        Check(!healthBody.RootElement.GetProperty("executionEnabled").GetBoolean(),
            "health does not advertise execution");
        // M3's compiled React UI is served from the exact same loopback
        // origin. Static assets are public, but /api never is.
        var uiSource = Path.Combine(Directory.GetCurrentDirectory(),
            "src", "CodingAssistant.Api", "wwwroot", "index.html");
        if (File.Exists(uiSource))
        {
            using var page = await unauthenticated.GetAsync("/");
            Check(page.StatusCode == HttpStatusCode.OK,
                "compiled React UI accessible on owner loopback");
            Check(page.Content.Headers.ContentType?.MediaType == "text/html",
                "Mission Control index served as HTML");
            Check(page.Headers.TryGetValues("Content-Security-Policy", out var csp) &&
                  csp.Any(value => value.Contains("script-src 'self'") &&
                                   value.Contains("frame-ancestors 'none'")),
                "frontend response has restrictive CSP");
            Check(page.Headers.TryGetValues("Referrer-Policy", out var referrer) &&
                  referrer.Contains("no-referrer"),
                "owner token never leaked via referrer");
            var html = await page.Content.ReadAsStringAsync();
            Check(html.Contains("Mission Control", StringComparison.Ordinal) &&
                  !html.Contains(OwnerToken, StringComparison.Ordinal),
                "static HTML has no embedded owner credential");
        }
        using var noToken = await unauthenticated.GetAsync("/api/tasks");
        Check(noToken.StatusCode == HttpStatusCode.Unauthorized,
            "unauthenticated owner history blocked");
        using var noAuthPost = await Post(unauthenticated, "/api/tasks", new
        {
            title = "unauthorized",
            objective = "no",
            requestId = "c" + new string('0', 31)
        });
        Check(noAuthPost.StatusCode == HttpStatusCode.Unauthorized,
            "unauthenticated mutation blocked");
    }

    // Zero-click Windows browser sign-in. The long-lived owner token stays
    // server-side, and all other anonymous API routes remain unauthorized.
    using (var browser = new HttpClient(new SocketsHttpHandler
    {
        UseProxy = false
    }) { BaseAddress = client.BaseAddress })
    {
        using (var wrongVerb = await browser.GetAsync("/api/local-session"))
            Check(wrongVerb.StatusCode == HttpStatusCode.Unauthorized,
                "browser bootstrap cannot use unguarded GET navigation");
        using (var noHeaders = await browser.PostAsJsonAsync(
            "/api/local-session", new { }))
            Check(noHeaders.StatusCode == (OperatingSystem.IsWindows()
                ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound),
                "anonymous or non-Windows browser bootstrap cannot issue credentials");

        HttpRequestMessage SessionRequest(string origin, string site,
            string? hostOverride = null)
        {
            var request = new HttpRequestMessage(HttpMethod.Post,
                "/api/local-session")
            {
                Content = JsonContent.Create(new { })
            };
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Sec-Fetch-Site", site);
            request.Headers.Add("X-CodingAssistant-Client", "mission-control");
            if (hostOverride is not null) request.Headers.Host = hostOverride;
            return request;
        }
        var localOrigin = client.BaseAddress!.GetLeftPart(UriPartial.Authority);
        using (var foreign = SessionRequest("http://malicious.example",
            "cross-site"))
        using (var response = await browser.SendAsync(foreign))
            Check(response.StatusCode == (OperatingSystem.IsWindows()
                ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound),
                "drive-by cross-site requests cannot bootstrap owner session");
        using (var fakeSite = SessionRequest(localOrigin, "cross-site"))
        using (var response = await browser.SendAsync(fakeSite))
            Check(response.StatusCode == (OperatingSystem.IsWindows()
                ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound),
                "browser fetch metadata blocks cross-site requests even if origin is forged");
        using (var rebinding = SessionRequest(localOrigin, "same-origin",
            "attacker.example:" + client.BaseAddress.Port))
        using (var response = await browser.SendAsync(rebinding))
            Check(response.StatusCode == (OperatingSystem.IsWindows()
                ? HttpStatusCode.Forbidden : HttpStatusCode.NotFound),
                "DNS rebinding with foreign Host cannot obtain a browser bearer");

        if (OperatingSystem.IsWindows())
        {
            using var allowed = SessionRequest(localOrigin, "same-origin");
            using var sessionResponse = await browser.SendAsync(allowed);
            using var payload = await ReadJson(sessionResponse);
            var shortToken = payload.RootElement.GetProperty("sessionToken").GetString()!;
            Check(sessionResponse.StatusCode == HttpStatusCode.OK &&
                  System.Text.RegularExpressions.Regex.IsMatch(
                      shortToken, "^[0-9a-f]{64}$") &&
                  shortToken != OwnerToken &&
                  payload.RootElement.GetProperty("lifetimeSeconds").GetInt32() ==
                  12 * 60 * 60,
                "same-origin Windows UI receives only a 12-hour scoped bearer");
            Check(!sessionResponse.Headers.Contains("Set-Cookie"),
                "automatic browser session never creates a cookie");
            using var actualBrowser = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false
            }) { BaseAddress = client.BaseAddress };
            actualBrowser.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shortToken);
            using (var projectList = await actualBrowser.GetAsync("/api/tasks"))
                Check(projectList.StatusCode == HttpStatusCode.OK,
                    "auto-issued bearer reads authenticated owner APIs");
            using (var created = await Post(actualBrowser, "/api/tasks", new {
                title = "Browser auto-auth sample",
                objective = "Verify automatic Windows browser authorization",
                requestId = Guid.NewGuid().ToString("N")
            }))
                Check(created.StatusCode == HttpStatusCode.Created,
                    "auto-issued bearer authorizes owner-initiated writes");
            var altered = shortToken[..63] +
                (shortToken[^1] == '0' ? '1' : '0');
            actualBrowser.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", altered);
            using (var rejected = await actualBrowser.GetAsync("/api/tasks"))
                Check(rejected.StatusCode == HttpStatusCode.Unauthorized,
                    "tampering with HMAC browser bearer is denied");
            // Session authentication does not enable unsafe Windows execution.
            actualBrowser.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", shortToken);
            using var status = await actualBrowser.GetAsync("/api/status");
            using var json = await ReadJson(status);
            Check(!json.RootElement.GetProperty("executionEnabled").GetBoolean(),
                "zero-click browser auth does not turn on autonomous code execution");
        }
    }

    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", OwnerToken);
    using (var authenticatedStatus = await client.GetAsync("/api/status"))
    {
        Check(authenticatedStatus.StatusCode == HttpStatusCode.OK,
            "authenticated status available");
        using var status = await ReadJson(authenticatedStatus);
        Check(!status.RootElement.GetProperty("executionEnabled").GetBoolean(),
            "control plane cannot start the agent");
        Check(status.RootElement.GetProperty("model").GetString() == "qwen3.5:4b",
            "fixed local model reported");
    }

    var requestId = Guid.NewGuid().ToString("N");
    const string title = "Fix compiler errors";
    const string objective = "Fix the C# compiler errors while preserving behavior.";
    using (var bad = await Post(client, "/api/tasks", new
    {
        title, objective, requestId = "invalid"
    }))
        Check(bad.StatusCode == HttpStatusCode.BadRequest,
              "reject malformed idempotency key");

    string taskId;
    using (var created = await Post(client, "/api/tasks", new
    {
        title, objective, requestId
    }))
    {
        Check(created.StatusCode == HttpStatusCode.Created,
              "persist new task");
        using var body = await ReadJson(created);
        taskId = body.RootElement.GetProperty("id").GetString()!;
        Check(taskId.Length == 32, "opaque task identity");
        Check(body.RootElement.GetProperty("status").GetString() == "Draft",
              "new task starts as draft");
    }

    // First user-visible task path: real HTML text from the stored objective,
    // no M1 planning consent, agent, executable, model or repository mutation.
    using (var unavailable = await client.GetAsync($"/api/tasks/{taskId}/html"))
    using (var body = await ReadJson(unavailable))
    {
        Check(unavailable.StatusCode == HttpStatusCode.OK &&
              !body.RootElement.GetProperty("available").GetBoolean(),
            "new task has no falsely invented HTML result");
    }
    using (var badModel = await Post(client, $"/api/tasks/{taskId}/html",
        new { model = "remote/claude" }))
        Check(badModel.StatusCode == HttpStatusCode.BadRequest,
            "HTML generator only admits two explicit local models");

    using (var output = await Post(client, $"/api/tasks/{taskId}/html",
        new { model = "qwen3.5:4b" }))
    using (var body = await ReadJson(output))
    {
        Check(output.StatusCode == HttpStatusCode.Created,
            "task-driven HTML artifact saved without agent approval");
        Check(body.RootElement.GetProperty("html").GetString()!
                  .Contains("Welcome, New User", StringComparison.Ordinal),
            "first useful HTML file has actual content");
        Check(body.RootElement.GetProperty("sha256").GetString()!.Length == 64,
            "saved file is SHA256-identifiable");
    }
    Check(htmlGenerator.Calls == 1 && htmlGenerator.ObservedObjective == objective,
        "task's original user objective reaches the bounded local generator");
    using (var persisted = await client.GetAsync($"/api/tasks/{taskId}/html"))
    using (var body = await ReadJson(persisted))
        Check(persisted.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("available").GetBoolean() &&
              body.RootElement.GetProperty("artifact").GetProperty("html")
                .GetString()!.Contains("Welcome, New User"),
            "HTML artifact is actually durable, not a fake browser state");

    foreach (var unsafeHtml in new[] {
        "<!DOCTYPE html><html><body><script>alert(1)</script></body></html>",
        "<!DOCTYPE html><html><body><img src='https://remote.example/x'></body></html>",
        "<!DOCTYPE html><html><body><div onclick='alert(1)'>Go</div></body></html>",
    })
    {
        try
        {
            HtmlArtifactStore.Validate(unsafeHtml);
            throw new Exception("Unsafe generated HTML was accepted");
        }
        catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_REJECTED")
        {
            checks++;
        }
    }

    using (var duplicate = await Post(client, "/api/tasks", new
    {
        title, objective, requestId
    }))
    {
        Check(duplicate.StatusCode == HttpStatusCode.Created,
              "duplicate request ID accepted as same task");
        using var body = await ReadJson(duplicate);
        Check(body.RootElement.GetProperty("id").GetString() == taskId,
              "request replay returns identical task");
    }

    using (var conflictingReplay = await Post(client, "/api/tasks", new
    {
        title = "changed title", objective, requestId
    }))
        Check(conflictingReplay.StatusCode == HttpStatusCode.Conflict,
            "conflicting duplicate request rejected");

    using (var list = await client.GetAsync("/api/tasks"))
    {
        using var body = await ReadJson(list);
        Check(body.RootElement.GetArrayLength() == 1,
            "duplicate request did not create second task");
    }
    using (var getTask = await client.GetAsync("/api/tasks/" + taskId))
    {
        Check(getTask.StatusCode == HttpStatusCode.OK, "get persisted task");
        using var body = await ReadJson(getTask);
        Check(body.RootElement.GetProperty("task").GetProperty("title")
                .GetString() == title, "task title stored");
        Check(body.RootElement.GetProperty("plan").ValueKind == JsonValueKind.Null,
            "no unapproved plan fabricated");
    }

    string hash;
    using (var draft = await Post(client, $"/api/tasks/{taskId}/plans", new
    {
        summary = "Repair a disposable C# program and validate its build.",
        expectedVersion = 0
    }))
    {
        Check(draft.StatusCode == HttpStatusCode.Created, "draft plan v1");
        using var body = await ReadJson(draft);
        hash = body.RootElement.GetProperty("planHash").GetString()!;
        Check(hash.Length == 64, "plan has 64-character SHA256");
        Check(body.RootElement.GetProperty("version").GetInt32() == 1,
              "plan version starts at one");
        Check(body.RootElement.GetProperty("status").GetString() == "Pending",
              "plan requires approval");
        Check(!body.RootElement.GetProperty("executionEnabled").GetBoolean(),
              "plan is review-only");
    }

    using (var staleDraft = await Post(client, $"/api/tasks/{taskId}/plans", new
    {
        summary = "Duplicate stale plan", expectedVersion = 0
    }))
        Check(staleDraft.StatusCode == HttpStatusCode.Conflict,
              "stale plan edit rejected");
    using (var badApprove = await Post(client, $"/api/tasks/{taskId}/approvals", new
    {
        version = 1, approval = "APPROVE WRONG"
    }))
        Check(badApprove.StatusCode == HttpStatusCode.Conflict,
              "wrong approval string cannot authorize");

    var exactApproval = "APPROVE " + hash;
    using (var approved = await Post(client, $"/api/tasks/{taskId}/approvals", new
    {
        version = 1, approval = exactApproval
    }))
    {
        Check(approved.StatusCode == HttpStatusCode.OK,
              "exact plan approval persisted");
        using var body = await ReadJson(approved);
        Check(body.RootElement.GetProperty("status").GetString() == "Approved",
              "approved status returned");
        Check(!body.RootElement.GetProperty("executionEnabled").GetBoolean(),
              "approval does not grant execution");
    }
    using (var repeatApproval = await Post(client, $"/api/tasks/{taskId}/approvals", new
    {
        version = 1, approval = exactApproval
    }))
        Check(repeatApproval.StatusCode == HttpStatusCode.OK,
              "exact approval is idempotent");

    // Two concurrent reviewers both claim version 1. Exactly one can win;
    // the superseded version's approval must never carry forward.
    async Task<HttpStatusCode> Revise(string text)
    {
        using var response = await Post(client, $"/api/tasks/{taskId}/plans", new
        {
            summary = text, expectedVersion = 1
        });
        return response.StatusCode;
    }

    var proposals = await Task.WhenAll(
        Revise("Version 2: repair and verify C# build"),
        Revise("Version 2: alternative review-only plan"));
    Check(proposals.Count(s => s == HttpStatusCode.Created) == 1,
          "only one concurrent plan writer succeeds");
    Check(proposals.Count(s => s == HttpStatusCode.Conflict) == 1,
          "stale concurrent writer receives conflict");

    using (var oldApproval = await Post(client, $"/api/tasks/{taskId}/approvals", new
    {
        version = 1, approval = exactApproval
    }))
        Check(oldApproval.StatusCode == HttpStatusCode.Conflict,
              "old plan approval cannot authorize new version");
    using (var noExecution = await Post(client, $"/api/tasks/{taskId}/execute", new { }))
        Check(noExecution.StatusCode == HttpStatusCode.NotFound,
              "no model or container execution API available");

    using (var recentTask = await client.GetAsync("/api/tasks/" + taskId))
    {
        using var body = await ReadJson(recentTask);
        Check(body.RootElement.GetProperty("task").GetProperty("currentVersion")
                .GetInt32() == 2, "current plan version durable");
        Check(body.RootElement.GetProperty("task").GetProperty("status")
                .GetString() == "AwaitingApproval",
              "revision revokes previous approval");
        Check(body.RootElement.GetProperty("plan").GetProperty("status")
                .GetString() == "Pending", "revision needs fresh approval");
    }

    client.Dispose();
    client = null;
    await appInstance!.StopAsync();
    await appInstance.DisposeAsync();
    appInstance = null;

    // A completely new app instance must see the same SQLite state.
    client = await Start(settings, a => appInstance = a);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", OwnerToken);
    using (var restored = await client.GetAsync("/api/tasks/" + taskId))
    {
        using var body = await ReadJson(restored);
        Check(body.RootElement.GetProperty("task").GetProperty("currentVersion")
                .GetInt32() == 2, "task persists across restart");
        Check(body.RootElement.GetProperty("plan").GetProperty("version")
                .GetInt32() == 2, "latest plan survives restart");
    }

    // M2 #75: real HTTP + SQLite + SignalR event contract, no worker.
    string secondHash;
    using (var current = await client.GetAsync("/api/tasks/" + taskId))
    {
        using var body = await ReadJson(current);
        secondHash = body.RootElement.GetProperty("plan")
            .GetProperty("planHash").GetString()!;
    }
    var runRequestId = Guid.NewGuid().ToString("N");
    var intent = new { planVersion = 2, planHash = secondHash, requestId = runRequestId };

    using (var notYetApproved = await Post(client, $"/api/tasks/{taskId}/runs", intent))
        Check(notYetApproved.StatusCode == HttpStatusCode.Conflict,
            "pending plan cannot create run intent");

    using (var authorize = await Post(client, $"/api/tasks/{taskId}/approvals",
        new { version = 2, approval = "APPROVE " + secondHash }))
        Check(authorize.StatusCode == HttpStatusCode.OK,
            "version 2 exact planning approval persisted");

    using (var wrongHash = await Post(client, $"/api/tasks/{taskId}/runs",
        new { planVersion = 2, planHash = new string('0', 64),
              requestId = Guid.NewGuid().ToString("N") }))
        Check(wrongHash.StatusCode == HttpStatusCode.Conflict,
            "run intent rejects wrong approval hash");
    using (var oldPlan = await Post(client, $"/api/tasks/{taskId}/runs",
        new { planVersion = 1, planHash = hash,
              requestId = Guid.NewGuid().ToString("N") }))
        Check(oldPlan.StatusCode == HttpStatusCode.Conflict,
            "run intent rejects stale plan version");
    using (var badRequestId = await Post(client, $"/api/tasks/{taskId}/runs",
        new { planVersion = 2, planHash = secondHash, requestId = "../invalid" }))
        Check(badRequestId.StatusCode == HttpStatusCode.BadRequest,
            "run intent rejects malformed idempotency key");

    string runId;
    using (var createdIntent = await Post(client, $"/api/tasks/{taskId}/runs", intent))
    {
        Check(createdIntent.StatusCode == HttpStatusCode.Created,
            "run intent created by real HTTP API");
        using var body = await ReadJson(createdIntent);
        runId = body.RootElement.GetProperty("id").GetString()!;
        Check(body.RootElement.GetProperty("state").GetString() ==
              "AwaitingExecutionAuthorization",
              "new run is explicitly NOT queued for execution");
        Check(!body.RootElement.GetProperty("executionEnabled").GetBoolean(),
              "run cannot execute a model or container");
        Check(body.RootElement.GetProperty("lastSequence").GetInt64() == 1,
              "first event is durably assigned sequence 1");
        Check(body.RootElement.GetProperty("planCurrent").GetBoolean(),
              "new intent references current approved plan");
    }

    using (var duplicateIntent = await Post(client, $"/api/tasks/{taskId}/runs", intent))
    {
        using var body = await ReadJson(duplicateIntent);
        Check(body.RootElement.GetProperty("id").GetString() == runId,
              "identical request id does not create second run");
    }
    using (var changedIdempotency = await Post(client, $"/api/tasks/{taskId}/runs",
        new { planVersion = 2, planHash = new string('0', 64),
              requestId = runRequestId }))
        Check(changedIdempotency.StatusCode == HttpStatusCode.Conflict,
              "same request id cannot be repurposed");

    using (var runList = await client.GetAsync($"/api/tasks/{taskId}/runs"))
    {
        Check(runList.StatusCode == HttpStatusCode.OK,
              "authenticated task run history");
        using var body = await ReadJson(runList);
        Check(body.RootElement.GetArrayLength() == 1,
              "duplicate request has one durable run");
    }

    using (var unauthHub = new HttpClient { BaseAddress = client.BaseAddress })
    {
        using var negotiate = await Post(unauthHub,
            "/api/progress/negotiate?negotiateVersion=1", new { });
        Check(negotiate.StatusCode == HttpStatusCode.Unauthorized,
              "SignalR handshake requires owner bearer token");
        using var queryToken = await Post(unauthHub,
            "/api/progress/negotiate?negotiateVersion=1&access_token=" + OwnerToken,
            new { });
        Check(queryToken.StatusCode == HttpStatusCode.Unauthorized,
              "owner token in query string never authenticates hub");
        using var noEvent = await unauthHub.GetAsync($"/api/runs/{runId}/events");
        Check(noEvent.StatusCode == HttpStatusCode.Unauthorized,
              "event history cannot be read without authorization");
    }

    using (var initial = await client.GetAsync($"/api/runs/{runId}/events?after=0&limit=1"))
    {
        Check(initial.StatusCode == HttpStatusCode.OK, "run event page accessible");
        using var body = await ReadJson(initial);
        Check(body.RootElement.GetProperty("events").GetArrayLength() == 1,
              "run creation was persisted as event");
        Check(body.RootElement.GetProperty("events")[0].GetProperty("kind")
                .GetString() == "IntentCreated",
              "event category is fixed and bounded");
        Check(body.RootElement.GetProperty("nextCursor").GetInt64() == 1,
              "initial resume cursor is sequence 1");
    }
    using (var badCursor = await client.GetAsync($"/api/runs/{runId}/events?after=-1"))
        Check(badCursor.StatusCode == HttpStatusCode.BadRequest,
              "negative replay cursor rejected");
    using (var badLimit = await client.GetAsync($"/api/runs/{runId}/events?limit=9999"))
        Check(badLimit.StatusCode == HttpStatusCode.BadRequest,
              "unbounded event fetch rejected");
    using (var badRun = await client.GetAsync("/api/runs/../../invalid/events"))
        Check(badRun.StatusCode != HttpStatusCode.OK,
              "path traversal run ID never returns event history");

    await using (var hub = new HubConnectionBuilder()
        .WithUrl(new Uri(client.BaseAddress!, "/api/progress"), options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.SkipNegotiation = true;
            options.AccessTokenProvider = () => Task.FromResult<string?>(OwnerToken);
        })
        .Build())
    {
        var streamed = new TaskCompletionSource<RunEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = hub.On<RunEvent>("RunUpdated", change =>
        {
            if (change.RunId == runId && change.Kind == "IntentCancelled")
                streamed.TrySetResult(change);
        });
        await hub.StartAsync();
        Check(hub.State == HubConnectionState.Connected,
              "authenticated SignalR websocket connected");
        var subscribedCursor = await hub.InvokeAsync<long>("WatchRun", runId);
        Check(subscribedCursor == 1,
              "subscribing returns current durable sequence for replay");

        using (var cancel = await Post(client, $"/api/runs/{runId}/cancel", new { }))
        {
            Check(cancel.StatusCode == HttpStatusCode.OK,
                  "run intent may be cancelled explicitly");
            using var body = await ReadJson(cancel);
            Check(body.RootElement.GetProperty("state").GetString() == "Cancelled",
                  "cancellation state persisted");
            Check(!body.RootElement.GetProperty("executionEnabled").GetBoolean(),
                  "cancelling intent does not imply worker involvement");
        }
        var notification = await streamed.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Check(notification.Sequence == 2 &&
              notification.Kind == "IntentCancelled" &&
              notification.State == "Cancelled",
              "SignalR delivered same fixed-shape event persisted in SQLite");
        await hub.InvokeAsync("UnwatchRun", runId);
        await hub.StopAsync();
    }

    using (var replay = await client.GetAsync($"/api/runs/{runId}/events?after=1"))
    {
        using var body = await ReadJson(replay);
        Check(body.RootElement.GetProperty("events").GetArrayLength() == 1,
              "replay catches up after missed first event");
        Check(body.RootElement.GetProperty("events")[0].GetProperty("sequence")
                .GetInt64() == 2, "replayed event sequence is monotonic");
        Check(body.RootElement.GetProperty("nextCursor").GetInt64() == 2,
              "client can persist exact cursor");
    }
    using (var emptyReplay = await client.GetAsync($"/api/runs/{runId}/events?after=2"))
    {
        using var body = await ReadJson(emptyReplay);
        Check(body.RootElement.GetProperty("events").GetArrayLength() == 0,
              "repeated replay is empty and idempotent");
        Check(body.RootElement.GetProperty("nextCursor").GetInt64() == 2,
              "empty replay preserves cursor");
    }
    using (var cancelAgain = await Post(client, $"/api/runs/{runId}/cancel", new { }))
    {
        using var body = await ReadJson(cancelAgain);
        Check(body.RootElement.GetProperty("lastSequence").GetInt64() == 2,
              "repeated cancellation cannot add duplicate event");
    }

    // Even if an operator revises a plan, an old run does not carry approval
    // forward and cannot silently become an executable queue item.
    using (var newerPlan = await Post(client, $"/api/tasks/{taskId}/plans",
        new { summary = "New reviewed plan, separate authorization",
              expectedVersion = 2 }))
        Check(newerPlan.StatusCode == HttpStatusCode.Created,
              "revision after run intent is accepted separately");
    using (var staleRun = await client.GetAsync($"/api/runs/{runId}"))
    {
        using var body = await ReadJson(staleRun);
        Check(!body.RootElement.GetProperty("planCurrent").GetBoolean(),
              "run intent visibly references a superseded plan");
        Check(!body.RootElement.GetProperty("executionEnabled").GetBoolean(),
              "superseded run never receives execution permission");
    }

    // Prove events survive the process/network restart, not just SignalR.
    client.Dispose();
    client = null;
    await appInstance!.StopAsync();
    await appInstance.DisposeAsync();
    appInstance = null;
    client = await Start(settings, a => appInstance = a);
    client.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer", OwnerToken);
    using (var replayAfterRestart =
        await client.GetAsync($"/api/runs/{runId}/events?after=0&limit=1"))
    {
        using var body = await ReadJson(replayAfterRestart);
        Check(body.RootElement.GetProperty("events").GetArrayLength() == 1,
              "durable event available after API restart");
        Check(body.RootElement.GetProperty("nextCursor").GetInt64() == 1,
              "new client can page from a saved cursor");
    }
    using (var recoveredRun = await client.GetAsync($"/api/runs/{runId}"))
    {
        using var body = await ReadJson(recoveredRun);
        Check(body.RootElement.GetProperty("state").GetString() == "Cancelled",
              "cancelled run state survives restart");
        Check(body.RootElement.GetProperty("lastSequence").GetInt64() == 2,
              "event head survives restart without false terminal success");
    }


    // M2→M1 model-free HTTP acceptance. The fake runner produces only
    // strict fixed-fixture plan metadata; no Podman, Ollama or coding agent.
    WebApplication? executionApp = null;
    HttpClient? executionClient = null;
    var fake = new SmokeM1Runner();
    try
    {
        executionClient = await Start(new ApiSettings(
            Path.Combine(root, "execution-private"), OwnerToken,
            "http://127.0.0.1:0", EnableLocalExecution: true),
            app => executionApp = app, fake);
        executionClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", OwnerToken);
        using (var status = await executionClient.GetAsync("/api/status"))
        using (var json = await ReadJson(status))
            Check(json.RootElement.GetProperty("executionEnabled").GetBoolean(),
                "explicitly enabled isolated adapter reports real execution capability");
        string executionTaskId;
        using (var created = await Post(executionClient, "/api/tasks", new {
            title = "Approved C# disposable repair", objective = "Fix compilation",
            requestId = Guid.NewGuid().ToString("N")
        }))
        using (var json = await ReadJson(created))
            executionTaskId = json.RootElement.GetProperty("id").GetString()!;
        string controlHash;
        using (var planned = await Post(executionClient,
            $"/api/tasks/{executionTaskId}/plans", new {
                summary = "Fixed qualified M1 C# repair", expectedVersion = 0
            }))
        using (var json = await ReadJson(planned))
            controlHash = json.RootElement.GetProperty("planHash").GetString()!;
        using (var approved = await Post(executionClient,
            $"/api/tasks/{executionTaskId}/approvals", new {
                version = 1, approval = "APPROVE " + controlHash
            }))
            Check(approved.StatusCode == HttpStatusCode.OK,
                "planning approved independently of executable scope");

        string executionRunId;
        using (var intentCreated = await Post(executionClient,
            $"/api/tasks/{executionTaskId}/runs", new {
                planVersion = 1, planHash = controlHash,
                requestId = Guid.NewGuid().ToString("N")
            }))
        using (var json = await ReadJson(intentCreated))
            executionRunId = json.RootElement.GetProperty("id").GetString()!;

        using (var notPreviewed = await Post(executionClient,
            $"/api/runs/{executionRunId}/execution-authorization",
            new { approval = "APPROVE " + controlHash }))
            Check(notPreviewed.StatusCode == HttpStatusCode.NotFound,
                "control-plane approval cannot authorize an absent M1 executable plan");
        using (var noStart = await Post(executionClient,
            $"/api/runs/{executionRunId}/start", new { }))
            Check(noStart.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict,
                "no M1 agent admission before exact executable approval");
        Check(fake.Executions == 0, "no worker was started by planning consent");

        string executableHash;
        using (var previewed = await Post(executionClient,
            $"/api/runs/{executionRunId}/execution-plan", new { }))
        using (var json = await ReadJson(previewed))
        {
            Check(previewed.StatusCode == HttpStatusCode.OK,
                "model-free M1 immutable executable preview stored");
            executableHash = json.RootElement.GetProperty("planHash").GetString()!;
            Check(executableHash != controlHash,
                "M1 execution hash never aliases M2 control-plane hash");
            Check(json.RootElement.GetProperty("state").GetString() == "Previewed",
                "M1 scope awaits separate approval");
        }
        using (var noModel = await Post(executionClient,
            $"/api/runs/{executionRunId}/execution-authorization",
            new { approval = "APPROVE " + controlHash }))
            Check(noModel.StatusCode == HttpStatusCode.Conflict,
                "M2 approval cannot substitute for M1 exact executable approval");
        using (var noChange = await Post(executionClient,
            $"/api/runs/{executionRunId}/execution-plan", new { }))
            Check(noChange.StatusCode == HttpStatusCode.OK,
                "matching execution preview is idempotent");
        using (var auth = await Post(executionClient,
            $"/api/runs/{executionRunId}/execution-authorization",
            new { approval = "APPROVE " + executableHash }))
            Check(auth.StatusCode == HttpStatusCode.OK,
                "owner's independent M1 exact hash approval recorded");
        Check(fake.Executions == 0,
            "separate explicit start remains required after M1 approval");
        using (var started = await Post(executionClient,
            $"/api/runs/{executionRunId}/start", new { }))
            Check(started.StatusCode == HttpStatusCode.Accepted,
                "single authorized worker was durably admitted");
        using (var doubleStart = await Post(executionClient,
            $"/api/runs/{executionRunId}/start", new { }))
            Check(doubleStart.StatusCode == HttpStatusCode.Conflict,
                "duplicate worker admission denied");
        for (var retry = 0; retry < 100; retry++)
        {
            using var current = await executionClient.GetAsync($"/api/runs/{executionRunId}");
            using var json = await ReadJson(current);
            if (json.RootElement.GetProperty("state").GetString() == "Succeeded")
                break;
            await Task.Delay(30);
        }
        using (var finalRun = await executionClient.GetAsync($"/api/runs/{executionRunId}"))
        using (var json = await ReadJson(finalRun))
        {
            Check(json.RootElement.GetProperty("state").GetString() == "Succeeded",
                "independent runner verification becomes durable terminal state");
            Check(!json.RootElement.GetProperty("executionEnabled").GetBoolean(),
                "completed runner is no longer enabled");
            Check(json.RootElement.GetProperty("lastSequence").GetInt64() == 8,
                "all eight cross-table events including fixed milestones are numbered");
        }
        Check(fake.Executions == 1, "one fake worker, no retries");
        using (var evidence = await executionClient.GetAsync(
            $"/api/runs/{executionRunId}/result"))
        using (var json = await ReadJson(evidence))
        {
            Check(evidence.StatusCode == HttpStatusCode.OK,
                "authenticated run output is available through the real API");
            Check(json.RootElement.GetProperty("available").GetBoolean(),
                "model-free saved evidence can be fetched after execution");
            var output = json.RootElement.GetProperty("result");
            Check(output.GetProperty("evaluatorVerified").GetBoolean() &&
                  output.GetProperty("sourceChanged").GetBoolean() &&
                  output.GetProperty("inferenceRequests").GetInt32() == 4,
                "UI gets bounded independent evaluator and local inference counts");
            Check(output.GetProperty("patch").GetString()!
                  .StartsWith("--- a/api/Program.cs", StringComparison.Ordinal),
                "only the approved disposable C# patch is available");
        }
        using (var history = await executionClient.GetAsync(
            $"/api/runs/{executionRunId}/events?after=0"))
        using (var json = await ReadJson(history))
        {
            var events = json.RootElement.GetProperty("events");
            Check(events.GetArrayLength() == 8,
                "original run intent plus fixed milestones replayed together");
            Check(events[5].GetProperty("kind").GetString() == "ExecutionStageGatewayReady" &&
                  events[6].GetProperty("kind").GetString() == "ExecutionStageCodingRequestSent",
                "only fixed runner milestones are persisted, not model messages");
            Check(events[7].GetProperty("kind").GetString() == "ExecutionVerified",
                "success event is only emitted after verified runner outcome");
            Check(events[0].GetProperty("sequence").GetInt64() == 1 &&
                  events[7].GetProperty("sequence").GetInt64() == 8,
                "events are gapless from intent to independent verification");
        }
        // A failed cleanup is a SECURITY BLOCKER for the entire host, not
        // permission to admit the next model run in another run intent.
        async Task<string> AuthorizedRun()
        {
            string id;
            using (var intent = await Post(executionClient,
                $"/api/tasks/{executionTaskId}/runs", new {
                    planVersion = 1, planHash = controlHash,
                    requestId = Guid.NewGuid().ToString("N")
                }))
            using (var json = await ReadJson(intent))
                id = json.RootElement.GetProperty("id").GetString()!;
            string hash;
            using (var preview = await Post(executionClient,
                $"/api/runs/{id}/execution-plan", new { }))
            using (var json = await ReadJson(preview))
                hash = json.RootElement.GetProperty("planHash").GetString()!;
            using (var auth = await Post(executionClient,
                $"/api/runs/{id}/execution-authorization",
                new { approval = "APPROVE " + hash }))
                Check(auth.StatusCode == HttpStatusCode.OK,
                      "independent execution consent required per new run");
            return id;
        }

        var blockedRunId = await AuthorizedRun();
        fake.BlockOnExecute = true;
        fake.CleanupIsVerified = false;
        using (var starting = await Post(executionClient,
            $"/api/runs/{blockedRunId}/start", new { }))
            Check(starting.StatusCode == HttpStatusCode.Accepted,
                  "admit distinct exactly approved fixed-fixture worker");

        for (var retry = 0; retry < 100 && fake.Executions < 2; retry++)
            await Task.Delay(20);
        Check(fake.Executions == 2,
              "the cancellation test has a genuine active fake worker");
        var queuedRunId = await AuthorizedRun();
        using (var busy = await Post(executionClient,
            $"/api/runs/{queuedRunId}/start", new { }))
            Check(busy.StatusCode == HttpStatusCode.Conflict,
                  "concurrent second owner task cannot start another worker");
        using (var cancel = await Post(executionClient,
            $"/api/runs/{blockedRunId}/execution-cancel", new { }))
            Check(cancel.StatusCode == HttpStatusCode.OK,
                  "cancel targets exact active M1 run");
        for (var retry = 0; retry < 100; retry++)
        {
            using var current = await executionClient.GetAsync($"/api/runs/{blockedRunId}");
            using var json = await ReadJson(current);
            if (json.RootElement.GetProperty("state").GetString() == "RecoveryBlocked")
                break;
            await Task.Delay(30);
        }
        using (var uncertain = await executionClient.GetAsync($"/api/runs/{blockedRunId}"))
        using (var json = await ReadJson(uncertain))
            Check(json.RootElement.GetProperty("state").GetString() == "RecoveryBlocked",
                "unknown cleanup never reports Cancelled or Succeeded");
        using (var stillBusy = await Post(executionClient,
            $"/api/runs/{queuedRunId}/start", new { }))
            Check(stillBusy.StatusCode == HttpStatusCode.Conflict,
                  "unverified orphan blocks new execution admission");
        fake.CleanupIsVerified = true;
        using (var recovered = await Post(executionClient,
            $"/api/runs/{blockedRunId}/execution-recover", new { }))
        using (var json = await ReadJson(recovered))
            Check(json.RootElement.GetProperty("state").GetString() == "Interrupted",
                "explicit model-free reconciliation is not reported as success");
        fake.BlockOnExecute = false;
        using (var resumed = await Post(executionClient,
            $"/api/runs/{queuedRunId}/start", new { }))
            Check(resumed.StatusCode == HttpStatusCode.Accepted,
                  "separate approved run can start only after verified recovery");

        // A new run is never a replay of the interrupted one.
        for (var retry = 0; retry < 100; retry++)
        {
            using var current = await executionClient.GetAsync($"/api/runs/{queuedRunId}");
            using var json = await ReadJson(current);
            if (json.RootElement.GetProperty("state").GetString() == "Succeeded")
                break;
            await Task.Delay(30);
        }
        Check(fake.Executions == 3, "interrupted run was not automatically retried");
        using (var recoveredOriginal =
            await executionClient.GetAsync($"/api/runs/{blockedRunId}"))
        using (var json = await ReadJson(recoveredOriginal))
            Check(json.RootElement.GetProperty("state").GetString() == "Interrupted",
                "interrupted run remains interrupted after another succeeds");

        // Strict plan parsing refuses request-scope escalation independent of
        // the JSON hash. These model-free contract cases are NOT substitutes
        // for the mandatory real Podman adversarial cases on the Dell.
        var verifiedRun = executionApp!.Services
            .GetRequiredService<RunStore>().Get(executionRunId);
        var verifiedPreview = await fake.PreviewAsync(verifiedRun, CancellationToken.None);
        M1ProcessRunner.VerifyExactBoundPlan(
            verifiedRun, verifiedPreview.Plan, verifiedPreview.PlanHash);
        Check(true, "known fixed-fixture plan accepted by independent parser");
        var hostilePlans = new (string Name, Action<JsonNode> Mutate)[] {
            ("parent directory traversal", n => n["writable_files"]![0] = "../../etc/passwd"),
            ("absolute host path", n => n["writable_files"]![0] = "/home/owner/.ssh"),
            ("arbitrary symlink target", n => n["writable_files"]![0] = "api/link-to-secrets"),
            ("cloud-model substitution", n => n["model"] = "anthropic/claude"),
            ("model fingerprint absent", n => n["model_digest"] = "latest"),
            ("mutable runtime image", n => n["image_id"] = "latest"),
            ("evaluator omitted", n => n["evaluator_image_id"] = "bad"),
            ("unapproved gateway command", n => n["gateway_method"] = "shell.exec"),
            ("Git remote escape", n => n["no_git_remote"] = false),
            ("network/cloud escape", n => n["no_cloud"] = false),
            ("wrong M2 task", n => n["control"]!["task_id"] = new string('0', 32)),
            ("wrong M2 run", n => n["control"]!["run_id"] = new string('1', 32)),
            ("stale planning version", n => n["control"]!["plan_version"] = 999),
            ("wrong M2 planning approval", n => n["control"]!["plan_hash"] = new string('0', 64)),
            ("extra child-process grant", n => n.AsObject().Add("allow_shell", true)),
            ("additional control grant", n => n["control"]!.AsObject().Add("extra", true)),
            ("changed assignment", n => n["assignment"] = "modify host OS"),
        };
        foreach (var (caseName, mutate) in hostilePlans)
        {
            var node = JsonNode.Parse(verifiedPreview.Plan.GetRawText())!;
            mutate(node);
            var tampered = JsonSerializer.SerializeToElement(node);
            var tamperedHash = M1ProcessRunner.ComputeCanonicalHash(tampered);
            try
            {
                M1ProcessRunner.VerifyExactBoundPlan(verifiedRun, tampered, tamperedHash);
                throw new Exception("Hostile M1 plan accepted: " + caseName);
            }
            catch (ExecutionUnavailable)
            {
                checks++;
            }
        }

        // Archived run output is a fixed, hash-bound subset of the owner
        // private M1 ledger. Any wrong ID/scope or raw-model-output field
        // must fail without producing an apparent verified report.
        var sampleResult = JsonSerializer.Serialize(new
        {
            schema = "codingassistant-run-result-v1",
            available = true,
            runId = verifiedRun.Id,
            planHash = verifiedPreview.PlanHash,
            status = "GATEWAY_APPROVED_CODING_PASS",
            phase = "complete",
            reasonCode = (string?)null,
            evaluatorVerified = (bool?)true,
            sourceChanged = (bool?)true,
            inferenceRequests = (int?)4,
            patch = "--- a/api/Program.cs\n+++ b/api/Program.cs\n@@ -1 +1 @@\n-old\n+new\n",
            patchOmitted = false
        });
        Check(M1ProcessRunner.ParseResult(sampleResult,
                  verifiedRun.Id, verifiedPreview.PlanHash)?.EvaluatorVerified == true,
            "bound fixed-fixture result accepted for owner view");
        foreach (var (badId, badHash) in new[] {
            (new string('f', 32), verifiedPreview.PlanHash),
            (verifiedRun.Id, new string('f', 64))
        })
        {
            try
            {
                M1ProcessRunner.ParseResult(sampleResult, badId, badHash);
                throw new Exception("Unbound archived output was accepted");
            }
            catch (ExecutionUnavailable)
            {
                checks++;
            }
        }

        // A stale M2 plan must block further execution previews.
        string staleRunId;
        using (var created = await Post(executionClient,
            $"/api/tasks/{executionTaskId}/runs", new {
                planVersion = 1, planHash = controlHash,
                requestId = Guid.NewGuid().ToString("N")
            }))
        using (var json = await ReadJson(created))
            staleRunId = json.RootElement.GetProperty("id").GetString()!;
        using (var newer = await Post(executionClient,
            $"/api/tasks/{executionTaskId}/plans", new {
                summary = "Must explicitly reapprove", expectedVersion = 1
            }))
            Check(newer.StatusCode == HttpStatusCode.Created,
                "M2 plan revision invalidates stale intent");
        using (var stalePreview = await Post(executionClient,
            $"/api/runs/{staleRunId}/execution-plan", new { }))
            Check(stalePreview.StatusCode == HttpStatusCode.Conflict,
                "superseded M2 plan cannot create an executable preview");
        Check(fake.Executions == 3,
            "no agent was started for rejected stale executable scope");

        await executionApp!.StopAsync();
        await executionApp.DisposeAsync();
        executionApp = null;
        executionClient.Dispose();
        executionClient = null;
    }
    finally
    {
        executionClient?.Dispose();
        if (executionApp is not null)
        {
            await executionApp.StopAsync();
            await executionApp.DisposeAsync();
        }
    }

    // First conversation-first, real API + SQLite vertical slice:
    // an arbitrary task objective drives adaptive model-supplied questions,
    // the owner's answers, a versioned specification, and exact approval.
    // The fake proves orchestration only; real Ollama acceptance is Dell-only.
    var interviewData = Path.Combine(root, "interview-private");
    var interviewSettings = new ApiSettings(
        interviewData, OwnerToken, "http://127.0.0.1:0");
    var fakeInterview = new SmokeInterviewModel();
    var fakeProject = new SmokeProjectDraftModel();
    var fakeCatalog = new SmokeOllamaModelCatalog();
    WebApplication? interviewApp = null;
    HttpClient? interviewClient = null;
    try
    {
        interviewClient = await Start(interviewSettings,
            app => interviewApp = app, interviewModel: fakeInterview,
            projectModel: fakeProject, catalog: fakeCatalog);
        interviewClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", OwnerToken);
        string id;
        const string ownerObjective =
            "Build a personal finance app with monthly budgets and editable categories";
        using (var created = await Post(interviewClient, "/api/tasks", new {
            title = "My own requested app",
            objective = ownerObjective,
            requestId = Guid.NewGuid().ToString("N")
        }))
        using (var json = await ReadJson(created))
        {
            Check(created.StatusCode == HttpStatusCode.Created,
                "arbitrary interview task persists");
            id = json.RootElement.GetProperty("id").GetString()!;
        }
        // The picker uses this authenticated endpoint, not a hard-coded list.
        using (var catalogResponse = await interviewClient.GetAsync("/api/models"))
        using (var catalogJson = await ReadJson(catalogResponse))
            Check(catalogResponse.StatusCode == HttpStatusCode.OK &&
                  catalogJson.RootElement.GetProperty("models").EnumerateArray()
                    .Select(m => m.GetString()).SequenceEqual(
                        new[] { "gemma4:latest", "qwen2.5-coder:3b", "qwen3.5:4b" }),
                "authenticated catalog returns current installed local models");
        using (var anonymous = new HttpClient(new SocketsHttpHandler {
            UseProxy = false
        }) { BaseAddress = interviewClient.BaseAddress })
        using (var denied = await anonymous.GetAsync("/api/models"))
            Check(denied.StatusCode == HttpStatusCode.Unauthorized,
                "model catalog cannot be read without owner authentication");
        using (var empty = await interviewClient.GetAsync($"/api/tasks/{id}/interview"))
        using (var json = await ReadJson(empty))
            Check(empty.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("lastSequence").GetInt32() == 0,
                "interview starts empty, without hard-coded questions");
        using (var wrongModel = await Post(interviewClient,
            $"/api/tasks/{id}/interview/turn", new {
                expectedSequence = 0, model = "cloud/model"
            }))
            Check(wrongModel.StatusCode == HttpStatusCode.BadRequest,
                "interview rejects remote model before an LLM call");
        using (var first = await Post(interviewClient,
            $"/api/tasks/{id}/interview/turn", new {
                expectedSequence = 0, model = "qwen3.5:4b"
            }))
        using (var json = await ReadJson(first))
        {
            Check(first.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("lastSequence").GetInt32() == 1 &&
                  json.RootElement.GetProperty("messages")[0]
                    .GetProperty("content").GetString()!
                    .Contains("budgets", StringComparison.OrdinalIgnoreCase),
                "first dynamic interview question is persisted");
        }
        using (var noAnswersSpec = await Post(interviewClient,
            $"/api/tasks/{id}/interview/specification", new {
                expectedSequence = 1
            }))
            Check(noAnswersSpec.StatusCode == HttpStatusCode.Conflict,
                "specification cannot be drafted without owner answers");
        using (var answer = await Post(interviewClient,
            $"/api/tasks/{id}/interview/turn", new {
                expectedSequence = 1,
                message = "Individual users, no bank integrations, C# and React."
            }))
        using (var json = await ReadJson(answer))
            Check(answer.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("lastSequence").GetInt32() == 3,
                "second context-aware question and answer persisted atomically");
        using (var stale = await Post(interviewClient,
            $"/api/tasks/{id}/interview/turn", new {
                expectedSequence = 1,
                message = "Stale repeated owner answer"
            }))
            Check(stale.StatusCode == HttpStatusCode.Conflict,
                "duplicate/interleaved owner answer cannot overwrite interview");
        using (var answer2 = await Post(interviewClient,
            $"/api/tasks/{id}/interview/turn", new {
                expectedSequence = 3,
                message = "Provide a monthly budget editor and expense reports."
            }))
        using (var json = await ReadJson(answer2))
            Check(answer2.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("readyForSpecification").GetBoolean(),
                "model signals readiness after actual interview");
        string specHash;
        int specVersion;
        using (var spec = await Post(interviewClient,
            $"/api/tasks/{id}/interview/specification", new {
                expectedSequence = 5
            }))
        using (var json = await ReadJson(spec))
        {
            Check(spec.StatusCode == HttpStatusCode.OK,
                "LLM-authored spec persists through authenticated API");
            var value = json.RootElement.GetProperty("specification");
            specHash = value.GetProperty("sha256").GetString()!;
            specVersion = value.GetProperty("version").GetInt32();
            Check(specHash.Length == 64 && specVersion == 1 &&
                  value.GetProperty("status").GetString() == "Draft",
                "reviewable v1 specification is SHA-256 bound");
        }
        // The correct version and hash are insufficient without explicit
        // human approval: the model cannot authorize its own source draft.
        using (var prematureDraft = await Post(interviewClient,
            $"/api/tasks/{id}/draft-build", new {
                specificationVersion = specVersion,
                specificationSha256 = specHash
            }))
            Check(prematureDraft.StatusCode == HttpStatusCode.Conflict,
                "unapproved specification cannot start source generation");

        using (var wrongApproval = await Post(interviewClient,
            $"/api/tasks/{id}/interview/approve", new {
                version = specVersion, sha256 = new string('f', 64)
            }))
            Check(wrongApproval.StatusCode == HttpStatusCode.Conflict,
                "unbound specification consent rejected");
        using (var approve = await Post(interviewClient,
            $"/api/tasks/{id}/interview/approve", new {
                version = specVersion, sha256 = specHash
            }))
        using (var json = await ReadJson(approve))
            Check(approve.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("specification")
                    .GetProperty("status").GetString() == "Approved",
                "owner's exact approved specification saved");
        Check(fakeInterview.AskCount == 3 &&
              fakeInterview.SpecCount == 1 &&
              fakeInterview.LastObjective == ownerObjective,
            "real task objective, not C# fixture, reached model interview");

        // An alternate installed model is allowed for a NEW project without
        // weakening the fixed, separate M1 executable coding model pin.
        string alternateId;
        using (var created = await Post(interviewClient, "/api/tasks", new {
            title = "Alternative local model",
            objective = "Create a note-taking CLI with searchable recent notes",
            requestId = Guid.NewGuid().ToString("N")
        }))
        using (var json = await ReadJson(created))
            alternateId = json.RootElement.GetProperty("id").GetString()!;
        using (var first = await Post(interviewClient,
            $"/api/tasks/{alternateId}/interview/turn", new {
                expectedSequence = 0, model = "gemma4:latest"
            }))
        using (var json = await ReadJson(first))
            Check(first.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("model").GetString() == "gemma4:latest",
                "user-selected installed model is preserved in the new interview");
        var beforeMissing = fakeInterview.AskCount;
        fakeCatalog.Models.Remove("gemma4:latest");
        using (var removed = await Post(interviewClient,
            $"/api/tasks/{alternateId}/interview/turn", new {
                expectedSequence = 1, message = "Local only", model = "gemma4:latest"
            }))
            Check(removed.StatusCode == HttpStatusCode.ServiceUnavailable &&
                  fakeInterview.AskCount == beforeMissing,
                "uninstalled model fails closed without sending another model request");
        using (var blocked = await Post(interviewClient,
            $"/api/tasks/{alternateId}/interview/turn", new {
                expectedSequence = 1, message = "Local only", model = "qwen3.5:4b"
            }))
            Check(blocked.StatusCode == HttpStatusCode.BadRequest &&
                  fakeInterview.AskCount == beforeMissing,
                "existing conversation cannot silently switch to another model");
        fakeCatalog.Models.Add("gemma4:latest");

        // No fake static HTML exercise here: approved specification drives
        // file choice and real persisted event/file state. The fake produces
        // text only; no command, container, package install or code execution.
        using (var emptyDraft = await interviewClient.GetAsync(
            $"/api/tasks/{id}/draft-build"))
        using (var json = await ReadJson(emptyDraft))
            Check(emptyDraft.StatusCode == HttpStatusCode.OK &&
                  !json.RootElement.GetProperty("available").GetBoolean(),
                "no invented generated files before owner starts a draft build");

        using (var wrongDraft = await Post(interviewClient,
            $"/api/tasks/{id}/draft-build", new {
                specificationVersion = specVersion,
                specificationSha256 = new string('f', 64)
            }))
            Check(wrongDraft.StatusCode == HttpStatusCode.Conflict,
                "draft builder rejects an unapproved or substituted spec hash");

        string draftId;
        using (var started = await Post(interviewClient,
            $"/api/tasks/{id}/draft-build", new {
                specificationVersion = specVersion,
                specificationSha256 = specHash
            }))
        using (var json = await ReadJson(started))
        {
            Check(started.StatusCode == HttpStatusCode.Accepted,
                "explicit owner action admitted a source-only draft job");
            draftId = json.RootElement.GetProperty("id").GetString()!;
        }
        JsonDocument? finishedJson = null;
        for (var attempt = 0; attempt < 120; attempt++)
        {
            using var get = await interviewClient.GetAsync(
                $"/api/tasks/{id}/draft-build");
            var doc = await ReadJson(get);
            if (doc.RootElement.GetProperty("snapshot").GetProperty("job")
                  .GetProperty("state").GetString() != "Running")
            {
                finishedJson = doc;
                break;
            }
            doc.Dispose();
            await Task.Delay(30);
        }
        if (finishedJson is null)
            throw new Exception("Source-only draft did not reach terminal status");
        using (finishedJson)
        {
            var body = finishedJson.RootElement.GetProperty("snapshot");
            var job = body.GetProperty("job");
            Check(job.GetProperty("state").GetString() == "Generated" &&
                  job.GetProperty("id").GetString() == draftId,
                "real source-text generation finished and was persisted");
            var files = body.GetProperty("files");
            Check(files.GetArrayLength() == 2 &&
                  files.EnumerateArray().Any(item =>
                      item.GetProperty("path").GetString() == "src/Dashboard.tsx") &&
                  files.EnumerateArray().Any(item =>
                      item.GetProperty("path").GetString() == "README.md"),
                "specification-specific files, not a fixed C# repair, were saved");
            var events = body.GetProperty("events");
            Check(events.GetArrayLength() >= 7 &&
                  events.EnumerateArray().Any(ev =>
                    ev.GetProperty("kind").GetString() == "FileStarted") &&
                  events.EnumerateArray().Count(ev =>
                    ev.GetProperty("kind").GetString() == "FileSaved") == 2 &&
                  events.EnumerateArray().Count(ev =>
                    ev.GetProperty("kind").GetString() == "ModelOutputReceived") == 3,
                "actual file starts/completions have timestamped durable events");
        }
        Check(fakeProject.PlanCount == 1 &&
              fakeProject.WriteCount == 2 &&
              fakeProject.ProgressCount == 3 &&
              fakeProject.LastObjective == ownerObjective &&
              fakeProject.LastSpecification.Contains("Individual users"),
            "draft builder consumed actual owner's approved specification");

        // Owner can download exact immutable task-specific agent contract,
        // but no coding process is launched by these read-only endpoints.
        string previewSha;
        using (var preview = await interviewClient.GetAsync(
            $"/api/tasks/{id}/agent-contract-preview"))
        using (var body = await ReadJson(preview))
        {
            Check(preview.StatusCode == HttpStatusCode.OK &&
                  !body.RootElement.GetProperty("executionEnabled").GetBoolean(),
                "task-specific agent preview cannot start executable work");
            previewSha = body.RootElement.GetProperty("contractSha256")
                .GetString()!;
            Check(body.RootElement.GetProperty("contract")
                      .GetProperty("objective").GetString() == ownerObjective &&
                  body.RootElement.GetProperty("contract")
                      .GetProperty("approvedSpecificationSha256").GetString()
                        == specHash,
                "contract originates in exact current approved user objective");
        }
        using (var contract = await interviewClient.GetAsync(
            $"/api/tasks/{id}/agent-contract-preview/json"))
        {
            var bytes = await contract.Content.ReadAsByteArrayAsync();
            var digest = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(bytes))
                .ToLowerInvariant();
            Check(contract.StatusCode == HttpStatusCode.OK &&
                  contract.Content.Headers.ContentType?.MediaType ==
                    "application/json" && digest == previewSha,
                "downloaded byte-exact contract matches authenticated preview");
            using var body = JsonDocument.Parse(bytes);
            Check(body.RootElement.GetProperty("files").GetArrayLength() == 2 &&
                  body.RootElement.GetProperty("taskId").GetString() == id,
                "download file binds actual task and chosen file plan");
        }

        // A React source-text draft cannot be misrepresented as qualified
        // executable .NET agent scope. Preparing it launches no model or tools.
        using (var wrongProfile = await Post(interviewClient,
            $"/api/tasks/{id}/task-build", new {
                requestId = Guid.NewGuid().ToString("N")
            }))
        using (var body = await ReadJson(wrongProfile))
            Check(wrongProfile.StatusCode == HttpStatusCode.BadRequest &&
                  body.RootElement.GetProperty("error").GetString() ==
                    "TASK_PROFILE_UNSUPPORTED",
                "new coding path rejects a React plan outside the .NET pilot profile");

        // Seed a SECOND small .NET fixture into the owner-private stores to
        // exercise exact transaction/HTTP semantics. This is model-free,
        // cannot start OpenCode, and is NOT runtime qualification evidence.
        string dotnetTaskId;
        const string dotnetObjective =
            "Implement a small offline C# inventory application with unit tests";
        using (var created = await Post(interviewClient, "/api/tasks", new {
            title = "Scoped .NET build review",
            objective = dotnetObjective,
            requestId = Guid.NewGuid().ToString("N")
        }))
        using (var json = await ReadJson(created))
        {
            Check(created.StatusCode == HttpStatusCode.Created,
                "second independently scoped .NET task created");
            dotnetTaskId = json.RootElement.GetProperty("id").GetString()!;
        }
        const string approvedDotnetSpec =
            "Build a .NET 10 offline inventory console application. " +
            "Store products in memory and support adding, removing and updating " +
            "inventory quantities. Include meaningful automated tests that " +
            "verify inventory calculations, rejection of invalid values and " +
            "correct removal. Do not connect to external services or install " +
            "packages from the public network.";
        var specIdentity = $"codingassistant-spec-v1\n{dotnetTaskId}\n1\n{approvedDotnetSpec}";
        var dotnetSpecSha = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(specIdentity))).ToLowerInvariant();
        var jobId = Guid.NewGuid().ToString("N");
        var createdUtc = DateTimeOffset.UtcNow.ToString("O");
        using (var db = new SqliteConnection(
            new SqliteConnectionStringBuilder {
                DataSource = Path.Combine(interviewData, "codingassistant.db")
            }.ToString()))
        {
            db.Open();
            void Seed(string sql, params (string Name, object Value)[] fields)
            {
                using var cmd = db.CreateCommand();
                cmd.CommandText = sql;
                foreach (var (name, value) in fields)
                    cmd.Parameters.AddWithValue(name, value);
                cmd.ExecuteNonQuery();
            }
            Seed("""
                INSERT INTO interview_specs
                (task_id,version,model,content,sha256,status,created_utc,approved_utc)
                VALUES(@task,1,@model,@spec,@hash,'Approved',@utc,@utc)
                """, ("@task", dotnetTaskId), ("@model", TaskStore.Model),
                ("@spec", approvedDotnetSpec), ("@hash", dotnetSpecSha),
                ("@utc", createdUtc));
            Seed("""
                INSERT INTO project_drafts
                (id,task_id,spec_version,spec_sha256,model,state,phase,
                 error_code,created_utc,updated_utc)
                VALUES(@id,@task,1,@hash,@model,'Generated','DraftGenerated',
                       NULL,@utc,@utc)
                """, ("@id", jobId), ("@task", dotnetTaskId),
                ("@hash", dotnetSpecSha), ("@model", TaskStore.Model),
                ("@utc", createdUtc));
            foreach (var filePath in new[] { "Inventory/Inventory.csproj",
                                              "Inventory/Program.cs" })
                Seed("""
                    INSERT INTO project_draft_files
                    (draft_id,path,purpose,content,sha256,created_utc)
                    VALUES(@id,@path,'Test fixture source',@content,@sha,@utc)
                    """, ("@id", jobId), ("@path", filePath),
                    ("@content", filePath.EndsWith(".csproj")
                        ? "<Project Sdk=\"Microsoft.NET.Sdk\" />"
                        : "public class Inventory {}"),
                    ("@sha", new string('c',64)), ("@utc", createdUtc));
        }

        using (var missing = await interviewClient.GetAsync(
            $"/api/tasks/{dotnetTaskId}/task-build"))
        using (var body = await ReadJson(missing))
            Check(missing.StatusCode == HttpStatusCode.OK &&
                  !body.RootElement.GetProperty("available").GetBoolean(),
                "no executable job is fabricated before exact-scope preparation");
        var reviewRequest = Guid.NewGuid().ToString("N");
        string buildId;
        string buildSha;
        using (var review = await Post(interviewClient,
            $"/api/tasks/{dotnetTaskId}/task-build", new {
                requestId = reviewRequest
            }))
        using (var body = await ReadJson(review))
        {
            Check(review.StatusCode == HttpStatusCode.Created,
                "task-specific contract persisted via authenticated endpoint");
            var build = body.RootElement.GetProperty("build");
            buildId = build.GetProperty("id").GetString()!;
            buildSha = build.GetProperty("contractSha256").GetString()!;
            Check(build.GetProperty("contract").GetProperty("objective")
                    .GetString() == dotnetObjective &&
                  build.GetProperty("contract").GetProperty("files")
                    .GetArrayLength() == 2 &&
                  !build.GetProperty("executionEnabled").GetBoolean() &&
                  build.GetProperty("state").GetString() == "Prepared",
                "actual approved objective and file allowlist bound to non-executable review");
        }
        using (var duplicate = await Post(interviewClient,
            $"/api/tasks/{dotnetTaskId}/task-build", new {
                requestId = reviewRequest
            }))
        using (var body = await ReadJson(duplicate))
            Check(duplicate.StatusCode == HttpStatusCode.Created &&
                  body.RootElement.GetProperty("build").GetProperty("id")
                    .GetString() == buildId &&
                  body.RootElement.GetProperty("events").GetArrayLength() == 1,
                "idempotent duplicate scope preparation creates no new event");

        using (var mismatch = await Post(interviewClient,
            $"/api/task-builds/{buildId}/scope-acknowledgement",
            new { approval = "APPROVE " + new string('0', 64) }))
            Check(mismatch.StatusCode == HttpStatusCode.Conflict,
                "incorrect contract digest cannot acknowledge scope");
        using (var blocked = await Post(interviewClient,
            $"/api/task-builds/{buildId}/start", new { }))
            Check(blocked.StatusCode == HttpStatusCode.Conflict,
                "unacknowledged task scope cannot start");

        using (var acknowledged = await Post(interviewClient,
            $"/api/task-builds/{buildId}/scope-acknowledgement",
            new { approval = "APPROVE " + buildSha }))
        using (var body = await ReadJson(acknowledged))
            Check(acknowledged.StatusCode == HttpStatusCode.OK &&
                  body.RootElement.GetProperty("build").GetProperty("state")
                    .GetString() == "ScopeAcknowledged" &&
                  body.RootElement.GetProperty("events").GetArrayLength() == 2,
                "exact scope acknowledgement is recorded and never executes");
        using (var blocked = await Post(interviewClient,
            $"/api/task-builds/{buildId}/start", new { }))
        using (var body = await ReadJson(blocked))
            Check(blocked.StatusCode == HttpStatusCode.ServiceUnavailable &&
                  body.RootElement.GetProperty("error").GetString() ==
                    "EXECUTION_UNAVAILABLE",
                "even acknowledged task build cannot bypass issue #76 release gate");
        using (var cursor = await interviewClient.GetAsync(
            $"/api/task-builds/{buildId}/events?after=1"))
        using (var body = await ReadJson(cursor))
            Check(cursor.StatusCode == HttpStatusCode.OK &&
                  body.RootElement.GetProperty("events").GetArrayLength() == 1 &&
                  body.RootElement.GetProperty("nextCursor").GetInt32() == 2,
                "append-only build events replay from an exact cursor");

        // Revocation must invalidate previously acknowledged scope.
        using (var db = new SqliteConnection(
            $"Data Source={Path.Combine(interviewData, "codingassistant.db")}"))
        {
            db.Open();
            using var cmd = db.CreateCommand();
            cmd.CommandText = """
                UPDATE interview_specs SET status='Superseded'
                WHERE task_id=@task AND version=1
                """;
            cmd.Parameters.AddWithValue("@task", dotnetTaskId);
            cmd.ExecuteNonQuery();
        }
        using (var stale = await Post(interviewClient,
            $"/api/task-builds/{buildId}/start", new { }))
            Check(stale.StatusCode == HttpStatusCode.Conflict,
                "revoked specification blocks a once-approved executable scope");
        using (var cancelled = await Post(interviewClient,
            $"/api/task-builds/{buildId}/cancel", new { }))
        using (var body = await ReadJson(cancelled))
            Check(cancelled.StatusCode == HttpStatusCode.OK &&
                  body.RootElement.GetProperty("build").GetProperty("state")
                    .GetString() == "Cancelled" &&
                  body.RootElement.GetProperty("events").GetArrayLength() == 3,
                "cancelled scope survives as an auditable terminal record");
        using (var invalid = await Post(interviewClient,
            $"/api/task-builds/{buildId}/scope-acknowledgement",
            new { approval = "APPROVE " + buildSha }))
            Check(invalid.StatusCode == HttpStatusCode.Conflict,
                "cancelled or superseded scope cannot be reauthorized");

        // The downloaded ZIP must be byte-identical to the saved source,
        // with no missing, modified, or extra files.
        using (var saved = await interviewClient.GetAsync(
            $"/api/tasks/{id}/draft-build"))
        using (var json = await ReadJson(saved))
        using (var zipped = await interviewClient.GetAsync(
            $"/api/tasks/{id}/draft-build/{draftId}/zip"))
        {
            Check(saved.StatusCode == HttpStatusCode.OK &&
                  zipped.StatusCode == HttpStatusCode.OK &&
                  zipped.Content.Headers.ContentType?.MediaType == "application/zip",
                "approved generated draft available as ZIP to the owner");
            var files = json.RootElement.GetProperty("snapshot")
                .GetProperty("files").EnumerateArray()
                .ToDictionary(e => e.GetProperty("path").GetString()!, e => (
                    Content: e.GetProperty("content").GetString()!,
                    Sha: e.GetProperty("sha256").GetString()!));
            var bytes = await zipped.Content.ReadAsByteArrayAsync();
            using var stream = new MemoryStream(bytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            Check(files.Count == 2 && archive.Entries.Count == files.Count &&
                  archive.Entries.Any(e => e.FullName == "src/Dashboard.tsx"),
                "ZIP contains exactly selected relative project source paths");
            foreach (var entry in archive.Entries)
            {
                using var contentStream = entry.Open();
                using var copy = new MemoryStream();
                contentStream.CopyTo(copy);
                var extracted = copy.ToArray();
                var digest = Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(extracted))
                    .ToLowerInvariant();
                Check(files.TryGetValue(entry.FullName, out var persisted) &&
                      System.Text.Encoding.UTF8.GetString(extracted) == persisted.Content &&
                      digest == persisted.Sha,
                    "ZIP entry bytes and SHA256 match persisted source: " + entry.FullName);
            }
        }
        // All archive endpoints still require the bearer token and exact task.
        using (var anonymous = new HttpClient(new SocketsHttpHandler {
            UseProxy = false
        }) { BaseAddress = interviewClient.BaseAddress })
        using (var denied = await anonymous.GetAsync(
            $"/api/tasks/{id}/draft-build/{draftId}/zip"))
            Check(denied.StatusCode == HttpStatusCode.Unauthorized,
                "unauthenticated ZIP download denied");
        using (var crossTask = await interviewClient.GetAsync(
            $"/api/tasks/{dotnetTaskId}/draft-build/{draftId}/zip"))
            Check(crossTask.StatusCode == HttpStatusCode.NotFound,
                "generated ZIP cannot be fetched using another task scope");
        foreach (var hostile in new[] {
            "../etc/passwd", "/root/.ssh/key", "src/../../secret.json",
            ".env", "scripts/build.sh", "src/evil\\\\x.ts"
        })
        {
            try
            {
                ProjectDraftStore.SafePath(hostile);
                throw new Exception("Unsafe model-selected path accepted: " + hostile);
            }
            catch (StoreFailure error) when (error.Code == "INVALID_PROJECT_PATH")
            {
                checks++;
            }
        }

        await interviewApp!.StopAsync();
        await interviewApp.DisposeAsync();
        interviewApp = null;
        interviewClient.Dispose();
        interviewClient = null;
        // Restart with the SAME real SQLite data: no conversation replay.
        interviewClient = await Start(interviewSettings,
            app => interviewApp = app, interviewModel: fakeInterview,
            projectModel: fakeProject, catalog: fakeCatalog);
        interviewClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", OwnerToken);
        using (var savedDraft = await interviewClient.GetAsync(
            $"/api/tasks/{id}/draft-build"))
        using (var json = await ReadJson(savedDraft))
        {
            var snapshot = json.RootElement.GetProperty("snapshot");
            Check(snapshot.GetProperty("job").GetProperty("state").GetString()
                    == "Generated" &&
                  snapshot.GetProperty("files").GetArrayLength() == 2 &&
                  fakeProject.PlanCount == 1,
                "durable generated files survive API restart without model replay");
        }
        using (var saved = await interviewClient.GetAsync($"/api/tasks/{id}/interview"))
        using (var json = await ReadJson(saved))
            Check(saved.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("messages").GetArrayLength() == 5 &&
                  json.RootElement.GetProperty("specification")
                    .GetProperty("sha256").GetString() == specHash &&
                  json.RootElement.GetProperty("specification")
                    .GetProperty("status").GetString() == "Approved",
                "interview, spec and approval survive real API restart");
        using (var savedRoles = await interviewClient.GetAsync(
            $"/api/tasks/{id}/interview"))
        using (var json = await ReadJson(savedRoles))
        {
            var messages = json.RootElement.GetProperty("messages")
                .EnumerateArray().ToArray();
            Check(savedRoles.StatusCode == HttpStatusCode.OK &&
                  messages.Count(m => m.GetProperty("role").GetString() == "assistant") == 3 &&
                  messages.Count(m => m.GetProperty("role").GetString() == "owner") == 2,
                "Product Owner display rename preserves persisted assistant/owner role contract");
        }


        // Hold an unfinished draft in the real SQLite store. A new owner
        // answer must invalidate both subsequent file writes and final
        // "Generated" success even if the model finished under an old spec.
        var rawDraftStore = new ProjectDraftStore(new TaskStore(interviewData));
        var pendingContract = rawDraftStore.Start(
            id, specVersion, specHash, "qwen3.5:4b");

        // More owner input deliberately supersedes an approved contract.
        using (var more = await Post(interviewClient,
            $"/api/tasks/{id}/interview/turn", new {
                expectedSequence = 5,
                message = "Also support exporting budget reports as CSV."
            }))
        using (var json = await ReadJson(more))
            Check(more.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("specification")
                    .GetProperty("status").GetString() == "Superseded",
                "new requirements invalidate earlier approved spec");
        try
        {
            rawDraftStore.SaveFile(id, pendingContract.Id,
                new PlannedProjectFile("README.md", "Instructions for budget app"),
                "# Wrong specification must not be silently finalized");
            throw new Exception("Superseded spec saved a stale project file");
        }
        catch (StoreFailure error) when (error.Code == "SPECIFICATION_CONFLICT")
        {
            checks++;
        }
        rawDraftStore.Finish(id, pendingContract.Id, true, null);
        Check(rawDraftStore.Get(id, pendingContract.Id).Job.State == "Failed" &&
              rawDraftStore.Get(id, pendingContract.Id).Job.ErrorCode ==
                "SPECIFICATION_CONFLICT",
            "atomic completion cannot claim success against superseded spec");

        using (var staleBuild = await Post(interviewClient,
            $"/api/tasks/{id}/draft-build", new {
                specificationVersion = specVersion,
                specificationSha256 = specHash
            }))
            Check(staleBuild.StatusCode == HttpStatusCode.Conflict,
                "superseded spec cannot authorize a new draft build");
        using (var updated = await Post(interviewClient,
            $"/api/tasks/{id}/interview/specification", new {
                expectedSequence = 7
            }))
        using (var json = await ReadJson(updated))
            Check(updated.StatusCode == HttpStatusCode.OK &&
                  json.RootElement.GetProperty("specification")
                    .GetProperty("version").GetInt32() == 2 &&
                  json.RootElement.GetProperty("specification")
                    .GetProperty("sha256").GetString() != specHash,
                "revised specification gets a new immutable version/hash");
    }
    finally
    {
        interviewClient?.Dispose();
        if (interviewApp is not null)
        {
            await interviewApp.StopAsync();
            await interviewApp.DisposeAsync();
        }
    }

    if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
    {
        var dirMode = File.GetUnixFileMode(data);
        var dbMode = File.GetUnixFileMode(Path.Combine(data, "codingassistant.db"));
        Check((dirMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead |
                          UnixFileMode.GroupWrite | UnixFileMode.OtherWrite |
                          UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0,
            "data directory owner-private");
        Check((dbMode & (UnixFileMode.GroupRead | UnixFileMode.OtherRead |
                         UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0,
            "SQLite DB owner-private");
    }
    Console.WriteLine($"M2 LOCAL_CONTROL_PLANE_SMOKE_PASS ({checks} assertions)");
}
finally
{
    client?.Dispose();
    if (appInstance is not null)
    {
        await appInstance.StopAsync();
        await appInstance.DisposeAsync();
    }
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { /* Windows temp cleanup may lag; no owner data involved. */ }
}

sealed class SmokeOllamaModelCatalog : IOllamaModelCatalog
{
    public HashSet<string> Models { get; } = new(StringComparer.Ordinal)
    {
        "qwen3.5:4b", "qwen2.5-coder:3b", "gemma4:latest"
    };

    public Task<IReadOnlyList<string>> ListAsync(CancellationToken _)
    {
        IReadOnlyList<string> result = Models.OrderBy(m => m, StringComparer.Ordinal)
            .ToArray();
        return Task.FromResult(result);
    }
}

sealed class SmokeProjectDraftModel : IStreamingProjectDraftModel
{
    public int PlanCount;
    public int WriteCount;
    public int ProgressCount;
    public string LastObjective = "";
    public string LastSpecification = "";

    public async Task<IReadOnlyList<PlannedProjectFile>> PlanWithProgressAsync(
        string objective, string approvedSpecification, string model,
        Func<Task> onOutputChunkBatch, CancellationToken cancellation)
    {
        ProgressCount++;
        await onOutputChunkBatch();
        return await PlanAsync(objective, approvedSpecification, model, cancellation);
    }

    public Task<IReadOnlyList<PlannedProjectFile>> PlanAsync(
        string objective, string approvedSpecification, string model,
        CancellationToken cancellation)
    {
        LastObjective = objective;
        LastSpecification = approvedSpecification;
        PlanCount++;
        IReadOnlyList<PlannedProjectFile> plan = new[] {
            new PlannedProjectFile("src/Dashboard.tsx",
                "React dashboard with personal finance budgets"),
            new PlannedProjectFile("README.md",
                "Instructions for locally inspecting the draft source")
        };
        return Task.FromResult(plan);
    }

    public async Task<string> WriteWithProgressAsync(
        string objective, string approvedSpecification, string model,
        PlannedProjectFile file, IReadOnlyList<ProjectDraftFile> previous,
        Func<Task> onOutputChunkBatch, CancellationToken cancellation)
    {
        ProgressCount++;
        await onOutputChunkBatch();
        return await WriteAsync(objective, approvedSpecification, model,
                                file, previous, cancellation);
    }

    public async Task<string> WriteAsync(
        string objective, string approvedSpecification, string model,
        PlannedProjectFile file, IReadOnlyList<ProjectDraftFile> previous,
        CancellationToken cancellation)
    {
        WriteCount++;
        await Task.Delay(15, cancellation);
        return file.Path.EndsWith(".tsx", StringComparison.Ordinal)
            ? "export function Dashboard() { return <main>Monthly budgets</main>; }"
            : "# Personal budget dashboard\\n\\nDraft implementation only.";
    }
}

sealed class SmokeInterviewModel : IInterviewModel
{
    public int AskCount;
    public int SpecCount;
    public string LastObjective = "";

    public Task<InterviewReply> AskAsync(
        string objective, IReadOnlyList<InterviewMessage> messages,
        string model, CancellationToken cancellation)
    {
        LastObjective = objective;
        AskCount++;
        var answerCount = messages.Count(x => x.Role == "owner");
        var message = answerCount switch
        {
            0 => "For your monthly budgets, who will use the application and " +
                 "are there any bank integrations?",
            1 => "For your C# and React application, which monthly budget " +
                 "editing features are essential?",
            _ => "I have enough detail to draft your finance specification."
        };
        return Task.FromResult(new InterviewReply(message, answerCount >= 2));
    }

    public Task<string> DraftAsync(
        string objective, IReadOnlyList<InterviewMessage> messages,
        string model, CancellationToken cancellation)
    {
        LastObjective = objective;
        SpecCount++;
        var text = "# Specification: " + objective + "\n\n" +
            "## Functional requirements\n" +
            string.Join("\n", messages.Where(x => x.Role == "owner")
                .Select(x => "- " + x.Content)) +
            "\n\n## Architecture\nLocal React and .NET with SQLite.\n" +
            "## Acceptance criteria\nAll required features are covered.\n" +
            "## Testing\nCompile and run independent acceptance tests.\n" +
            "## Risk\nDo not use cloud services or bank integrations.";
        return Task.FromResult(text);
    }
}

sealed class SmokeHtmlGenerator : IHtmlPreviewGenerator
{
    public int Calls;
    public string? ObservedObjective;
    public Task<string> GenerateAsync(string objective, string model,
                                       CancellationToken cancellation)
    {
        Calls++;
        ObservedObjective = objective;
        return Task.FromResult("<!DOCTYPE html>\n<html><head><title>Welcome</title></head>" +
            "<body><h1>Welcome, New User</h1></body></html>");
    }
}

sealed class SmokeM1Runner :
    IM1ExecutionRunner, IM1ProgressReporter, IM1ResultReader
{
    public int Executions;
    public volatile bool BlockOnExecute;
    public volatile bool CleanupIsVerified = true;

    public Task<M1ExecutablePreview> PreviewAsync(RunItem run, CancellationToken _)
    {
        var plan = JsonSerializer.SerializeToElement(new
        {
            schema_version = 1,
            stage = "M1.12",
            assignment = "repair fixed C# compiler errors in disposable api/Program.cs",
            fixture = "02-build-repair",
            fixture_sha256 = new string('a', 64),
            manifest_sha256 = new string('b', 64),
            model = "qwen3.5:4b",
            model_digest = new string('c', 64),
            image_id = new string('d', 64),
            evaluator_image_id = new string('e', 64),
            source_sha256 = new string('f', 64),
            writable_files = new[] { "api/Program.cs" },
            gateway_method = "codingassistant.coding.run",
            no_cloud = true,
            no_git_remote = true,
            control = new {
                task_id = run.TaskId, run_id = run.Id,
                plan_version = run.PlanVersion, plan_hash = run.PlanHash
            }
        });
        return Task.FromResult(new M1ExecutablePreview(
            M1ProcessRunner.ComputeCanonicalHash(plan), plan));
    }

    public Task<M1ExecutionOutcome> ExecuteAsync(
        RunItem run, string hash, CancellationToken cancellation) =>
        ExecuteWithProgressAsync(run, hash, _ => Task.CompletedTask, cancellation);

    public async Task<M1ExecutionOutcome> ExecuteWithProgressAsync(
        RunItem run, string hash,
        Func<string, Task> onStage, CancellationToken cancellation)
    {
        Interlocked.Increment(ref Executions);
        await onStage("GatewayReady");
        await onStage("CodingRequestSent");
        if (BlockOnExecute)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
        return M1ExecutionOutcome.Verified;
    }

    public Task<M1RunResult?> ReadResultAsync(
        string runId, string expectedPlanHash, CancellationToken cancellation) =>
        Task.FromResult<M1RunResult?>(new M1RunResult(
            runId, expectedPlanHash, "GATEWAY_APPROVED_CODING_PASS", "complete",
            null, true, true, 4,
            "--- a/api/Program.cs\n+++ b/api/Program.cs\n@@ -1 +1 @@\n-old\n+new\n",
            false));

    public Task<bool> ReconcileAsync(string requestId, CancellationToken cancellation) =>
        Task.FromResult(CleanupIsVerified);
}
