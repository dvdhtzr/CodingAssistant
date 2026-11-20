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
        IM1ExecutionRunner? runnerOverride = null,
        IHtmlPreviewGenerator? htmlGeneratorOverride = null,
        IInterviewModel? interviewModelOverride = null,
        IProjectDraftModel? projectModelOverride = null)
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
        var htmlArtifacts = new HtmlArtifactStore(store);
        htmlArtifacts.Initialize();
        var htmlGenerator = htmlGeneratorOverride ?? new LocalHtmlPreviewGenerator();
        var interviews = new InterviewStore(store);
        interviews.Initialize();
        var interviewModel = interviewModelOverride ?? new LocalInterviewModel();
        var projectDrafts = new ProjectDraftStore(store);
        projectDrafts.Initialize();
        projectDrafts.MarkInterruptedOnStartup();
        var projectModel = projectModelOverride ?? new LocalProjectDraftModel();
        var enabled = settings.EnableLocalExecution &&
            (runnerOverride is not null || OperatingSystem.IsLinux());
        var runner = runnerOverride ?? (enabled
            ? new M1ProcessRunner(settings.RepositoryRoot ??
                                  Directory.GetCurrentDirectory())
            : new DisabledM1Runner());
        // Result inspection is deliberately read-only and available even in
        // history-only mode. Never enable the coding runner just to display
        // an archived result; the ledger stays owner-private on the Dell.
        IM1ResultReader resultReader = runner as IM1ResultReader ??
            new DisabledM1ResultReader();
        if (runner is not IM1ResultReader && OperatingSystem.IsLinux())
        {
            var root = settings.RepositoryRoot ?? Directory.GetCurrentDirectory();
            var script = Path.Combine(root, "tools", "m1",
                                      "approved_gateway_coding.py");
            if (Directory.Exists(root) && File.Exists(script))
                resultReader = new M1ProcessRunner(root);
        }
        var expected = Convert.FromHexString(settings.OwnerToken);
        // Web root must be selected BEFORE WebApplicationBuilder is created.
        // The deployed API uses assets next to the binary; repository smoke
        // tests may use Vite's generated source-tree output instead.
        var packagedWeb = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var repositoryWeb = Path.Combine(Directory.GetCurrentDirectory(),
            "src", "CodingAssistant.Api", "wwwroot");
        var webRoot = Directory.Exists(packagedWeb) ? packagedWeb :
            Directory.Exists(repositoryWeb) ? repositoryWeb : null;
        var builder = webRoot is null
            ? WebApplication.CreateBuilder(args)
            : WebApplication.CreateBuilder(new WebApplicationOptions
                { Args = args, WebRootPath = webRoot });
        builder.WebHost.UseUrls(settings.BindUrl);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.AddServerHeader = false;
        });
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(interviews);
        builder.Services.AddSingleton<IInterviewModel>(interviewModel);
        builder.Services.AddSingleton(projectDrafts);
        builder.Services.AddSingleton<IProjectDraftModel>(projectModel);
        builder.Services.AddSingleton<ProjectDraftCoordinator>();
        builder.Services.AddSingleton(htmlArtifacts);
        builder.Services.AddSingleton<IHtmlPreviewGenerator>(htmlGenerator);
        builder.Services.AddSingleton(runs);
        builder.Services.AddSingleton(executions);
        builder.Services.AddSingleton<IM1ExecutionRunner>(runner);
        builder.Services.AddSingleton<IM1ResultReader>(resultReader);
        builder.Services.AddSingleton<ExecutionCoordinator>();
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
        // Static HTML generation returns text only: no agent, tools or project writes.
        app.MapGet("/api/tasks/{id}/html", (string id, HtmlArtifactStore artifacts) =>
            Safe(() => {
                var artifact = artifacts.Latest(id);
                return Results.Ok(new { available = artifact is not null, artifact });
            }));
        app.MapPost("/api/tasks/{id}/html", (
                string id, CreateHtmlPreviewRequest request,
                TaskStore db, HtmlArtifactStore artifacts,
                IHtmlPreviewGenerator generator, CancellationToken cancellation) =>
            SafeAsync(async () =>
            {
                var task = db.GetTask(id).Task;
                var model = request.Model ?? LocalHtmlPreviewGenerator.Baseline;
                if (!LocalHtmlPreviewGenerator.AllowedModel(model))
                    throw new StoreFailure("INVALID_INPUT");
                var html = await generator.GenerateAsync(
                    task.Objective, model, cancellation);
                var artifact = artifacts.Save(id, model, html);
                return Results.Created($"/api/tasks/{id}/html", artifact);
            }));
        // Task-specific dynamic AI interview + owner-reviewed specification.
        // All steps are planning-only; none can launch the M1 execution plane.
        app.MapGet("/api/tasks/{id}/interview", (string id, InterviewStore db) =>
            Safe(() => Results.Ok(db.Read(id))));
        app.MapPost("/api/tasks/{id}/interview/turn", (
                string id, InterviewTurnRequest input,
                TaskStore tasks, InterviewStore db, IInterviewModel model,
                CancellationToken cancellation) =>
            SafeAsync(async () =>
            {
                var task = tasks.GetTask(id).Task;
                var snapshot = db.Read(id);
                if (snapshot.LastSequence != input.ExpectedSequence ||
                    snapshot.Messages.Count(x => x.Role == "owner") >=
                    InterviewStore.MaxOwnerAnswers)
                    throw new StoreFailure("STALE_INTERVIEW");
                var selected = snapshot.Model ?? input.Model ??
                    LocalInterviewModel.DefaultModel;
                if (!LocalInterviewModel.AllowedModel(selected) ||
                    snapshot.Model is not null && input.Model is not null &&
                    snapshot.Model != input.Model)
                    throw new StoreFailure("INVALID_MODEL");
                if (snapshot.LastSequence == 0 && input.Message is not null)
                    throw new StoreFailure("INVALID_INPUT");
                var context = snapshot.Messages.ToList();
                if (snapshot.LastSequence != 0)
                    context.Add(new InterviewMessage(
                        snapshot.LastSequence + 1, "owner",
                        InterviewStore.ValidOwnerMessage(input.Message),
                        DateTimeOffset.UtcNow.ToString("O")));
                var reply = await model.AskAsync(
                    task.Objective, context, selected, cancellation);
                return Results.Ok(db.CommitTurn(
                    id, input with { Model = selected }, reply));
            }));
        app.MapPost("/api/tasks/{id}/interview/specification", (
                string id, SpecificationRequest input,
                TaskStore tasks, InterviewStore db, IInterviewModel model,
                CancellationToken cancellation) =>
            SafeAsync(async () =>
            {
                var task = tasks.GetTask(id).Task;
                var snapshot = db.Read(id);
                if (snapshot.LastSequence != input.ExpectedSequence ||
                    !snapshot.Messages.Any(x => x.Role == "owner") ||
                    snapshot.Model is null)
                    throw new StoreFailure("STALE_INTERVIEW");
                var content = await model.DraftAsync(
                    task.Objective, snapshot.Messages, snapshot.Model,
                    cancellation);
                return Results.Ok(db.CommitSpecification(
                    id, input.ExpectedSequence, content));
            }));
        app.MapPost("/api/tasks/{id}/interview/approve", (
                string id, ApproveSpecificationRequest input,
                InterviewStore db) =>
            Safe(() => Results.Ok(db.Approve(id, input))));
        // User-scoped draft source generation. This is not M1 agent
        // execution and runs NO model-generated code, shell or package tool.
        // Only an approved, current interview specification can start it.
        app.MapGet("/api/tasks/{id}/draft-build", (
                string id, ProjectDraftStore drafts) =>
            Safe(() => {
                var snapshot = drafts.Latest(id);
                return Results.Ok(new { available = snapshot is not null,
                    snapshot });
            }));
        app.MapPost("/api/tasks/{id}/draft-build", (
                string id, StartProjectDraftRequest request,
                ProjectDraftCoordinator coordinator) =>
            Safe(() => Results.Accepted(
                $"/api/tasks/{id}/draft-build",
                coordinator.Start(id, request))));
        app.MapPost("/api/tasks/{id}/draft-build/{jobId}/cancel", (
                string id, string jobId,
                ProjectDraftCoordinator coordinator) =>
            Safe(() => Results.Ok(coordinator.Cancel(id, jobId))));
        app.MapGet("/api/tasks/{id}/draft-build/{jobId}/zip", (
                string id, string jobId, ProjectDraftStore drafts) =>
            Safe(() => Results.File(drafts.Zip(id, jobId),
                "application/zip", "codingassistant-project-draft.zip")));
        // Read-only preview of the real task-specific agent contract.
        // Does not authorize a Gateway run or bypass integrated M1 security.
        app.MapGet("/api/tasks/{id}/agent-contract-preview", (
                string id, TaskStore tasks, InterviewStore interviews,
                ProjectDraftStore drafts) =>
            Safe(() =>
            {
                var task = tasks.GetTask(id).Task;
                var spec = interviews.Read(id).Specification;
                var draft = drafts.Latest(id);
                var preview = TaskAgentContract.Preview(task, spec, draft);
                // A changed current approval cannot survive this preview.
                var current = interviews.Read(id).Specification;
                if (current is null || current.Status != "Approved" ||
                    current.Version != spec!.Version ||
                    current.Sha256 != spec.Sha256)
                    throw new StoreFailure("SPECIFICATION_CONFLICT");
                return Results.Ok(preview);
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
        app.MapGet("/api/runs/{id}/result", (
                string id, ExecutionStore db, IM1ResultReader reader,
                CancellationToken cancellation) =>
            SafeAsync(async () =>
            {
                var plan = db.Get(id); // ID and exact approved plan are server-owned.
                var result = await reader.ReadResultAsync(
                    id, plan.PlanHash, cancellation);
                return Results.Ok(new { available = result is not null, result });
            }));
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
            "LOCAL_MODEL_UNAVAILABLE" or "MODEL_NOT_INSTALLED" =>
                StatusCodes.Status503ServiceUnavailable,
            "BUILD_BUSY" or "BUILD_NOT_COMPLETE" or
                "STALE_INTERVIEW" or "SPECIFICATION_CONFLICT" or
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
