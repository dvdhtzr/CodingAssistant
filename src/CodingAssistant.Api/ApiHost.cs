using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Routing;
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
        IEngineeringTaskModel? engineeringModelOverride = null,
        IQaSourceReviewer? qaReviewerOverride = null,
        ILocalSourceSmokeRunner? sourceSmokeOverride = null,
        IAtlasCommitArchiveDownloader? atlasArchiveOverride = null,
        ILeadSourceReviewer? leadReviewerOverride = null,
        IEngineeringIssueVerifier? issueVerifierOverride = null,
        ICollaborationLead? collaborationLeadOverride=null)
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
        _=LocalInferenceScheduler.Shared;
        new InferenceLeaseStore(store).Recover();
        new SourceSmokeEvidenceStore(store).Recover();
        var roleModels = new AgentRoleModelSettings(store);
        roleModels.Initialize();
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
            store, implementationPlans);
        engineeringRuns.Initialize();
        var atlasLedger = new EngineeringAtlasLedger(store, engineeringRuns);
        atlasLedger.Initialize();
        var atlasImports = new CodeAtlasRepositoryImportStore(store,
            atlasArchiveOverride ?? new PublicGitHubCommitArchiveDownloader());
        atlasImports.Initialize();
        engineeringRuns.MarkInterruptedOnStartup();
        var leadReviews = new LeadReviewStore(store);
        leadReviews.MarkInterruptedOnStartup();
        var qaReviews = new QaReviewStore(store, implementationPlans);
        qaReviews.Initialize();
        qaReviews.MarkInterruptedOnStartup();
        var repairProposals = new RepairProposalStore(store);
        repairProposals.Initialize();
        var repairTriage = new RepairTriageStore(store);
        repairTriage.Initialize();
        var repairCycles = new RepairCycleStore(store, engineeringRuns,
            qaReviews, repairProposals);
        repairCycles.Initialize();
        var qaEngineer = qaReviewerOverride ?? new MafQaEngineer();
        var sourceSmokeRunner = sourceSmokeOverride ??
            new WindowsDockerSourceSmokeRunner(settings.RepositoryRoot);
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
        // Default hosting request logs can include raw URLs and query strings.
        // The Studio already exposes bounded, redacted route-template metadata.
        builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
        builder.WebHost.UseUrls(settings.BindUrl);
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = 16 * 1024;
            options.AddServerHeader = false;
        });
        var liveHttpActivity = new LocalHttpActivityLedger();
        builder.Services.AddSingleton(liveHttpActivity);
        builder.Services.AddSingleton(browserSessions);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(roleModels);
        builder.Services.AddSingleton<SkillReviewStore>();
        builder.Services.AddSingleton(interviews);
        builder.Services.AddSingleton(architectureDecisions);
        builder.Services.AddSingleton(handoffs);
        builder.Services.AddSingleton(implementationPlans);
        builder.Services.AddSingleton(engineeringRuns);
        builder.Services.AddSingleton(atlasLedger);
        builder.Services.AddSingleton(atlasImports);
        builder.Services.AddSingleton<IEngineeringTaskModel>(softwareEngineer);
        builder.Services.AddSingleton<EngineeringRunCoordinator>();
        builder.Services.AddSingleton(leadReviews);
        builder.Services.AddSingleton<ILeadSourceReviewer>(leadReviewerOverride ?? new MafLeadSoftwareEngineer());
        builder.Services.AddSingleton<LeadReviewCoordinator>();
        builder.Services.AddSingleton<IEngineeringIssueVerifier>(issueVerifierOverride ?? new MafEngineeringIssueVerifier());
        builder.Services.AddSingleton(qaReviews);
        builder.Services.AddSingleton(repairProposals);
        builder.Services.AddSingleton(repairTriage);
        builder.Services.AddSingleton(repairCycles);
        builder.Services.AddSingleton<CodeAtlasEvidenceService>();
        builder.Services.AddSingleton<RepairCycleCoordinator>();
        builder.Services.AddSingleton<IQaSourceReviewer>(qaEngineer);
        builder.Services.AddSingleton<QaReviewCoordinator>();
        builder.Services.AddSingleton<ILocalSourceSmokeRunner>(sourceSmokeRunner);
        builder.Services.AddSingleton<SourceSmokeCoordinator>();
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
        builder.Services.AddSingleton<EngineeringAtlasNotifier>();
        builder.Services.AddSingleton<EngineeringActivityStore>();
        var collaborationStore=new CollaborationStore(store);
        collaborationStore.Recover();
        builder.Services.AddSingleton(collaborationStore);
        builder.Services.AddSingleton<ICollaborationLead>(collaborationLeadOverride??new MafCollaborationLead());
        builder.Services.AddSingleton<CollaborationCoordinator>();
        builder.Services.AddHostedService<EngineeringActivityNotifier>();

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

        // Capture only post-authenticated API metadata. No request content,
        // path values, query strings, credentials, or model output enter this
        // small ephemeral buffer. The read endpoint is excluded so merely
        // watching the feed cannot create an artificial stream of activity.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/api") ||
                context.Request.Path.StartsWithSegments("/api/studio/http-activity") ||
                context.Request.Path.StartsWithSegments("/api/progress") ||
                context.Request.Path.StartsWithSegments("/api/local-session"))
            {
                await next(context);
                return;
            }
            var stopwatch = Stopwatch.StartNew();
            try
            {
                await next(context);
            }
            finally
            {
                stopwatch.Stop();
                var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
                liveHttpActivity.Record(context.Request.Method, route,
                    context.Response.StatusCode, stopwatch.Elapsed.TotalMilliseconds);
            }
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

        // Authenticated, read-only, bounded presentation data; not an audit log.
        app.MapGet("/api/studio/http-activity",
            (long? after, int? limit, LocalHttpActivityLedger ledger) =>
                Results.Ok(ledger.Read(after ?? 0, limit ?? 20)));

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
        // Owner-scoped model assignment is not an agent instruction or approval.
        // ProductOwner remains explicitly selected/pinned by the interview workflow.
        app.MapGet("/api/tasks/{id}/agent-models",
            (string id, AgentRoleModelSettings settings, InterviewStore interview) =>
                Safe(() => {
                    var current = interview.Read(id);
                    var inherited = current.Specification?.Model ??
                        current.Model ?? LocalInterviewModel.DefaultModel;
                    return Results.Ok(new { agents = settings.Read(id, inherited, current.Model) });
                }));
        app.MapPost("/api/tasks/{id}/agent-models/{role}",
            (string id, string role, SetAgentRoleModelRequest request,
             AgentRoleModelSettings settings, InterviewStore interview,
             IOllamaModelCatalog catalog, CancellationToken cancellation) =>
                SafeAsync(async () => {
                    // Saving an explicit choice requires that exact model to be
                    // downloaded locally; never install or silently substitute.
                    if (request.Model is not null) {
                        if (!LocalOllamaModelCatalog.ValidLocalName(request.Model))
                            throw new StoreFailure("INVALID_INPUT");
                        var installed = await catalog.ListAsync(cancellation);
                        if (!installed.Contains(request.Model, StringComparer.Ordinal))
                            throw new StoreFailure("MODEL_NOT_INSTALLED");
                    }
                    var current = interview.Read(id);
                    var inherited = current.Specification?.Model ??
                        current.Model ?? LocalInterviewModel.DefaultModel;
                    return Results.Ok(settings.Set(id, role, request.Model, inherited));
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
        // Owner-only policy management. These endpoints do not start work.
        app.MapGet("/api/tasks/{id}/engineering-delegation",
            (string id, TaskStore tasks) => Safe(() => Results.Ok(new {
                policy = new EngineeringDelegationStore(tasks).Read(id)
            })));
        app.MapGet("/api/tasks/{id}/engineering-delegation/history",
            (string id, TaskStore tasks) => Safe(() => Results.Ok(new {
                history = new EngineeringDelegationStore(tasks).History(id)
            })));
        app.MapPost("/api/tasks/{id}/engineering-delegation/propose",
            (string id, ProposeDelegationRequest input, TaskStore tasks) =>
                Safe(() => Results.Ok(new EngineeringDelegationStore(tasks).Propose(id, input))));
        app.MapPost("/api/tasks/{id}/engineering-delegation/approve",
            (string id, ApproveDelegationRequest input, TaskStore tasks) =>
                Safe(() => Results.Ok(new EngineeringDelegationStore(tasks).Approve(id, input))));
        app.MapPost("/api/tasks/{id}/engineering-delegation/revoke",
            (string id, ApproveDelegationRequest input, TaskStore tasks) =>
                Safe(() => Results.Ok(new EngineeringDelegationStore(tasks).Revoke(id, input))));
        app.MapGet("/api/tasks/{id}/engineering-activity",
            (string id, long? after, int? limit, EngineeringActivityStore activity) =>
                Safe(() => Results.Ok(activity.Read(id,after ?? 0,limit ?? 200))));
        app.MapGet("/api/tasks/{id}/agent-communications",
            (string id, long? after, string? role, string? direction, TaskStore tasks) =>
                Safe(() => Results.Ok(new {
                    messages = new AgentCommunicationStore(tasks).List(
                        id, after ?? 0, role, direction ?? "all")
                })));
        app.MapGet("/api/tasks/{id}/engineering-messages",
            (string id, long? after, TaskStore tasks) =>
                Safe(() => Results.Ok(new {
                    messages = new EngineeringMessageStore(tasks).List(id, after ?? 0)
                })));
        app.MapGet("/api/tasks/{id}/agent-handoffs",
            (string id, MafHandoffStore db, ImplementationPlanStore plans,
             EngineeringRunStore engineers, QaReviewStore qa) =>
                Safe(() => Results.Ok(new {
                    engine = "Microsoft Agent Framework",
                    executionAvailable = false,
                    events = db.List(id).Concat(plans.Handoffs(id))
                        .Concat(engineers.Handoffs(id)).Concat(qa.Handoffs(id))
                        .OrderBy(e => e.CreatedUtc).ThenBy(e => e.Kind).ToArray()
                })));
        app.MapPost("/api/tasks/{id}/interview/turn", (
                string id, InterviewTurnRequest input,
                TaskStore tasks, InterviewStore db, IInterviewModel model,
                IOllamaModelCatalog catalog, SkillReviewStore skillReviews,
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
                // The previous model response may still contain a question
                // after the original twelve-answer cap. Save the owner's
                // response, then end discovery without generating another
                // unanswerable question or requiring an Ollama round trip.
                if (snapshot.Messages.Count(x => x.Role == "owner") ==
                    InterviewStore.ModelDrivenAnswerLimit)
                {
                    if (input.Skill is not null)
                        throw new StoreFailure("SKILL_SCOPE_DENIED");
                    _ = InterviewStore.ValidOwnerMessage(input.Message);
                    return Results.Ok(db.CommitTurn(
                        id, input with { Model = selected },
                        InterviewStore.FinalAnswerAcknowledgement()));
                }
                var localNames = await catalog.ListAsync(cancellation);
                if (!localNames.Contains(selected, StringComparer.Ordinal))
                    throw new StoreFailure("MODEL_NOT_INSTALLED");
                var context = snapshot.Messages.ToList();
                if (snapshot.LastSequence != 0)
                    context.Add(new InterviewMessage(
                        snapshot.LastSequence + 1, "owner",
                        InterviewStore.ValidOwnerMessage(input.Message),
                        DateTimeOffset.UtcNow.ToString("O")));
                using var providerScope = EngineeringProviderActivity.Enter(tasks,id,
                    "ProductOwner",id);
                InterviewReply reply;
                if (input.Skill is null)
                    reply = await model.AskAsync(task.Objective, context, selected, cancellation);
                else
                {
                    if (model is not MafAgentTeam team)
                        throw new StoreFailure("SKILL_ADAPTER_UNAVAILABLE");
                    var admitted = skillReviews.AdmitDiscovery(id, input.Skill,
                        "interview-turn", input.ExpectedSequence, input.Message, selected);
                    try { reply = await team.AskWithSkillAsync(admitted, cancellation); }
                    catch { skillReviews.FinishDiscovery(admitted, false); throw; }
                    skillReviews.FinishDiscovery(admitted, true);
                }
                return Results.Ok(db.CommitTurn(
                    id, input with { Model = selected }, reply));
            }));
        app.MapPost("/api/tasks/{id}/interview/specification", (
                string id, SpecificationRequest input,
                TaskStore tasks, InterviewStore db, IInterviewModel model,
                IOllamaModelCatalog catalog, SkillReviewStore skillReviews,
                CancellationToken cancellation) =>
            SafeAsync(async () =>
            {
                var task = tasks.GetTask(id).Task;
                var snapshot = db.Read(id);
                if (snapshot.LastSequence != input.ExpectedSequence ||
                    !snapshot.Messages.Any(x => x.Role == "owner") ||
                    snapshot.Model is null)
                    throw new StoreFailure("STALE_INTERVIEW");
                // The interview's pinned model remains unchanged. A separate,
                // explicitly selected installed local model may draft the spec.
                var selectedModel = input.Model ?? snapshot.Model;
                if (!LocalInterviewModel.AllowedModel(selectedModel))
                    throw new StoreFailure("INVALID_MODEL");
                var localNames = await catalog.ListAsync(cancellation);
                if (!localNames.Contains(selectedModel, StringComparer.Ordinal))
                    throw new StoreFailure("MODEL_NOT_INSTALLED");
                using var providerScope = EngineeringProviderActivity.Enter(tasks,id,
                    "ProductOwner",id);
                string content;
                if (input.Skill is null)
                    content = await model.DraftAsync(task.Objective, snapshot.Messages,
                        selectedModel, cancellation);
                else
                {
                    if (model is not MafAgentTeam team)
                        throw new StoreFailure("SKILL_ADAPTER_UNAVAILABLE");
                    var admitted = skillReviews.AdmitDiscovery(id, input.Skill,
                        "specification-draft", input.ExpectedSequence, null, selectedModel);
                    try { content = await team.DraftWithSkillAsync(admitted, cancellation); }
                    catch { skillReviews.FinishDiscovery(admitted, false); throw; }
                    skillReviews.FinishDiscovery(admitted, true);
                }
                return Results.Ok(db.CommitSpecification(
                    id, input.ExpectedSequence, content, selectedModel));
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
                 AgentRoleModelSettings roleModels, SkillReviewStore skillReviews,
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
                var chosen = roleModels.Resolve(id, "SolutionArchitect", spec.Model);
                var models = await catalog.ListAsync(cancellation);
                if (!models.Contains(chosen, StringComparer.Ordinal))
                    throw new StoreFailure("MODEL_NOT_INSTALLED");
                using var providerScope = EngineeringProviderActivity.Enter(tasks,id,
                    "SolutionArchitect",id);
                ArchitectureRecommendation recommendation;
                if (input.Skill is null)
                    recommendation = await advisor.RecommendAsync(
                        task.Objective, spec.Content, chosen, input.Topic!, cancellation);
                else
                {
                    // No implicit fallback if an explicitly selected skill is unavailable.
                    if (advisor is not MafAgentTeam team)
                        throw new StoreFailure("SKILL_ADAPTER_UNAVAILABLE");
                    var context = skillReviews.AdmitArchitecture(id, input, chosen);
                    try
                    {
                        recommendation = await team.RecommendWithSkillAsync(
                            task.Objective, spec.Content, chosen, input.Topic!, context, cancellation);
                    }
                    catch
                    {
                        skillReviews.FinishArchitecture(context, false);
                        throw;
                    }
                    skillReviews.FinishArchitecture(context, true);
                }
                var created = decisions.Propose(id, new(
                    ArchitectureDecisionStore.SchemaVersion,
                    input.SpecificationVersion, input.SpecificationSha256,
                    input.Topic, recommendation.RecommendedOptionId,
                    recommendation.Rationale, recommendation.Options,
                    input.ExpectedRevision));
                handoffs.RecordProposal(id, input.Topic!, spec, created.Revision, chosen);
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
             IOllamaModelCatalog catalog, AgentRoleModelSettings roleModels,
             CancellationToken cancellation) =>
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
                var chosen = roleModels.Resolve(id, "TechnicalLead", spec.Model);
                var installed = await catalog.ListAsync(cancellation);
                if (!installed.Contains(chosen, StringComparer.Ordinal))
                    throw new StoreFailure("MODEL_NOT_INSTALLED");
                using var providerScope = EngineeringProviderActivity.Enter(tasks,id,
                    "TechnicalLead",id);
                var outline = await planner.PlanAsync(task.Objective, spec.Content,
                    architecture, chosen, cancellation);
                // Atomically recheck specification, architecture and revision
                // *after* local inference to reject concurrent human edits.
                var created = db.Propose(id, input, chosen, outline);
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
        app.MapGet("/api/agents/skills", () =>
            Safe(() => Results.Ok(new { skills = SkillCatalog.Discover() })));
        app.MapGet("/api/tasks/{id}/skill-uses", (string id, SkillReviewStore reviews) =>
            Safe(() => Results.Ok(new { events = reviews.ReadUses(id),
                discoveryEvents = reviews.ReadDiscoveryUses(id),
                qaEvents = reviews.ReadQaUses(id) })));
        app.MapGet("/api/tasks/{id}/skill-reviews", (string id, SkillReviewStore reviews) =>
            Safe(() => Results.Ok(new { skills = reviews.Read(id) })));
        app.MapPost("/api/tasks/{id}/skill-reviews/{name}",
            (string id, string name, SkillReviewRequest request, SkillReviewStore reviews) =>
                Safe(() => Results.Ok(reviews.Review(id, name, request))));

        // Fourth real MAF agent: one task at a time from the exact current
        // owner-approved Technical Lead plan, source-only and never executed.
        app.MapGet("/api/tasks/{id}/engineering-runs/latest",
            (string id, EngineeringRunStore db) =>
                Safe(() => Results.Ok(new { snapshot = db.Latest(id) })));
        app.MapPost("/api/tasks/{id}/engineering-runs",
            (string id, StartEngineeringRunRequest request,
             EngineeringRunCoordinator coordinator, ImplementationPlanStore plans,
             AgentRoleModelSettings roleModels, IOllamaModelCatalog catalog,
             CancellationToken cancellation) =>
                SafeAsync(async () => {
                    var inherited = plans.Read(id).Plan?.Model ??
                        LocalInterviewModel.DefaultModel;
                    var model = roleModels.Resolve(id, "SoftwareEngineer", inherited);
                    var installed = await catalog.ListAsync(cancellation);
                    if (!installed.Contains(model, StringComparer.Ordinal))
                        throw new StoreFailure("MODEL_NOT_INSTALLED");
                    return Results.Accepted($"/api/tasks/{id}/engineering-runs",
                        coordinator.Start(id, request, model));
                }));
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}",
            (string id, string runId, EngineeringRunStore db) =>
                Safe(() => Results.Ok(db.Get(id, runId))));
        app.MapGet("/api/tasks/{id}/delivery-evidence",
            (string id,TaskStore tasks)=>Safe(()=>Results.Ok(new DeliveryEvidenceStore(tasks).Read(id))));
        // M7: import only a public, exact-commit GitHub source archive.
        // No repo scripts, assembly loading, shell or untrusted execution.
        app.MapGet("/api/tasks/{id}/atlas-imports",
            (string id, CodeAtlasRepositoryImportStore db) =>
                Safe(() => Results.Ok(db.List(id))));
        app.MapPost("/api/tasks/{id}/atlas-imports",
            (string id, AtlasImportGitHubRequest request,
             CodeAtlasRepositoryImportStore db, CancellationToken cancellation) =>
                SafeAsync(async () => Results.Ok(
                    await db.ImportAsync(id, request, cancellation))));
        // Two already-saved, task-scoped, immutable GitHub commits.
        app.MapGet("/api/tasks/{id}/atlas-imports/drift",
            (string id, string from, string to,
             CodeAtlasRepositoryImportStore db) =>
                Safe(() => Results.Ok(CodeAtlasImportedCommitDrift.Compare(
                    db.Get(id, from), db.Get(id, to)))));
        app.MapGet("/api/tasks/{id}/atlas-imports/{importId}",
            (string id, string importId, CodeAtlasRepositoryImportStore db) =>
                Safe(() => Results.Ok(db.Get(id, importId))));
        app.MapGet("/api/tasks/{id}/atlas-imports/{importId}/file",
            (string id, string importId, string path,
             CodeAtlasRepositoryImportStore db) =>
                Safe(() => Results.Ok(db.GetFile(id, importId, path))));
        app.MapGet("/api/tasks/{id}/atlas-imports/{importId}/path",
            (string id, string importId, string from, string to,
             int? maxHops, CodeAtlasRepositoryImportStore db) =>
                Safe(() => Results.Ok(CodeAtlasPathFinder.Trace(
                    db.Get(id, importId).Graph, from, to, maxHops ?? 6))));
        // Atlas is a read-only projection of persisted source. It performs
        // syntax parsing only; it does not compile or execute user code.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/atlas",
            (string id, string runId, EngineeringRunStore db) =>
                Safe(() => Results.Ok(CodeAtlasIndexer.Index(db.Get(id, runId)))));
        // Immutable historical graph checkpoints; playback does not execute
        // source or pretend the latest source text matches older revisions.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/atlas/replay",
            (string id, string runId, EngineeringAtlasLedger ledger) =>
                Safe(() => Results.Ok(ledger.Timeline(id, runId))));
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/atlas/replay/{sequence:int}",
            (string id, string runId, int sequence, EngineeringAtlasLedger ledger) =>
                Safe(() => Results.Ok(ledger.Replay(id, runId, sequence))));
        // M8: exact two-checkpoint architecture drift; existing run/task
        // scoping and owner authentication apply to both historical reads.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/atlas/drift",
            (string id, string runId, int from, int to,
             EngineeringAtlasLedger ledger) =>
                Safe(() => Results.Ok(CodeAtlasArchitectureDrift.Compare(
                    ledger.Replay(id, runId, from),
                    ledger.Replay(id, runId, to)))));
        // Bounded directed traversal of source-backed relationships.
        // It neither executes a project nor invents dynamic call targets.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/atlas/path",
            (string id, string runId, string from, string to,
             int? maxHops, EngineeringRunStore db) =>
                Safe(() => Results.Ok(CodeAtlasPathFinder.Trace(
                    CodeAtlasIndexer.Index(db.Get(id, runId)),
                    from, to, maxHops ?? 6))));
        // QA and repair findings remain hypotheses until independent runtime
        // verification. Overlay is read-only and bound to this saved run.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/atlas/evidence",
            (string id, string runId, CodeAtlasEvidenceService evidence) =>
                Safe(() => Results.Ok(evidence.Read(id, runId))));
        // Durable source-change cursor; no source contents cross the event wire.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/atlas-events",
            (string id, string runId, int? after, EngineeringAtlasLedger ledger) =>
                Safe(() => Results.Ok(ledger.Read(id, runId, after ?? 0))));
        // Read-only deterministic source inspection: no Node, Chromium,
        // model, sandbox admission, independent tests, or VERIFIED claim.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/static-web-preflight",
            (string id, string runId, EngineeringRunStore db) =>
                Safe(() => Results.Ok(
                    StaticWebSourcePreflight.Inspect(db.Get(id, runId)))));
        // Stack-aware readiness is a source-only report: no package manager,
        // runner, browser, untrusted process or verification admission.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/source-readiness",
            (string id, string runId, EngineeringRunStore db) =>
                Safe(() => Results.Ok(
                    ProjectSourceReadiness.Inspect(db.Get(id, runId)))));
        // M4.5 exact, read-only verification-scope preview: task-bound source
        // digests and current approvals only. This is NOT a runnable contract.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/verification-scope",
            (string id, string runId, EngineeringRunStore db,
             ImplementationPlanStore plans, InterviewStore interviews,
             TaskStore tasks) => Safe(() => {
                 var source = db.Get(id, runId);
                 var current = db.Latest(id);
                 var plan = plans.Read(id).Plan;
                 var specification = interviews.Read(id).Specification;
                 var architecture = DraftArchitecturePolicy.Readiness(
                     tasks, interviews, id);
                 return Results.Ok(StaticWebVerificationScope.Preview(
                     source, plan, specification, architecture,
                     current?.Job.Id == runId));
             }));
        // One explicit owner click uses the exact latest approved source
        // snapshot; no ZIP download, no arbitrary file/command input.
        // Chromium inner sandbox is disabled ONLY inside restricted Docker.
        // This is a smoke test, never project VERIFIED or runtime QUALIFIED.
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/source-smoke-evidence",
            (string id,string runId,TaskStore tasks)=>Safe(()=>Results.Ok(new {evidence=new SourceSmokeEvidenceStore(tasks).List(id,runId)})));
        app.MapPost("/api/tasks/{id}/engineering-runs/{runId}/source-smoke",
            (string id, string runId, StartSourceSmokeRequest request,
             SourceSmokeCoordinator coordinator,
             CancellationToken cancellation) =>
                SafeAsync(async () => Results.Ok(await coordinator.RunAsync(
                    id, runId, request, cancellation))));
        app.MapPost("/api/tasks/{id}/engineering-runs/{runId}/cancel",
            (string id, string runId, EngineeringRunCoordinator coordinator) =>
                Safe(() => Results.Ok(coordinator.Cancel(id, runId))));
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/zip",
            (string id, string runId, EngineeringRunStore db) =>
                Safe(() => {
                    var snapshot = db.Get(id, runId);
                    var filename = snapshot.Job.State == "Generated"
                        ? "codingassistant-engineering-source.zip"
                        : "codingassistant-engineering-PARTIAL-unverified.zip";
                    return Results.File(db.Zip(id, runId), "application/zip", filename);
                }));
        // Phase 5: independent, read-only MAF QA. Owner must initiate a
        // completed, current-plan engineering run. Static review is NOT a
        // build, execution, test run, security qualification or PASS.
        // R1: a persisted, immutable triage proposal, not a repair or test run.
        app.MapGet("/api/tasks/{id}/repair-proposals/latest",
            (string id, RepairProposalStore db) =>
                Safe(() => Results.Ok(new { proposal = db.Latest(id) })));
        app.MapPost("/api/tasks/{id}/repair-proposals",
            (string id, CreateRepairProposalRequest request,
             RepairProposalStore db) =>
                Safe(() => Results.Created(
                    $"/api/tasks/{id}/repair-proposals/latest",
                    db.Propose(id, request))));
        app.MapGet("/api/tasks/{id}/repair-proposals/{proposalId}/triage",
            (string id, string proposalId, RepairTriageStore db) =>
                Safe(() => Results.Ok(new { decisions = db.List(id, proposalId) })));
        app.MapPost("/api/tasks/{id}/repair-proposals/{proposalId}/triage",
            (string id, string proposalId, ClassifyRepairFindingRequest request,
             RepairTriageStore db) =>
                Safe(() => Results.Ok(db.Classify(id, proposalId, request))));
        // R2-R5: owner-authorized repairs remain source-only. Persisted
        // progress is derived from actual engineering and QA records.
        app.MapGet("/api/tasks/{id}/repair-cycles",
            (string id, RepairCycleStore db) =>
                Safe(() => Results.Ok(new { cycles = db.History(id) })));
        app.MapGet("/api/tasks/{id}/repair-cycles/latest",
            (string id, RepairCycleStore db) =>
                Safe(() => Results.Ok(new { snapshot = db.Latest(id) })));
        app.MapGet("/api/tasks/{id}/repair-cycles/{cycleId}",
            (string id, string cycleId, RepairCycleStore db) =>
                Safe(() => Results.Ok(db.Get(id, cycleId))));
        app.MapPost("/api/tasks/{id}/repair-cycles",
            (string id, StartRepairCycleRequest request,
             RepairCycleCoordinator coordinator, ImplementationPlanStore plans,
             AgentRoleModelSettings roleModels, IOllamaModelCatalog catalog,
             CancellationToken cancellation) =>
                SafeAsync(async () => {
                    var inherited = plans.Read(id).Plan?.Model ??
                        LocalInterviewModel.DefaultModel;
                    var engineer = roleModels.Resolve(id, "SoftwareEngineer", inherited);
                    var qa = roleModels.Resolve(id, "QaEngineer", inherited);
                    var installed = await catalog.ListAsync(cancellation);
                    if (!installed.Contains(engineer, StringComparer.Ordinal) ||
                        !installed.Contains(qa, StringComparer.Ordinal) ||
                        (request.ContinueCollaborationId is not null &&
                            (!installed.Contains(roleModels.Resolve(id,"TechnicalLead",inherited),StringComparer.Ordinal) ||
                             !installed.Contains(roleModels.Resolve(id,"LeadSoftwareEngineer",inherited),StringComparer.Ordinal))))
                        throw new StoreFailure("MODEL_NOT_INSTALLED");
                    return Results.Accepted($"/api/tasks/{id}/repair-cycles",
                        coordinator.Start(id, request, engineer, qa));
                }));
        app.MapPost("/api/tasks/{id}/repair-cycles/{cycleId}/cancel",
            (string id, string cycleId, RepairCycleCoordinator coordinator) =>
                Safe(() => Results.Ok(coordinator.Cancel(id, cycleId))));
        app.MapGet("/api/tasks/{id}/engineering-runs/{runId}/repair-progress",
            (string id,string runId,TaskStore tasks)=>Safe(()=>Results.Ok(new RepairProgressStore(tasks).Read(id,runId))));
        app.MapGet("/api/inference-capacity",()=>Results.Ok(LocalInferenceScheduler.Shared.Snapshot()));
        app.MapGet("/api/tasks/{id}/inference-leases",
            (string id,int? limit,TaskStore tasks)=>Safe(()=>Results.Ok(new {leases=new InferenceLeaseStore(tasks).List(id,limit??100)})));
        app.MapGet("/api/tasks/{id}/collaboration/preview",
            (string id,CollaborationStore db)=>Safe(()=>Results.Ok(db.Preview(id))));
        app.MapGet("/api/tasks/{id}/collaboration/latest",
            (string id,CollaborationStore db)=>Safe(()=>Results.Ok(new {snapshot=db.Latest(id)})));
        app.MapPost("/api/tasks/{id}/collaboration",
            (string id,StartCollaborationRequest request,CollaborationCoordinator coordinator,CancellationToken cancellation)=>
                SafeAsync(async()=>Results.Accepted($"/api/tasks/{id}/collaboration/latest",
                    await coordinator.StartAsync(id,request,cancellation))));
        app.MapPost("/api/tasks/{id}/collaboration/{jobId}/resume",
            (string id,string jobId,StartCollaborationRequest request,CollaborationCoordinator coordinator,CancellationToken cancellation)=>
                SafeAsync(async()=>Results.Accepted($"/api/tasks/{id}/collaboration/latest",
                    await coordinator.ResumeAsync(id,jobId,request,cancellation))));
        app.MapPost("/api/tasks/{id}/repair-cycles/{cycleId}/resume-collaboration",
            (string id,string cycleId,StartCollaborationRequest request,CollaborationCoordinator coordinator,CancellationToken cancellation)=>
                SafeAsync(async()=>Results.Accepted($"/api/tasks/{id}/collaboration/latest",
                    await coordinator.StartAsync(id,request,cancellation,cycleId))));
        app.MapPost("/api/tasks/{id}/collaboration/{jobId}/cancel",
            (string id,string jobId,CollaborationCoordinator coordinator)=>Safe(()=>Results.Ok(coordinator.Cancel(id,jobId))));
        app.MapGet("/api/tasks/{id}/adaptive-work-plan",
            (string id,TaskStore tasks)=>Safe(()=>Results.Ok(new AdaptiveWorkPlanStore(tasks).Read(id))));
        app.MapGet("/api/tasks/{id}/engineering-issues",
            (string id,string? runId,TaskStore tasks) =>
                Safe(() => Results.Ok(new EngineeringIssueStore(tasks).Read(id,runId))));
        app.MapPost("/api/tasks/{id}/engineering-issues/verify",
            (string id,VerifyEngineeringIssueRequest request,TaskStore tasks,
             IEngineeringIssueVerifier verifier,AgentRoleModelSettings roleModels,
             IOllamaModelCatalog catalog,CancellationToken cancellation)=>
                SafeAsync(async ()=>{
                    var store=new IssueVerificationStore(tasks);
                    var input=store.Prepare(id,request);
                    var role=input.Issue.Origin;
                    var model=roleModels.Resolve(id,role,input.Plan.Model);
                    if(!(await catalog.ListAsync(cancellation)).Contains(model,StringComparer.Ordinal))
                        throw new StoreFailure("MODEL_NOT_INSTALLED");
                    using var scope=EngineeringProviderActivity.Enter(tasks,id,role,
                        input.Issue.Id,input.Request.CandidateSourceSha256);
                    var assessment=await verifier.VerifyAsync(input,model,cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    return Results.Ok(store.Record(id,input,role,model,assessment));
                }));
        app.MapGet("/api/tasks/{id}/lead-reviews/latest",
            (string id, LeadReviewStore db) =>
                Safe(() => Results.Ok(new { snapshot = db.Latest(id) })));
        app.MapPost("/api/tasks/{id}/lead-reviews",
            (string id, StartQaReviewRequest request,
             LeadReviewCoordinator coordinator, ImplementationPlanStore plans,
             AgentRoleModelSettings roleModels, IOllamaModelCatalog catalog,
             CancellationToken cancellation) =>
                SafeAsync(async () => {
                    var inherited = plans.Read(id).Plan?.Model ?? LocalInterviewModel.DefaultModel;
                    var model = roleModels.Resolve(id, "LeadSoftwareEngineer", inherited);
                    var installed = await catalog.ListAsync(cancellation);
                    if (!installed.Contains(model, StringComparer.Ordinal))
                        throw new StoreFailure("MODEL_NOT_INSTALLED");
                    return Results.Accepted($"/api/tasks/{id}/lead-reviews",
                        coordinator.Start(id,request,model));
                }));
        app.MapGet("/api/tasks/{id}/lead-reviews/{reviewId}",
            (string id,string reviewId,LeadReviewStore db) =>
                Safe(() => Results.Ok(db.Get(id,reviewId))));
        app.MapPost("/api/tasks/{id}/lead-reviews/{reviewId}/cancel",
            (string id,string reviewId,LeadReviewCoordinator coordinator) =>
                Safe(() => Results.Ok(coordinator.Cancel(id,reviewId))));
        app.MapGet("/api/tasks/{id}/qa-reviews/latest",
            (string id, QaReviewStore db) =>
                Safe(() => Results.Ok(new { snapshot = db.Latest(id) })));
        app.MapPost("/api/tasks/{id}/qa-reviews",
            (string id, StartQaReviewRequest request,
             QaReviewCoordinator coordinator, ImplementationPlanStore plans,
             AgentRoleModelSettings roleModels, IOllamaModelCatalog catalog,
             CancellationToken cancellation) =>
                SafeAsync(async () => {
                    var inherited = plans.Read(id).Plan?.Model ??
                        LocalInterviewModel.DefaultModel;
                    var model = roleModels.Resolve(id, "QaEngineer", inherited);
                    var installed = await catalog.ListAsync(cancellation);
                    if (!installed.Contains(model, StringComparer.Ordinal))
                        throw new StoreFailure("MODEL_NOT_INSTALLED");
                    return Results.Accepted($"/api/tasks/{id}/qa-reviews",
                        coordinator.Start(id, request, model));
                }));
        app.MapGet("/api/tasks/{id}/qa-reviews/{reviewId}",
            (string id, string reviewId, QaReviewStore db) =>
                Safe(() => Results.Ok(db.Get(id, reviewId))));
        app.MapPost("/api/tasks/{id}/qa-reviews/{reviewId}/cancel",
            (string id, string reviewId, QaReviewCoordinator coordinator) =>
                Safe(() => Results.Ok(coordinator.Cancel(id, reviewId))));
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
            "LOCAL_MODEL_UNAVAILABLE" or "MODEL_NOT_INSTALLED" or "PRIVATE_DATA_UNAVAILABLE" or
                "INFERENCE_QUEUE_FULL" or "INFERENCE_QUEUE_TIMEOUT" or "INFERENCE_LEASE_INTERRUPTED" =>
                StatusCodes.Status503ServiceUnavailable,
            "REPAIR_NOT_AUTHORIZED" or "REPAIR_LIMIT_REACHED" or
                "REPAIR_SCOPE_TOO_LARGE" or "REPAIR_SCOPE_VIOLATION" or
                "REPAIR_NO_CHANGE" or
            "BUILD_BUSY" or "BUILD_NOT_COMPLETE" or
                "STALE_INTERVIEW" or "SPECIFICATION_CONFLICT" or
                "ARCHITECTURE_NOT_READY" or "ARCHITECTURE_CONFLICT" or
                "ARCHITECTURE_PLAN_CONFLICT" or
                "STALE_DECISION" or "STALE_IMPLEMENTATION_PLAN" or
                "STALE_SOURCE_RUN" or "RESUME_NOT_AVAILABLE" or
                "SOURCE_CHANGED" or "SOURCE_SCOPE_CHANGED" or
                "SMOKE_BUSY" or "SMOKE_ISOLATION_ACK_REQUIRED" or
                "SPECIFICATION_NOT_APPROVED" or
                "STALE_DELEGATION_POLICY" or "DELEGATION_NOT_AUTHORIZED" or
                "STALE_WORK_PLAN" or "WORK_SCOPE_EXPANSION" or "WORK_DEPENDENCY_CYCLE" or
                "WORK_NOT_READY" or "WORK_SCOPE_VIOLATION" or "WORK_ALREADY_COMPLETED" or
            "STALE_SKILL_REVIEW" or "STALE_VERSION" or "IDEMPOTENCY_CONFLICT" or
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
