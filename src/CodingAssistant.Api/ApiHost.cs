using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace CodingAssistant.Api;

public sealed record ApiSettings(
    string DataDirectory, string OwnerToken, string BindUrl,
    bool EnableLocalExecution = false, string? RepositoryRoot = null)
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
        return new ApiSettings(directory, token ?? "", $"http://127.0.0.1:{port}",
            Environment.GetEnvironmentVariable("CODINGASSISTANT_ENABLE_LOCAL_EXECUTION") == "1",
            Environment.GetEnvironmentVariable("CODINGASSISTANT_REPOSITORY_ROOT"));
    }
}

public static class ApiHost
{
    private static readonly Regex OwnerTokenPattern =
        new("^[0-9a-f]{64}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static WebApplication Build(
        string[] args, ApiSettings? settings = null,
        IM1ExecutionRunner? runnerOverride = null)
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
        var executions = new ExecutionStore(store);
        executions.Initialize();
        executions.MarkUnreconciledOnStartup();
        var enabled = settings.EnableLocalExecution &&
            (runnerOverride is not null || OperatingSystem.IsLinux());
        var runner = runnerOverride ?? (enabled
            ? new M1ProcessRunner(settings.RepositoryRoot ??
                                  Directory.GetCurrentDirectory())
            : new DisabledM1Runner());
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
        builder.Services.AddSingleton(executions);
        builder.Services.AddSingleton<IM1ExecutionRunner>(runner);
        builder.Services.AddSingleton<ExecutionCoordinator>();
        builder.Services.AddSignalR(options =>
        {
            options.EnableDetailedErrors = false;
            options.MaximumReceiveMessageSize = 4 * 1024;
            options.MaximumParallelInvocationsPerClient = 2;
        });
        builder.Services.AddSingleton<RunNotifier>();

        // The published self-contained API ships its own compiled, same-origin
        // React assets. During repo smoke checks Vite writes to API/wwwroot.
        // Never serve arbitrary owner directories or accept a path from HTTP.
        var packagedWeb = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var repositoryWeb = Path.Combine(Directory.GetCurrentDirectory(),
            "src", "CodingAssistant.Api", "wwwroot");
        if (Directory.Exists(packagedWeb))
            builder.WebHost.UseWebRoot(packagedWeb);
        else if (Directory.Exists(repositoryWeb))
            builder.WebHost.UseWebRoot(repositoryWeb);

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cache-Control"] = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'self'; style-src 'self'; " +
                "connect-src 'self'; img-src 'self' data:; " +
                "base-uri 'none'; frame-ancestors 'none'; form-action 'none'";
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

        // UI assets are static and unauthenticated; all data/operations still
        // require the exact owner bearer in an Authorization header.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/healthz", () => Results.Ok(
            new { status = "ok", executionEnabled = enabled }));
        app.MapGet("/api/status", () => Results.Ok(
            new {
                phase = "M2-control-plane", model = TaskStore.Model,
                executionEnabled = enabled, localOnly = true,
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

        // M2 planning consent cannot start M1. A separate exact M1 plan
        // must be generated, reviewed, authorized, and explicitly started.
        app.MapPost("/api/runs/{id}/execution-plan", (
                string id, ExecutionCoordinator coordinator,
                CancellationToken cancellation) =>
            SafeAsync(async () =>
                Results.Ok(await RequireRunner().PrepareAsync(id, cancellation))));
        app.MapGet("/api/runs/{id}/execution-plan", (
                string id, ExecutionStore db) =>
            Safe(() => Results.Ok(db.Get(id))));
        app.MapPost("/api/runs/{id}/execution-authorization", (
                string id, AuthorizeExecutionRequest request,
                ExecutionCoordinator coordinator) =>
            SafeAsync(async () =>
                Results.Ok(await RequireRunner().AuthorizeAsync(id, request))));
        app.MapPost("/api/runs/{id}/start", (
                string id, ExecutionCoordinator coordinator) =>
            SafeAsync(async () =>
                Results.Accepted($"/api/runs/{id}",
                    await RequireRunner().StartAsync(id))));
        app.MapPost("/api/runs/{id}/execution-cancel", (
                string id, ExecutionCoordinator coordinator) =>
            SafeAsync(async () =>
                Results.Ok(await RequireRunner().CancelAsync(id))));
        app.MapPost("/api/runs/{id}/execution-recover", (
                string id, ExecutionCoordinator coordinator,
                CancellationToken cancellation) =>
            SafeAsync(async () =>
                Results.Ok(await RequireRunner().RecoverAsync(id, cancellation))));

        ExecutionCoordinator RequireRunner()
        {
            if (!enabled)
                throw new ExecutionUnavailable();
            return app.Services.GetRequiredService<ExecutionCoordinator>();
        }

        // SignalR uses the exact same authenticated /api HTTP boundary,
        // including WebSocket handshakes. It carries no durable-only state.
        app.MapHub<RunProgressHub>("/api/progress");

        // There is intentionally no arbitrary command, repository, model,
        // mount or prompt selection endpoint. M1 stays fixed-fixture-only.
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
        catch (ExecutionUnavailable)
        {
            return ExecutionDisabled();
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
        catch (ExecutionUnavailable)
        {
            return ExecutionDisabled();
        }
        catch (OperationCanceledException)
        {
            return ExecutionDisabled();
        }
    }

    private static IResult Rejected(StoreFailure error)
    {
        var status = error.Code switch
        {
            "NOT_FOUND" => StatusCodes.Status404NotFound,
            "STALE_VERSION" or "IDEMPOTENCY_CONFLICT" or
                "APPROVAL_MISMATCH" or "PLAN_NOT_APPROVED" or
                "INVALID_STATE" or "EXECUTION_BUSY" or
                "EXECUTION_PLAN_CONFLICT" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status400BadRequest
        };
        return Results.Json(new { error = error.Code }, statusCode: status);
    }

    private static IResult ExecutionDisabled() =>
        Results.Json(new { error = "EXECUTION_UNAVAILABLE" },
            statusCode: StatusCodes.Status503ServiceUnavailable);

    private static IResult DatabaseUnavailable() =>
        Results.Json(new { error = "DATABASE_UNAVAILABLE" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
}
