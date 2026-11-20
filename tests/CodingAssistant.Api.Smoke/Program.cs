using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAssistant.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

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

async Task<HttpClient> Start(ApiSettings settings, Action<WebApplication> saveApp)
{
    var app = ApiHost.Build([], settings);
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

    client = await Start(settings, a => appInstance = a);

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
