using System.Net;
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
    IM1ExecutionRunner? runner = null)
{
    var app = ApiHost.Build([], settings, runner);
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
            "--- a/api/Program.cs\\n+++ b/api/Program.cs\\n@@ -1 +1 @@\\n-old\\n+new\\n",
            false));

    public Task<bool> ReconcileAsync(string requestId, CancellationToken cancellation) =>
        Task.FromResult(CleanupIsVerified);
}
