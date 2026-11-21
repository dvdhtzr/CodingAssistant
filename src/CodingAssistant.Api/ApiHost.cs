using System.Security.Cryptography;
using System.Text;
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
        // Visual Studio F5 does not run the PowerShell launcher. Windows uses
        // the same user-protected credential as the one-click launcher when
        // no explicit token was configured. Linux stays explicit by design.
        if (string.IsNullOrWhiteSpace(token))
            token = OperatingSystem.IsWindows()
                ? WindowsOwnerTokenStore.GetOrCreate(directory) : "";
        return new ApiSettings(directory, token, $"http://127.0.0.1:{port}",
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
        IProjectDraftModel? projectModelOverride = null,
        IOllamaModelCatalog? catalogOverride = null,
        IArchitectureAdvisor? architectureAdvisorOverride = null,
        IImplementationPlanner? implementationPlannerOverride = null,
        IEngineeringTaskModel? engineeringModelOverride = null)
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
        var architectureDecisions = new ArchitectureDecisionStore(store);
        architectureDecisions.Initialize();
        // Phase 3: real Microsoft Agent Framework agents by default.
        // Test overrides remain explicit; no cloud or executable tool fallback.
        var mafTeam = new MafAgentTeam();
        var interviewModel = interviewModelOverride ?? mafTeam;
        var architectModel = architectureAdvisorOverride ?? mafTeam;
        var handoffs = new MafHandoffStore(store, interviews);
        handoffs.Initialize();
        var implementationPlans = new ImplementationPlanStore(store, interviews);
        implementationPlans.Initialize();
        var engineeringRuns = new EngineeringRunStore(
            store, interviews, implementationPlans);
        engineeringRuns.Initialize();
        engineeringRuns.MarkInterruptedOnStartup();
        var softwareEngineer = engineeringModelOverride ?? new MafSoftwareEngineer();
        var technicalLead = implementationPlannerOverride ?? (IImplementationPlanner)mafTeam;
        var catalog = catalogOverride ?? new LocalOllamaModelCatalog();
        var projectDrafts = new ProjectDraftStore(store);
        projectDrafts.Initialize();
        projectDrafts.MarkInterruptedOnStartup();
        var taskBuilds = new TaskBuildStore(store);
        taskBuilds.Initialize();
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
        // Browser login never discloses this persistent owner token.
        var browserSessions = new LocalBrowserSessions(expected);
        // Web root must be selected BEFORE WebApplicationBuilder is created.
        // The deployed API uses assets next to the binary; repository smoke
        // tests may use Vite's generated source-tree output instead.
        var packagedWeb = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var repositoryWeb = Path.Combine(Directory.GetCurrentDirectory(),
            "src", "CodingAssistant.Api", "wwwroot");
        // On Windows F5, source-built Vite output is authoritative. The
        // copy next to bin/Debug/net10.0 may be left over from earlier runs.
        // Published applications still use their packaged assets.
        var preparedWeb = WindowsDevelopmentFrontend.PreparedWebRoot;
        var webRoot = preparedWeb is not null &&
            Directory.Exists(preparedWeb) ? preparedWeb :
            Directory.Exists(packagedWeb) ? packagedWeb :
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
        builder.Services.AddSingleton(browserSessions);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(interviews);
        builder.Services.AddSingleton(architectureDecisions);
        builder.Services.AddSingleton(handoffs);
        builder.Services.AddSingleton(implementationPlans);
        builder.Services.AddSingleton(engineeringRuns);
        builder.Services.AddSingleton<IEngineeringTaskModel>(softwareEngineer);
        builder.Services.AddSingleton<EngineeringRunCoordinator>();
        builder.Services.AddSingleton<IImplementationPlanner>(technicalLead);
        builder.Services.AddSingleton<IArchitectureAdvisor>(architectModel);
        builder.Services.AddSingleton<IInterviewModel>(interviewModel);
        builder.Services.AddSingleton<IOllamaModelCatalog>(catalog);
        builder.Services.AddSingleton(projectDrafts);
        builder.Services.AddSingleton(taskBuilds);
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

            // Only the tightly-validated bootstrap may enter without a bearer.
            // Every other API and SignalR request still requires a bearer.
            if (context.Request.Path == "/api/local-session" &&
                HttpMethods.IsPost(context.Request.Method))
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

            // Valid owner bearer OR short-lived, in-memory browser session.
            // No token is written to SQLite, cookies, URLs or request logs.
            try
            {
                var token = header[7..];
                var provided = Convert.FromHexString(token);
                if (!CryptographicOperations.FixedTimeEquals(provided, expected) &&
                    !browserSessions.IsValid(token))
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

        // UI assets are static and unauthenticated. All data/operations require
        // the owner bearer or an ephemeral authenticated local browser session.
        app.UseDefaultFiles();
        app.UseStaticFiles();

        // Same-origin, Windows-only bootstrap: short-lived bearer returned
        // only to this app's own JS. No cookie and no persistent owner secret.
        app.MapPost("/api/local-session", (HttpContext context,
                LocalBrowserSessions sessions) =>
        {
            if (!OperatingSystem.IsWindows())
                return Results.NotFound();
            if (!LocalBrowserSessions.MayBootstrap(context))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return Results.Ok(new
            {
                sessionToken = sessions.Issue(),
                lifetimeSeconds = 12 * 60 * 60
            });
        });

        app.MapGet("/healthz", () => Results.Ok(
            new { status = "ok", executionEnabled = enabled,
                  uiVersion = WindowsDevelopmentFrontend.UiVersion,
                  uiSource = preparedWeb is not null ? "source-current" : "packaged" }));
        app.MapGet("/api/status", () => Results.Ok(
            new {
                phase = "M2-control-plane", model = TaskStore.Model,
                executionEnabled = enabled, localOnly = true,
                plannedRunner = "OpenClaw-ACPX-OpenCode"
            }));
        // Authenticated discovery of models downloaded to the local Ollama
        // server. M1 executable runner model selection remains unchanged.
        app.MapGet("/api/models", (IOllamaModelCatalog catalog,
                CancellationToken cancellation) =>
            SafeAsync(async () => Results.Ok(new {
                models = await catalog.ListAsync(cancellation)
            })));
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
        app.MapDelete("/api/tasks/{id}", (string id, TaskStore db) =>
            Safe(() =>
            {
                db.DeleteTask(id);
                return Results.NoContent();
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
        app.MapGet("/api/tasks/{id}/agent-handoffs",
            (string id, MafHandoffStore db, ImplementationPlanStore plans,
             EngineeringRunStore engineers) =>
                Safe(() => Results.Ok(new {
                    engine = "Microsoft Agent Framework",
                    executionAvailable = false,
                    events = db.List(id).Concat(plans.Handoffs(id))
                        .Concat(engineers.Handoffs(id))
                        .OrderBy(e => e.CreatedUtc).ThenBy(e => e.Kind).ToArray()
                })));
        app.MapPost("/api/tasks/{id}/interview/turn", (
                string id, InterviewTurnRequest input,
                TaskStore tasks, InterviewStore db, IInterviewModel model,
                IOllamaModelCatalog catalog, CancellationToken cancellation) =>
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
                var localNames = await catalog.ListAsync(cancellation);
                if (!localNames.Contains(selected, StringComparer.Ordinal))
                    throw new StoreFailure("MODEL_NOT_INSTALLED");
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
                IOllamaModelCatalog catalog, CancellationToken cancellation) =>
            SafeAsync(async () =>
            {
                var task = tasks.GetTask(id).Task;
                var snapshot = db.Read(id);
                if (snapshot.LastSequence != input.ExpectedSequence ||
                    !snapshot.Messages.Any(x => x.Role == "owner") ||
                    snapshot.Model is null)
                    throw new StoreFailure("STALE_INTERVIEW");
                var localNames = await catalog.ListAsync(cancellation);
                if (!localNames.Contains(snapshot.Model, StringComparer.Ordinal))
                    throw new StoreFailure("MODEL_NOT_INSTALLED");
                var content = await model.DraftAsync(
                    task.Objective, snapshot.Messages, snapshot.Model,
                    cancellation);
                return Results.Ok(db.CommitSpecification(
                    id, input.ExpectedSequence, content));
            }));
        app.MapPost("/api/tasks/{id}/interview/approve", (
                string id, ApproveSpecificationRequest input,
                InterviewStore db, MafHandoffStore handoffs) =>
            Safe(() => {
                var approved = db.Approve(id, input);
                handoffs.RecordApproved(id, approved.Specification!);
                return Results.Ok(approved);
            }));
        // Planning-only Phase 2 contracts. A profile descriptor is not a
        // runnable toolchain and an architecture approval is not execution consent.
        app.MapGet("/api/runtime-profiles", () => Results.Ok(new
        {
            schemaVersion = RuntimeProfileRegistry.SchemaVersion,
            registryExecutionAvailable = false,
            profiles = RuntimeProfileRegistry.List()
        }));
        app.MapGet("/api/runtime-profiles/{id}", (string id) =>
        {
            var profile = RuntimeProfileRegistry.Find(id);
            return profile is null ? Results.NotFound() : Results.Ok(profile);
        });
        app.MapGet("/api/tasks/{id}/architecture-decisions",
                (string id, ArchitectureDecisionStore decisions) =>
            Safe(() => Results.Ok(new
            {
                schemaVersion = ArchitectureDecisionStore.SchemaVersion,
                decisions = decisions.List(id)
            })));
        app.MapGet("/api/tasks/{id}/architecture-decisions/{topic}/history",
                (string id, string topic, ArchitectureDecisionStore decisions) =>
            Safe(() => Results.Ok(new
            {
                schemaVersion = ArchitectureDecisionStore.SchemaVersion,
                decisions = decisions.History(id, topic)
            })));
        // The Solution Architect only proposes; owner review is separate.
        // A pending model response is never saved if the spec/revision changed.
        app.MapPost("/api/tasks/{id}/architecture-decisions/recommend",
                (string id, RecommendArchitectureRequest input,
                 TaskStore tasks, InterviewStore interviews,
                 ArchitectureDecisionStore decisions, IArchitectureAdvisor advisor,
                 MafHandoffStore handoffs, IOllamaModelCatalog catalog,
                 CancellationToken cancellation) =>
            SafeAsync(async () =>
            {
                if (input.SchemaVersion != ArchitectureDecisionStore.SchemaVersion ||
                    !LocalArchitectureAdvisor.AllowedTopic(input.Topic) ||
                    input.ExpectedRevision < 0)
                    throw new StoreFailure("INVALID_INPUT");
                var task = tasks.GetTask(id).Task;
                var snapshot = interviews.Read(id);
                var spec = snapshot.Specification;
                if (spec is null || spec.Status != "Approved" ||
                    spec.Version != input.SpecificationVersion ||
                    spec.Sha256 != input.SpecificationSha256)
                    throw new StoreFailure("SPECIFICATION_CONFLICT");
                var models = await catalog.ListAsync(cancellation);
                if (!models.Contains(spec.Model, StringComparer.Ordinal))
                    throw new StoreFailure("MODEL_NOT_INSTALLED");
                var recommendation = await advisor.RecommendAsync(
                    task.Objective, spec.Content, spec.Model,
                    input.Topic!, cancellation);
                var created = decisions.Propose(id, new(
                    ArchitectureDecisionStore.SchemaVersion,
                    input.SpecificationVersion, input.SpecificationSha256,
                    input.Topic, recommendation.RecommendedOptionId,
                    recommendation.Rationale, recommendation.Options,
                    input.ExpectedRevision));
                handoffs.RecordProposal(id, input.Topic!, spec, created.Revision);
                return Results.Created(
                    $"/api/tasks/{id}/architecture-decisions/{created.Topic}/history",
                    created);
            }));
        app.MapPost("/api/tasks/{id}/architecture-decisions",
                (string id, ProposeArchitectureDecisionRequest input,
                 ArchitectureDecisionStore decisions) =>
            Safe(() =>
            {
                var proposal = decisions.Propose(id, input);
                return Results.Created(
                    $"/api/tasks/{id}/architecture-decisions/{proposal.Topic}/history",
                    proposal);
            }));
        app.MapPost("/api/tasks/{id}/architecture-decisions/{topic}/review",
                (string id, string topic, ReviewArchitectureDecisionRequest input,
                 ArchitectureDecisionStore decisions) =>
            Safe(() => Results.Ok(decisions.Review(id, topic, input))));
        // Phase 3: real MAF Technical Lead planning with immutable owner
        // approval. The plan is advisory and never runs generated code.
        app.MapGet("/api/tasks/{id}/implementation-plan",
            (string id, ImplementationPlanStore db) =>
                Safe(() => Results.Ok(db.Read(id))));
        app.MapPost("/api/tasks/{id}/implementation-plan/generate",
            (string id, GenerateImplementationPlanRequest input,
             TaskStore tasks, InterviewStore interviews,
             ImplementationPlanStore db, IImplementationPlanner planner,
             IOllamaModelCatalog catalog, CancellationToken cancellation) =>
            SafeAsync(async () =>
            {
                if (input.SchemaVersion != ImplementationPlanStore.SchemaVersion ||
                    input.ExpectedRevision < 0)
                    throw new StoreFailure("INVALID_INPUT");
                var task = tasks.GetTask(id).Task;
                var spec = interviews.Read(id).Specification;
                if (spec is null || spec.Status != "Approved" ||
                    spec.Version != input.SpecificationVersion ||
                    spec.Sha256 != input.SpecificationSha256)
                    throw new StoreFailure("SPECIFICATION_CONFLICT");
                var architecture = DraftArchitecturePolicy.Readiness(tasks, interviews, id);
                if (!architecture.ReadyForSourceGeneration ||
                    architecture.ArchitectureSha256 != input.ArchitectureSha256)
                    throw new StoreFailure("ARCHITECTURE_CONFLICT");
                if ((db.Read(id).Plan?.Revision ?? 0) != input.ExpectedRevision)
                    throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
                var installed = await catalog.ListAsync(cancellation);
                if (!installed.Contains(spec.Model, StringComparer.Ordinal))
                    throw new StoreFailure("MODEL_NOT_INSTALLED");
                var outline = await planner.PlanAsync(task.Objective, spec.Content,
                    architecture, spec.Model, cancellation);
                // Atomically recheck specification, architecture and revision
                // *after* local inference to reject concurrent human edits.
                var created = db.Propose(id, input, spec.Model, outline);
                return Results.Created($"/api/tasks/{id}/implementation-plan",
                    created);
            }));
        app.MapPost("/api/tasks/{id}/implementation-plan/approve",
            (string id, ApproveImplementationPlanRequest input,
             ImplementationPlanStore db) =>
                Safe(() => Results.Ok(db.Approve(id, input))));
        // Exact identity, goals, fictional work histories and future skill scopes
        // are host-owned; none of these fields grant executable permissions.
        app.MapGet("/api/agents/roster", () =>
            Results.Ok(new { engine = "Microsoft Agent Framework",
                agents = EngineeringAgentRoster.All }));

        // Fourth real MAF agent: one task at a time from the exact current
        // owner-approved Technical Lead plan, source-only and never executed.
        app.MapGet("/api/tasks/{id}/engineering-runs/latest",
            (string id, EngineeringRunStore db) =>
                Safe(() => Results.Ok(new { snapshot = db.Latest(id) })));
        app.MapPost("/api/tasks/{id}/engineering-runs",
            (string id, StartEngineeringRunRequest request,
             EngineeringRunCoordinator coordinator) =>
                Safe(() => Results.Accepted(
                    $"/api/tasks/{id}/engineering-runs",
                    coordinator.Start(id, request))));
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}",
            (string id, string runId, EngineeringRunStore db) =>
                Safe(() => Results.Ok(db.Get(id, runId))));
        app.MapPost("/api/tasks/{id}/engineering-runs/{runId}/cancel",
            (string id, string runId, EngineeringRunCoordinator coordinator) =>
                Safe(() => Results.Ok(coordinator.Cancel(id, runId))));
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/zip",
            (string id, string runId, EngineeringRunStore db) =>
                Safe(() => Results.File(db.Zip(id, runId), "application/zip",
                    "codingassistant-engineering-source.zip")));
        // User-scoped draft source generation. This is not M1 agent
        // execution and runs NO model-generated code, shell or package tool.
        // Only an approved, current interview specification can start it.
        // Read-only readiness for the new owner-facing architecture-bound
        // source workflow. Never grants or starts a runtime.
        app.MapGet("/api/tasks/{id}/draft-build/architecture", (
                string id, TaskStore tasks, InterviewStore interviews) =>
            Safe(() => Results.Ok(DraftArchitecturePolicy.Readiness(
                tasks, interviews, id))));
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
        // Download the EXACT bytes checked by the task pilot. This is
        // planning-only export, not consent to run the agent.
        app.MapGet("/api/tasks/{id}/agent-contract-preview/json", (
                string id, TaskStore tasks, InterviewStore interviews,
                ProjectDraftStore drafts) =>
            Safe(() =>
            {
                var task = tasks.GetTask(id).Task;
                var spec = interviews.Read(id).Specification;
                var preview = TaskAgentContract.Preview(
                    task, spec, drafts.Latest(id));
                var bytes = Encoding.UTF8.GetBytes(preview.Contract.GetRawText());
                var hash = Convert.ToHexString(SHA256.HashData(bytes))
                    .ToLowerInvariant();
                if (hash != preview.ContractSha256)
                    throw new StoreFailure("SPECIFICATION_CONFLICT");
                var current = interviews.Read(id).Specification;
                if (current is null || current.Status != "Approved" ||
                    current.Version != spec!.Version ||
                    current.Sha256 != spec.Sha256)
                    throw new StoreFailure("SPECIFICATION_CONFLICT");
                return Results.File(bytes, "application/json",
                    "codingassistant-agent-contract.json");
            }));
        // New task-specific build admission is independent of M1.12's fixture.
        // These endpoints can persist/review a source scope, but cannot yet
        // launch OpenCode. The actual runtime plan must receive separate exact
        // owner authorization after issue #76's integrated security gates.
        app.MapGet("/api/tasks/{id}/task-build", (
                string id, TaskBuildStore builds) =>
            Safe(() => {
                var snapshot = builds.Latest(id);
                return Results.Ok(new { available = snapshot is not null, snapshot });
            }));
        app.MapPost("/api/tasks/{id}/task-build", (
                string id, PrepareTaskBuildRequest request,
                TaskStore tasks, InterviewStore interviews,
                ProjectDraftStore drafts, TaskBuildStore builds) =>
            Safe(() => {
                var task = tasks.GetTask(id).Task;
                var preview = TaskAgentContract.Preview(task,
                    interviews.Read(id).Specification, drafts.Latest(id));
                var snapshot = builds.Prepare(id, request.RequestId, preview);
                return Results.Created($"/api/task-builds/{snapshot.Build.Id}", snapshot);
            }));
        app.MapGet("/api/task-builds/{id}", (string id, TaskBuildStore builds) =>
            Safe(() => Results.Ok(builds.Get(id))));
        app.MapGet("/api/task-builds/{id}/events", (
                string id, int? after, int? limit, TaskBuildStore builds) =>
            Safe(() => Results.Ok(builds.ReadEvents(id, after ?? 0, limit ?? 50))));
        app.MapPost("/api/task-builds/{id}/scope-acknowledgement", (
                string id, AcknowledgeTaskBuildRequest request,
                TaskBuildStore builds) =>
            Safe(() => Results.Ok(builds.Acknowledge(id, request.Approval))));
        app.MapPost("/api/task-builds/{id}/cancel", (
                string id, TaskBuildStore builds) =>
            Safe(() => Results.Ok(builds.Cancel(id))));
        app.MapPost("/api/task-builds/{id}/start", (
                string id, TaskBuildStore builds) =>
            Safe(() => {
                builds.RequireStartGate(id);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
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
                "ARCHITECTURE_NOT_READY" or "ARCHITECTURE_CONFLICT" or
                "ARCHITECTURE_PLAN_CONFLICT" or
                "STALE_DECISION" or "STALE_IMPLEMENTATION_PLAN" or
                "SPECIFICATION_NOT_APPROVED" or
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
