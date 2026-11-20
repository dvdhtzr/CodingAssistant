using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CodingAssistant.Api;

public sealed record ApiSettings(
    string DataDirectory, string OwnerToken, string BindUrl)
{
    public static ApiSettings FromEnvironment()
    {
        var token = Environment.GetEnvironmentVariable("CODINGASSISTANT_OWNER_TOKEN");
        var directory = Environment.GetEnvironmentVariable("CODINGASSISTANT_DATA_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CodingAssistant");
        }

        var rawPort = Environment.GetEnvironmentVariable("CODINGASSISTANT_PORT") ?? "5098";
        if (!int.TryParse(rawPort, out var port) || port is < 1024 or > 65535)
            throw new InvalidOperationException("Invalid local-only HTTP port");
        return new ApiSettings(directory, token ?? "", $"http://127.0.0.1:{port}");
    }
}

public static class ApiHost
{
    private static readonly Regex OwnerTokenPattern =
        new("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static WebApplication Build(string[] args, ApiSettings? settings = null)
    {
        settings ??= ApiSettings.FromEnvironment();
        if (!OwnerTokenPattern.IsMatch(settings.OwnerToken))
            throw new InvalidOperationException(
                "Set CODINGASSISTANT_OWNER_TOKEN to 64 random lowercase hex characters");
        if (!Uri.TryCreate(settings.BindUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp ||
            uri.Host != "127.0.0.1" ||
            uri.Port is < 0 or > 65535 ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("Control plane must bind to IPv4 loopback");

        // Fail before opening a network listener if data permissions are unsafe.
        var store = new TaskStore(settings.DataDirectory);
        var runs = new RunStore(store);
        runs.Initialize();
        var expected = Convert.FromHexString(settings.OwnerToken);
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(settings.BindUrl);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.AddServerHeader = false;
        });
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(runs);
        builder.Services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumReceiveMessageSize = 4 * 1024;
            options.MaximumParallelInvocationsPerClient = 2;
        });
        builder.Services.AddSingleton<RunNotifier>();

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            if (!context.Request.Path.StartsWithSegments("/api"))
            {
                await next(context);
                return;
            }

            var header = context.Request.Headers.Authorization.ToString();
            if (header.Length != 7 + 64 ||
                !header.StartsWith("Bearer ", StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            // Strict ASCII hex token, fixed-length decoded bytes, constant-time
            // comparison; no token is ever written to SQLite or request logs.
            try
            {
                var provided = Convert.FromHexString(header.AsSpan(7));
                if (!CryptographicOperations.FixedTimeEquals(provided, expected))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            catch (FormatException)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });

        app.MapGet("/healthz", () => Results.Ok(
            new { status = "ok", executionEnabled = false }));
        app.MapGet("/api/status", () => Results.Ok(
            new {
                phase = "M2-control-plane", model = TaskStore.Model,
                executionEnabled = false, localOnly = true,
                plannedRunner = "OpenClaw-ACPX-OpenCode"
            }));
        app.MapGet("/api/tasks", (TaskStore db) =>
            Safe(() => Results.Ok(db.ListTasks())));
        app.MapGet("/api/tasks/{id}", (string id, TaskStore db) =>
            Safe(() => Results.Ok(db.GetTask(id))));
        app.MapPost("/api/tasks", (CreateTaskRequest input, TaskStore db) =>
            Safe(() =>
            {
                var created = db.CreateTask(input);
                return Results.Created($"/api/tasks/{created.Id}", created);
            }));
        app.MapPost("/api/tasks/{id}/plans", (
                string id, DraftPlanRequest input, TaskStore db) =>
            Safe(() =>
            {
                var plan = db.DraftPlan(id, input);
                return Results.Created(
                    $"/api/tasks/{id}/plans/{plan.Version}", plan);
            }));
        app.MapPost("/api/tasks/{id}/approvals", (
                string id, ApprovePlanRequest input, TaskStore db) =>
            Safe(() => Results.Ok(db.ApprovePlan(id, input))));

        // A run-intent is only a durable owner request, not a worker queue
        // entry or any authorization to access a repository or call a model.
        app.MapPost("/api/tasks/{id}/runs", (
                string id, CreateRunIntentRequest request, RunStore db,
                RunNotifier notifier) =>
            SafeAsync(async () =>
            {
                var change = db.CreateIntent(id, request);
                await notifier.PublishAsync(change.NewEvent);
                return Results.Created($"/api/runs/{change.Run.Id}", change.Run);
            }));
        app.MapGet("/api/tasks/{id}/runs", (string id, RunStore db) =>
            Safe(() => Results.Ok(db.ListForTask(id))));
        app.MapGet("/api/runs/{id}", (string id, RunStore db) =>
            Safe(() => Results.Ok(db.Get(id))));
        app.MapGet("/api/runs/{id}/events", (
                string id, long? after, int? limit, RunStore db) =>
            Safe(() => Results.Ok(db.ReadEvents(
                id, after ?? 0, limit ?? 50))));
        app.MapPost("/api/runs/{id}/cancel", (
                string id, RunStore db, RunNotifier notifier) =>
            SafeAsync(async () =>
            {
                var change = db.CancelIntent(id);
                await notifier.PublishAsync(change.NewEvent);
                return Results.Ok(change.Run);
            }));

        // A trusted host coordinator can nudge live observers AFTER writing
        // durable SQLite state. This endpoint cannot create an event, alter a
        // run or start an agent; it only broadcasts the current stored tip.
        app.MapPost("/api/runs/{id}/refresh", (
                string id, RunStore db, RunNotifier notifier) =>
            SafeAsync(async () =>
            {
                var current = db.Get(id);
                var page = db.ReadEvents(id,
                    after: Math.Max(0, current.LastSequence - 1), limit: 1);
                await notifier.PublishAsync(page.Events.LastOrDefault());
                return Results.Ok(current);
            }));

        // SignalR uses the exact same authenticated /api HTTP boundary,
        // including WebSocket handshakes. It carries no durable-only state.
        app.MapHub<RunProgressHub>("/api/progress");

        // Intentionally NO /execute endpoint yet: control-plane approval
        // is not authorization for M1 runner execution or repository mounts.
        return app;
    }

    private static IResult Safe(Func<IResult> action)
    {
        try
        {
            return action();
        }
        catch (StoreFailure error)
        {
            return Rejected(error);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return DatabaseUnavailable();
        }
    }

    private static async Task<IResult> SafeAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (StoreFailure error)
        {
            return Rejected(error);
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return DatabaseUnavailable();
        }
    }

    private static IResult Rejected(StoreFailure error)
    {
        var status = error.Code switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "STALE_VERSION" or "IDEMPOTENCY_CONFLICT" or
                "APPROVAL_MISMATCH" or "PLAN_NOT_APPROVED" or
                "INVALID_STATE" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { error = error.Code }, statusCode: status);
    }

    private static IResult DatabaseUnavailable() =>
        Results.Json(new { error = "DATABASE_UNAVAILABLE" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
