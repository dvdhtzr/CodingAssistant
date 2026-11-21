using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO.Compression;
using System.Text.Json;
using CodingAssistant.Api;
using Microsoft.Extensions.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

const string Token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition)
        throw new Exception("Phase 2 acceptance failed: " + label);
    checks++;
}

async Task<HttpClient> Start(WebApplication app)
{
    await app.StartAsync();
    var addresses = app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()?.Addresses;
    var address = addresses?.SingleOrDefault() ??
        throw new Exception("Missing loopback address");
    Check(address.StartsWith("http://127.0.0.1:", StringComparison.Ordinal),
        "HTTP API stays on IPv4 loopback");
    return new HttpClient(new SocketsHttpHandler { UseProxy = false })
    {
        BaseAddress = new Uri(address),
        DefaultRequestHeaders = { Authorization =
            new AuthenticationHeaderValue("Bearer", Token) }
    };
}

async Task<JsonDocument> Body(HttpResponseMessage response) =>
    JsonDocument.Parse(await response.Content.ReadAsStringAsync());

async Task<HttpResponseMessage> Post(HttpClient client, string path, object obj) =>
    await client.PostAsJsonAsync(path, obj);

// Strict structured output is only a proposal, never owner approval.
{
    var parsed = LocalArchitectureAdvisor.Parse(
        """{"recommendedOptionId":"static-web","rationale":"An offline page is proportional.","options":[{"id":"static-web","label":"Static web","benefit":"Offline","tradeoff":"Browser only","runtimeProfileId":"static-web"},{"id":"react","label":"React app","benefit":"Component UI","tradeoff":"Build tools","runtimeProfileId":null}]}""");
    Check(parsed.Options.Count == 2 && parsed.RecommendedOptionId == "static-web",
        "local Solution Architect model output parses as structured options");
    try
    {
        _ = LocalArchitectureAdvisor.Parse("not valid JSON");
        Check(false, "malformed output must not be admitted");
    }
    catch (StoreFailure e) when (e.Code == "MODEL_OUTPUT_REJECTED")
    {
        Check(true, "malformed model response rejected");
    }
}

// The source-file manifest is NOT the Technical Lead's engineering task
// plan. Small local models may return unrelated JSON shapes or invalid fields.
{
    var sourceFiles = LocalProjectDraftModel.ParseFileManifest(
        """{"files":[{"path":"index.html","purpose":"Browser entry point"},{"path":"app.js","purpose":"Wire user controls"}]}""");
    Check(sourceFiles.Count == 2 && sourceFiles[0].Path == "index.html",
        "strict local file-manifest JSON accepts the documented shape");
    foreach (var invalid in new[] {
        """{"tasks":[{"id":"create-ui","title":"Plan tasks"}]}""",
        """{"files":[{"path":"index.html"}]}""",
        """{"files":[{"path":"index.html","purpose":"bad"},{"path":"index.html","purpose":"duplicate path"}]}""",
        """{"files":[]}""",
        """{"files":"index.html"}""",
        """{"files":[{"path":"app.js","purpose":"Valid JavaScript file"}]""",
    })
    {
        try
        {
            _ = LocalProjectDraftModel.ParseFileManifest(invalid);
            Check(false, "invalid local JSON cannot pass source plan parser");
        }
        catch (StoreFailure error) when (error.Code == "INVALID_PROJECT_PLAN")
        {
            Check(true, "malformed/missing/duplicate file-manifest data rejected");
        }
    }
}

// Actual MAF invocation path using an injected no-network IChatClient.
// No direct LocalInterviewModel/LocalArchitectureAdvisor legacy path is used.
{
    var factory = new Phase3FakeMafFactory();
    var team = new MafAgentTeam(factory);
    var first = await team.AskAsync("Build an offline notes tracker", [],
        "qwen3.5:4b", CancellationToken.None);
    Check(first.Message.Contains("users") && !first.ReadyForSpecification,
        "MAF Product Owner agent turns the objective into a real follow-up question");
    var interview = new InterviewMessage[] {
        new(1, "assistant", first.Message, "2026-11-20T00:00:00Z"),
        new(2, "owner", "Just me, working offline", "2026-11-20T00:00:01Z")
    };
    var draft = await team.DraftAsync("Build an offline notes tracker",
        interview, "qwen3.5:4b", CancellationToken.None);
    Check(draft.Length >= 180 && draft.Contains("acceptance"),
        "MAF Product Owner generates a validated specification from saved transcript");
    var advice = await team.RecommendAsync("Build an offline notes tracker",
        draft, "qwen3.5:4b", "technology-stack", CancellationToken.None);
    Check(advice.Options.Count == 2 && advice.RecommendedOptionId == "static-web",
        "distinct MAF Solution Architect agent returns proposal, not an approval");
    var technicalPlan = await team.PlanAsync(
        "Build an offline notes tracker", draft,
        new DraftArchitectureReadiness("READY", true, new string('a', 64), [],
            [
                new DraftArchitectureChoice("technology-stack", 1,
                    new string('b', 64), "static-web", "Static HTML and JavaScript",
                    "Offline", "Manual DOM", "static-web"),
                new DraftArchitectureChoice("architecture-style", 1,
                    new string('c', 64), "small-modules", "Small modules",
                    "Simple to test", "Manual boundaries", null)
            ]),
        "qwen3.5:4b", CancellationToken.None);
    Check(technicalPlan.Tasks.Count == 2 &&
          technicalPlan.Tasks[1].DependsOn.SequenceEqual(["create-ui"]) &&
          technicalPlan.Tasks[1].AcceptanceCriteria.Count > 0,
        "real MAF Technical Lead returns dependency-ordered, checkable work");
    Check(factory.Purposes.SequenceEqual(new[] {
            MafAgentPurpose.Interview, MafAgentPurpose.Specification,
            MafAgentPurpose.Architecture, MafAgentPurpose.TechnicalLead
        }) && factory.SystemPoliciesSeen == 4 &&
        factory.AllowedToolsSeen == 0 &&
        factory.Models.All(m => m == "qwen3.5:4b"),
        "three MAF specialists keep exact model, policies and no tools");
    try
    {
        _ = ImplementationPlanContract.Parse(
            """{"summary":"A valid plan summary for offline notes.","tasks":[{"id":"one-task","title":"Create working UI","description":"Implement user controls","dependsOn":["later-task"],"acceptanceCriteria":["Clicks visibly work"]},{"id":"later-task","title":"Persist local data","description":"Store offline notes","dependsOn":[],"acceptanceCriteria":["Reload retains notes"]}],"testStrategy":["Independently click buttons"],"risks":[]}""");
        Check(false, "forward dependencies must be rejected");
    }
    catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_REJECTED")
    {
        Check(true, "Technical Lead model cannot create cyclic or forward dependencies");
    }
}

// Five true MAF roles have separate goals, fictional work histories and
// no rights to use executable tools or load skills merely by naming them.
{
    var roster = EngineeringAgentRoster.All;
    Check(roster.Count == 5 &&
          roster.Select(a => a.Id).Distinct().Count() == 5 &&
          roster.All(a => a.Goal.Length > 25 &&
              a.Backstory.Length > 25 && !a.CanExecuteCode) &&
          EngineeringAgentRoster.Get("SoftwareEngineer")
              .AllowedSkillScopes.Contains("source-implementation") &&
          EngineeringAgentRoster.Get("QaEngineer")
              .AllowedSkillScopes.Contains("static-code-review"),
        "five distinct role/goals/backstories and non-executable skill scopes");

    var manifest = MafSoftwareEngineer.Parse(
        """{"summary":"Implemented the task with a functional browser entry page.","files":[{"path":"index.html","purpose":"Complete browser interface","content":"<!doctype html><html><body><button id='save'>Save</button></body></html>"}],"verificationNotes":["Click the Save button in a browser"]}""");
    Check(manifest.Files.Count == 1 &&
          manifest.Files[0].Path == "index.html",
        "Software Engineer accepts safe bounded complete file replacements");
    foreach (var invalid in new[] {
        """{"summary":"Valid-looking but empty source list.","files":[],"verificationNotes":[]}""",
        """{"summary":"Unsafe path in source file response.","files":[{"path":"../escape.js","purpose":"Unsafe path example","content":"console.log('bad')"}],"verificationNotes":[]}""",
        """{"summary":"Duplicate paths in source file response.","files":[{"path":"app.js","purpose":"First JavaScript file","content":"console.log('one')"},{"path":"APP.js","purpose":"Duplicate same path","content":"console.log('two')"}],"verificationNotes":[]}""",
        """{"tasks":[{"id":"implementation-task"}]}""",
    })
    {
        try { _ = MafSoftwareEngineer.Parse(invalid); Check(false, "invalid engineer output accepted"); }
        catch (StoreFailure e) when (e.Code == "MODEL_OUTPUT_REJECTED")
        { Check(true, "unsafe/duplicate/task-plan engineer response rejected"); }
    }
    var agentFactory = new Phase4FakeMafFactory();
    var fakePlan = new ImplementationPlan(
        "a".PadRight(32, 'a'), 1, 1, new string('b', 64),
        new string('c', 64), "qwen3.5:4b",
        new ImplementationOutline("Build an offline notes app with working controls.",
            [new ImplementationWorkItem("create-ui", "Create user interface",
                "Implement the browser interface with real controls.", [],
                ["A person can click Save in the browser"])],
            ["Independently test browser interaction"], []),
        new string('d', 64), "Approved", "2026-11-21T00:00:00Z",
        "2026-11-21T00:01:00Z", true, "Approved");
    var architecture = new DraftArchitectureReadiness(
        "READY", true, new string('c', 64), [],
        [new DraftArchitectureChoice("technology-stack", 1,
            new string('e', 64), "static-web",
            "Static HTML and JavaScript", "Offline", "DOM", "static-web"),
         new DraftArchitectureChoice("architecture-style", 1,
            new string('f', 64), "modular",
            "Simple modules", "Explicit boundaries", "Extra wiring", null)]);
    var assignment = new EngineeringTaskInput("Build offline notes",
        "Offline notes; keyboard and mouse Save controls.", fakePlan,
        architecture, fakePlan.Outline.Tasks[0], []);
    var output = await new MafSoftwareEngineer(agentFactory).ImplementAsync(
        assignment, CancellationToken.None);
    Check(output.Files.Count == 1 &&
          agentFactory.Purposes.SequenceEqual([MafAgentPurpose.SoftwareEngineer]) &&
          agentFactory.Models.SequenceEqual(["qwen3.5:4b"]) &&
          agentFactory.SystemPolicySeen && agentFactory.NoTools &&
          agentFactory.TaskDescriptionSeen,
        "fourth real MAF agent gets independent identity, task, exact model and no tools");
}

// Phase 5 QA: strict review-only JSON, separate MAF identity and exact
// approved source with NO ability to run tests or call external tools.
{
    var parsed = MafQaEngineer.Parse(
        """{"summary":"The source has an event handler but does not yet demonstrate persistence.","findings":[{"severity":"Major","requirement":"Tasks survive reload","evidence":"The code only logs clicks and has no storage call","recommendation":"Add storage and verify reload in a browser"}],"manualChecks":["Reload browser after saving a task and inspect storage"]}""");
    Check(parsed.Findings.Count == 1 &&
          parsed.Findings[0].Severity == "Major",
        "independent QA accepts only bounded potential findings, not test results");
    foreach (var invalid in new[] {
        """{"summary":"This does not count as a test pass.","findings":[],"manualChecks":[]}""",
        """{"summary":"This does not count as a test pass.","findings":[{"severity":"Passed","requirement":"Store tasks","evidence":"Mock evidence should not be accepted","recommendation":"Check the actual browser later"}],"manualChecks":["Reload the browser and inspect storage"]}""",
        """{"summary":"This does not count as a test pass.","findings":"looks fine","manualChecks":["Test it in a real browser later"]}""",
        """{"status":"PASS","summary":"Runs fine!"}"""
    })
    {
        try { _ = MafQaEngineer.Parse(invalid); Check(false, "invalid QA output accepted"); }
        catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_REJECTED")
        { Check(true, "QA cannot output invented PASS or malformed review"); }
    }
    var fakeFactory = new Phase5FakeMafFactory();
    var fakePlan = new ImplementationPlan(
        new string('a', 32), 1, 1, new string('b', 64),
        new string('c', 64), "qwen3.5:4b",
        new ImplementationOutline(
            "Build a small offline notes tracker with clickable save controls.",
            [new ImplementationWorkItem("create-ui", "Create working controls",
                "Implement a complete page with functional buttons.", [],
                ["Save button responds to mouse and keyboard"])],
            ["Verify click and reload behaviors independently"], []),
        new string('d', 64), "Approved", "2026-11-21T00:00:00Z",
        "2026-11-21T00:01:00Z", true, "Approved");
    var arch = new DraftArchitectureReadiness("READY", true,
        new string('c', 64), [],
        [new DraftArchitectureChoice("technology-stack", 1,
            new string('e', 64), "static-web", "Static Web", "Offline",
            "No server", "static-web"),
         new DraftArchitectureChoice("architecture-style", 1,
            new string('f', 64), "simple", "Simple modules",
            "Fast", "Manual interfaces", null)]);
    var run = new EngineeringRun(new string('9', 32), fakePlan.TaskId,
        1, fakePlan.PlanSha256, 1, fakePlan.SpecificationSha256,
        fakePlan.ArchitectureSha256, fakePlan.Model,
        "Generated", "SourceOnlyGenerated", null,
        "2026-11-21T00:02:00Z", "2026-11-21T00:03:00Z");
    var file = new EngineeringSourceFile("index.html", "Offline app entry",
        "<html><body><button>Save</button></body></html>",
        new string('8', 64), "create-ui", "2026-11-21T00:03:00Z");
    var assessment = await new MafQaEngineer(fakeFactory).ReviewAsync(
        new QaReviewInput("Build notes", "Approved offline notes specification",
            fakePlan, arch, run, file, [file]), CancellationToken.None);
    Check(assessment.Findings.Count == 1 &&
          fakeFactory.Purposes.SequenceEqual([MafAgentPurpose.QaEngineer]) &&
          fakeFactory.Models.SequenceEqual(["qwen3.5:4b"]) &&
          fakeFactory.QaRoleSeen && fakeFactory.NoTools &&
          fakeFactory.SourceSeen,
        "fifth real MAF agent independently inspects exact source without tools");
}

var root = Path.Combine(Path.GetTempPath(),
    "ca-phase2-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(root,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
WebApplication? app = null;
HttpClient? client = null;
var boundModel = new Phase2DraftModel();
var leadModel = new Phase2TechnicalLeadPlanner();
var softwareModel = new Phase4EngineeringModel();
var qaModel = new Phase5QaModel();
try
{
    var tasks = new TaskStore(root);
    var interviews = new InterviewStore(tasks);
    interviews.Initialize();
    var task = tasks.CreateTask(new CreateTaskRequest(
        "Calculator", "Offline journal calculator",
        Guid.NewGuid().ToString("N")));
    var first = interviews.CommitTurn(task.Id,
        new InterviewTurnRequest(0, null, "qwen3.5:4b"),
        new InterviewReply("Who uses this calculation journal?", false));
    var second = interviews.CommitTurn(task.Id,
        new InterviewTurnRequest(first.LastSequence,
            "An offline user needs decimals and history.", "qwen3.5:4b"),
        new InterviewReply("Should history survive reloads?", true));
    var drafted = interviews.CommitSpecification(task.Id, second.LastSequence,
        ("An offline calculation journal with keyboard and mouse input; " +
         "acceptance requires arithmetic, a Back action, and durable history. " +
         "Deployment requires no cloud connectivity and all source files must link correctly. ")
        .PadRight(200, 'x'));
    var spec = drafted.Specification!;
    interviews.Approve(task.Id, new ApproveSpecificationRequest(
        spec.Version, spec.Sha256));

    var settings = new ApiSettings(root, Token, "http://127.0.0.1:0");
    app = ApiHost.Build([], settings, projectModelOverride: boundModel,
        catalogOverride: new Phase2Catalog(),
        architectureAdvisorOverride: new Phase2Advisor(),
        implementationPlannerOverride: leadModel,
        engineeringModelOverride: softwareModel,
        qaReviewerOverride: qaModel);
    client = await Start(app);
    using (var unauthenticated = new HttpClient { BaseAddress = client.BaseAddress })
    using (var denied = await unauthenticated.GetAsync("/api/runtime-profiles"))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized,
            "runtime profiles are authenticated");


    // Model-free injected fixture: actual HTTP, approved spec, private SQLite.
    var recommendPath = "/api/tasks/" + task.Id + "/architecture-decisions/recommend";
    using (var badTopic = await Post(client, recommendPath, new {
        schemaVersion = 1, specificationVersion = spec.Version,
        specificationSha256 = spec.Sha256, topic = "execute-shell",
        expectedRevision = 0
    }))
        Check(badTopic.StatusCode == HttpStatusCode.BadRequest,
            "recommend endpoint rejects arbitrary topics and execution attempts");
    using (var wrongSpec = await Post(client, recommendPath, new {
        schemaVersion = 1, specificationVersion = spec.Version,
        specificationSha256 = new string('0', 64),
        topic = "architecture-style", expectedRevision = 0
    }))
        Check(wrongSpec.StatusCode == HttpStatusCode.Conflict,
            "advisor cannot propose against changed spec hash");
    using (var recommended = await Post(client, recommendPath, new {
        schemaVersion = 1, specificationVersion = spec.Version,
        specificationSha256 = spec.Sha256, topic = "architecture-style",
        expectedRevision = 0
    }))
    using (var body = await Body(recommended))
    {
        Check(recommended.StatusCode == HttpStatusCode.Created,
            "Solution Architect proposal persisted through HTTP");
        var item = body.RootElement;
        Check(item.GetProperty("status").GetString() == "Proposed" &&
              item.GetProperty("selectedOptionId").ValueKind == JsonValueKind.Null &&
              item.GetProperty("recommendedOptionId").GetString() == "minimal-modules",
            "AI recommendation is not owner-approved selection");
    }
    using (var repeatedRecommend = await Post(client, recommendPath, new {
        schemaVersion = 1, specificationVersion = spec.Version,
        specificationSha256 = spec.Sha256, topic = "architecture-style",
        expectedRevision = 0
    }))
        Check(repeatedRecommend.StatusCode == HttpStatusCode.Conflict,
            "stale model recommendation cannot overwrite newer decision");
    using (var profiles = await client.GetAsync("/api/runtime-profiles"))
    using (var body = await Body(profiles))
    {
        Check(profiles.StatusCode == HttpStatusCode.OK, "profile registry GET");
        var json = body.RootElement;
        Check(json.GetProperty("schemaVersion").GetInt32() == 1,
            "profile registry schema is explicit");
        Check(!json.GetProperty("registryExecutionAvailable").GetBoolean(),
            "registry never grants executable capability");
        var descriptors = json.GetProperty("profiles").EnumerateArray().ToArray();
        Check(descriptors.Length == 2, "two scoped descriptors");
        var web = descriptors.Single(p => p.GetProperty("id").GetString() == "static-web");
        Check(web.GetProperty("qualificationStatus").GetString() == "UNQUALIFIED" &&
              !web.GetProperty("executableViaRegistry").GetBoolean() &&
              web.GetProperty("toolchainImageDigest").ValueKind == JsonValueKind.Null,
            "Static Web cannot claim an image or executable admission");
        var legacy = descriptors.Single(p =>
            p.GetProperty("id").GetString() == "legacy-dotnet-pilot");
        Check(legacy.GetProperty("qualificationStatus").GetString() ==
              "LEGACY_SCOPED_PILOT_ONLY" &&
              !legacy.GetProperty("executableViaRegistry").GetBoolean(),
            "legacy .NET pilot is not general-purpose .NET");
    }

    using (var missing = await client.GetAsync("/api/runtime-profiles/python"))
        Check(missing.StatusCode == HttpStatusCode.NotFound,
            "unsupported profile cannot be selected");
    using (var noMutation = await Post(client, "/api/runtime-profiles",
        new { id = "python", executableViaRegistry = true }))
        Check(noMutation.StatusCode == HttpStatusCode.MethodNotAllowed,
            "runtime profiles have no mutation API");

    var options = new[]
    {
        new ArchitectureOption("static-web", "Static browser app",
            "Offline operation with no build dependencies",
            "Manual module boundaries", "static-web"),
        new ArchitectureOption("legacy-dotnet", "Scoped C# pilot",
            "Existing isolated .NET test scenario",
            "Not a general product runtime", "legacy-dotnet-pilot")
    };
    var proposal = new ProposeArchitectureDecisionRequest(
        1, spec.Version, spec.Sha256, "technology-stack", "static-web",
        "Prefer offline JavaScript; owner may select an alternative.",
        options, 0);
    var path = $"/api/tasks/{task.Id}/architecture-decisions";

    using (var unsupported = await Post(client, path, proposal with
    {
        Options = new[]
        {
            options[0] with { RuntimeProfileId = "untrusted-container" },
            options[1]
        }
    }))
        Check(unsupported.StatusCode == HttpStatusCode.BadRequest,
            "unknown profile rejected before persistence");

    using (var invalidSchema = await Post(client, path,
        proposal with { SchemaVersion = 2 }))
        Check(invalidSchema.StatusCode == HttpStatusCode.BadRequest,
            "unknown schema version rejected");

    string hash;
    using (var proposed = await Post(client, path, proposal))
    using (var body = await Body(proposed))
    {
        Check(proposed.StatusCode == HttpStatusCode.Created,
            "versioned proposal accepted with approved specification");
        var json = body.RootElement;
        Check(json.GetProperty("status").GetString() == "Proposed" &&
              json.GetProperty("selectedOptionId").ValueKind == JsonValueKind.Null &&
              json.GetProperty("revision").GetInt32() == 1,
            "recommendation does not imply owner approval");
        hash = json.GetProperty("proposalSha256").GetString()!;
        Check(hash.Length == 64 && hash.All(Uri.IsHexDigit),
            "proposal has immutable SHA256 identity");
    }

    using (var repeated = await Post(client, path, proposal))
        Check(repeated.StatusCode == HttpStatusCode.Conflict,
            "concurrent or repeated obsolete revisions rejected");

    var reviewPath = path + "/technology-stack/review";
    var selection = new ReviewArchitectureDecisionRequest(
        1, 1, hash, "Approve", "legacy-dotnet", "APPROVE " + hash);
    using (var wrongHash = await Post(client, reviewPath,
        selection with { ProposalSha256 = new string('0', 64) }))
        Check(wrongHash.StatusCode == HttpStatusCode.Conflict,
            "changed proposal hash invalidates approval");

    using (var wrongConfirmation = await Post(client, reviewPath,
        selection with { Confirmation = "APPROVE unrelated" }))
        Check(wrongConfirmation.StatusCode == HttpStatusCode.Conflict,
            "owner must confirm exact proposal hash");

    using (var unknownChoice = await Post(client, reviewPath,
        selection with { SelectedOptionId = "arbitrary-runtime" }))
        Check(unknownChoice.StatusCode == HttpStatusCode.BadRequest,
            "owner cannot approve unproposed option");

    using (var accepted = await Post(client, reviewPath, selection))
    using (var body = await Body(accepted))
    {
        Check(accepted.StatusCode == HttpStatusCode.OK,
            "owner approves recommendation alternative explicitly");
        Check(body.RootElement.GetProperty("status").GetString() == "Approved" &&
              body.RootElement.GetProperty("selectedOptionId").GetString() ==
              "legacy-dotnet", "selection differs from model recommendation");
    }
    using (var replay = await Post(client, reviewPath, selection))
        Check(replay.StatusCode == HttpStatusCode.Conflict,
            "approval cannot be replayed or silently changed");

    using (var list = await client.GetAsync(path))
    using (var body = await Body(list))
    {
        var items = body.RootElement.GetProperty("decisions");
        var technology = items.EnumerateArray().Single(d =>
            d.GetProperty("topic").GetString() == "technology-stack");
        Check(items.GetArrayLength() == 2 &&
              technology.GetProperty("specificationCurrent").GetBoolean() &&
              technology.GetProperty("effectiveStatus").GetString() == "Approved",
            "current approved technology decision listed separately from architect proposal");
    }
    using (var status = await client.GetAsync("/api/status"))
    using (var body = await Body(status))
        Check(!body.RootElement.GetProperty("executionEnabled").GetBoolean(),
            "decision approval never starts or enables the M1 runner");

    // Recreate the entire HTTP host; proposals and old interviews remain.
    client.Dispose();
    client = null;
    await app.StopAsync();
    await app.DisposeAsync();
    app = null;

    app = ApiHost.Build([], settings, projectModelOverride: boundModel,
        catalogOverride: new Phase2Catalog(),
        architectureAdvisorOverride: new Phase2Advisor(),
        implementationPlannerOverride: leadModel,
        engineeringModelOverride: softwareModel,
        qaReviewerOverride: qaModel);
    client = await Start(app);
    using (var persisted = await client.GetAsync(path))
    using (var body = await Body(persisted))
    {
        var saved = body.RootElement.GetProperty("decisions")
            .EnumerateArray().Single(item =>
                item.GetProperty("topic").GetString() == "technology-stack");
        Check(saved.GetProperty("selectedOptionId").GetString() == "legacy-dotnet" &&
              saved.GetProperty("proposalSha256").GetString() == hash,
            "SQLite selection and proposal hash survive restart");
    }
    Check(interviews.Read(task.Id).Specification!.Status == "Approved",
        "original interview approval survives Phase 2 migration");

    var replacement = proposal with
    {
        ExpectedRevision = 1,
        RecommendedOptionId = "legacy-dotnet",
        Rationale = "Alternative proposed after owner changed priorities."
    };
    using (var replaced = await Post(client, path, replacement))
    using (var body = await Body(replaced))
        Check(replaced.StatusCode == HttpStatusCode.Created &&
              body.RootElement.GetProperty("revision").GetInt32() == 2 &&
              body.RootElement.GetProperty("status").GetString() == "Proposed",
            "new proposal supersedes previous approval");

    using (var history = await client.GetAsync(path + "/technology-stack/history"))
    using (var body = await Body(history))
    {
        var versions = body.RootElement.GetProperty("decisions");
        Check(versions.GetArrayLength() == 2 &&
              versions[0].GetProperty("status").GetString() == "Superseded" &&
              versions[1].GetProperty("status").GetString() == "Proposed",
            "immutable revisions retain audit history");
    }

    using (var staleReview = await Post(client, reviewPath, selection))
        Check(staleReview.StatusCode == HttpStatusCode.Conflict,
            "old proposal can never be reapproved");

    // Updated Product Owner conversation invalidates the exact old spec.
    var revisedInterview = interviews.CommitTurn(task.Id,
        new InterviewTurnRequest(second.LastSequence,
            "Change requirement: history may now be optional.", "qwen3.5:4b"),
        new InterviewReply("What is the minimum history behavior?", true));
    using (var stale = await client.GetAsync(path))
    using (var body = await Body(stale))
        Check(body.RootElement.GetProperty("decisions")[0]
            .GetProperty("effectiveStatus").GetString() == "STALE_SPECIFICATION",
            "revised interview invalidates effective architecture decisions");

    using (var forbidden = await Post(client, reviewPath,
        new ReviewArchitectureDecisionRequest(
            1, 2, hash, "Defer", null, "DEFER " + hash)))
        Check(forbidden.StatusCode == HttpStatusCode.Conflict,
            "stale revision cannot be reviewed");

    var newSpec = interviews.CommitSpecification(
        task.Id, revisedInterview.LastSequence,
        ("Revised calculator. History is optional. All controls must work " +
         "offline with mouse and keyboard. The owner still selects architecture. ")
        .PadRight(200, 'y')).Specification!;
    interviews.Approve(task.Id, new ApproveSpecificationRequest(
        newSpec.Version, newSpec.Sha256));
    using (var oldSpec = await Post(client, path, replacement with
    {
        ExpectedRevision = 2
    }))
        Check(oldSpec.StatusCode == HttpStatusCode.Conflict,
            "superseded spec hash cannot create new proposal");

    string deferredHash;
    using (var newProposal = await Post(client, path, proposal with
    {
        ExpectedRevision = 2,
        SpecificationVersion = newSpec.Version,
        SpecificationSha256 = newSpec.Sha256
    }))
    using (var body = await Body(newProposal))
    {
        Check(newProposal.StatusCode == HttpStatusCode.Created,
            "new approved specification permits revision 3");
        deferredHash = body.RootElement.GetProperty("proposalSha256").GetString()!;
    }
    using (var deferred = await Post(client, reviewPath,
        new ReviewArchitectureDecisionRequest(
            1, 3, deferredHash, "Defer", null, "DEFER " + deferredHash)))
    using (var body = await Body(deferred))
        Check(deferred.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("status").GetString() == "Deferred",
            "owner can explicitly defer a decision without selecting a profile");

    using (var noExec = await Post(client, "/api/tasks/" + task.Id + "/execute",
        new { }))
        Check(noExec.StatusCode == HttpStatusCode.NotFound,
            "Phase 2 adds no arbitrary execution endpoint");

    // Phase 2 source-only handoff is an independently scoped task. A new
    // Mission Control draft MUST bind both human-approved technical choices.
    var taskBound = tasks.CreateTask(new CreateTaskRequest(
        "Architecture bound browser app",
        "Create an offline static web timer with mouse and keyboard controls",
        Guid.NewGuid().ToString("N")));
    var initial = interviews.CommitTurn(taskBound.Id,
        new InterviewTurnRequest(0, null, "qwen3.5:4b"),
        new InterviewReply("What are the timer controls?", false));
    var answered = interviews.CommitTurn(taskBound.Id,
        new InterviewTurnRequest(initial.LastSequence,
            "Start, stop and reset. No server or packages.", "qwen3.5:4b"),
        new InterviewReply("Should the timer work without internet?", true));
    var draftBound = interviews.CommitSpecification(taskBound.Id,
        answered.LastSequence,
        ("Create an offline browser timer. It must support a start button, " +
         "pause and reset using mouse and keyboard; no server or packages. " +
         "Use approved technical choices and save files as an untested draft. ")
        .PadRight(210, 'z'));
    var approvedBound = interviews.Approve(taskBound.Id,
        new ApproveSpecificationRequest(
            draftBound.Specification!.Version, draftBound.Specification.Sha256));
    var specBound = approvedBound.Specification!;
    var boundPath = "/api/tasks/" + taskBound.Id + "/draft-build";
    var architecturePath = boundPath + "/architecture";
    var approvedRequest = new {
        specificationVersion = specBound.Version,
        specificationSha256 = specBound.Sha256,
        architectureSha256 = ""
    };
    using (var noChoices = await client.GetAsync(architecturePath))
    using (var body = await Body(noChoices))
        Check(body.RootElement.GetProperty("status").GetString() == "LEGACY_UNBOUND" &&
              !body.RootElement.GetProperty("readyForSourceGeneration").GetBoolean(),
            "new Mission Control drafts explicitly reject legacy spec-only fallback");

    var decisionStore = new ArchitectureDecisionStore(tasks);
    var stack = decisionStore.Propose(taskBound.Id,
        new ProposeArchitectureDecisionRequest(1, specBound.Version,
            specBound.Sha256, "technology-stack", "static",
            "Choose offline files with no package manager.", [
                new("static", "Static HTML and JavaScript",
                    "Offline, no installation", "Manual DOM interaction", "static-web"),
                new("react", "React and TypeScript",
                    "Reusable UI", "Requires a build system", null)
            ], 0));
    using (var partial = await client.GetAsync(architecturePath))
    using (var body = await Body(partial))
        Check(body.RootElement.GetProperty("status").GetString() == "INCOMPLETE" &&
              !body.RootElement.GetProperty("readyForSourceGeneration").GetBoolean(),
            "one proposed stack is not enough for drafting");
    using (var attempt = await Post(client, boundPath, approvedRequest))
        Check(attempt.StatusCode == HttpStatusCode.Conflict,
            "partially approved architecture is blocked by the server");
    decisionStore.Review(taskBound.Id, "technology-stack",
        new ReviewArchitectureDecisionRequest(
            1, 1, stack.ProposalSha256, "Approve", "static",
            "APPROVE " + stack.ProposalSha256));
    var style = decisionStore.Propose(taskBound.Id,
        new ProposeArchitectureDecisionRequest(1, specBound.Version,
            specBound.Sha256, "architecture-style", "modular",
            "Simple component boundaries for a small timer.", [
                new("modular", "Small event modules",
                    "Low complexity", "Manual module boundaries", null),
                new("layers", "Full layered architecture",
                    "Explicit dependencies", "Extra boilerplate", null)
            ], 0));
    decisionStore.Review(taskBound.Id, "architecture-style",
        new ReviewArchitectureDecisionRequest(
            1, 1, style.ProposalSha256, "Approve", "modular",
            "APPROVE " + style.ProposalSha256));
    string architectureSha;
    using (var ready = await client.GetAsync(architecturePath))
    using (var body = await Body(ready))
    {
        var value = body.RootElement;
        architectureSha = value.GetProperty("architectureSha256").GetString()!;
        Check(value.GetProperty("status").GetString() == "READY" &&
              value.GetProperty("readyForSourceGeneration").GetBoolean() &&
              architectureSha.Length == 64 &&
              value.GetProperty("approvedChoices").GetArrayLength() == 2,
            "two human-approved technical choices produce stable fingerprint");
    }
    // Real HTTP/SQLite Technical Lead contracts: every proposal consumes
    // *both* saved owner-approved architecture choices.
    var leadPath = "/api/tasks/" + taskBound.Id + "/implementation-plan";
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var denied = await anonymous.GetAsync(leadPath))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized,
            "Technical Lead plans are owner-private");
    using (var empty = await client.GetAsync(leadPath))
    using (var body = await Body(empty))
        Check(empty.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("plan").ValueKind == JsonValueKind.Null,
            "Technical Lead does not auto-generate or auto-approve");

    var leadRequest = new GenerateImplementationPlanRequest(
        1, specBound.Version, specBound.Sha256, architectureSha, 0);
    using (var wrong = await Post(client, leadPath + "/generate",
        leadRequest with { ArchitectureSha256 = new string('0', 64) }))
        Check(wrong.StatusCode == HttpStatusCode.Conflict &&
              leadModel.Attempts == 0,
            "wrong architecture hash rejected before agent invocation");

    string firstPlanHash;
    using (var generated = await Post(client, leadPath + "/generate", leadRequest))
    using (var body = await Body(generated))
    {
        var value = body.RootElement;
        firstPlanHash = value.GetProperty("planSha256").GetString()!;
        Check(generated.StatusCode == HttpStatusCode.Created &&
              value.GetProperty("status").GetString() == "Proposed" &&
              value.GetProperty("revision").GetInt32() == 1 &&
              value.GetProperty("current").GetBoolean() &&
              value.GetProperty("outline").GetProperty("tasks").GetArrayLength() == 2 &&
              firstPlanHash.Length == 64 && leadModel.Attempts == 1,
            "Technical Lead saves only bounded, versioned proposal");
    }
    using (var replay = await Post(client, leadPath + "/generate", leadRequest))
        Check(replay.StatusCode == HttpStatusCode.Conflict &&
              leadModel.Attempts == 1,
            "stale revision rejected before a second model request");

    var approveLead = new ApproveImplementationPlanRequest(
        1, 1, firstPlanHash, "APPROVE " + firstPlanHash);
    using (var wrongHash = await Post(client, leadPath + "/approve",
        approveLead with { PlanSha256 = new string('f', 64),
            Confirmation = "APPROVE " + new string('f', 64) }))
        Check(wrongHash.StatusCode == HttpStatusCode.Conflict,
            "owner cannot approve a substituted plan digest");
    using (var wrongConsent = await Post(client, leadPath + "/approve",
        approveLead with { Confirmation = "APPROVE different" }))
        Check(wrongConsent.StatusCode == HttpStatusCode.BadRequest,
            "approval requires exact owner-confirmed plan hash");

    using (var approvedPlan = await Post(client, leadPath + "/approve", approveLead))
    using (var body = await Body(approvedPlan))
        Check(approvedPlan.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("status").GetString() == "Approved" &&
              body.RootElement.GetProperty("planSha256").GetString() == firstPlanHash,
            "exact owner review saves plan approval");
    using (var replay = await Post(client, leadPath + "/approve", approveLead))
        Check(replay.StatusCode == HttpStatusCode.Conflict,
            "plan approval cannot be replayed");

    using (var eventResponse = await client.GetAsync(
        "/api/tasks/" + taskBound.Id + "/agent-handoffs"))
    using (var body = await Body(eventResponse))
    {
        var events = body.RootElement.GetProperty("events");
        Check(eventResponse.StatusCode == HttpStatusCode.OK &&
              events.EnumerateArray().Any(e =>
                  e.GetProperty("kind").GetString() == "ArchitectureHandoff" &&
                  e.GetProperty("toAgent").GetString() == "TechnicalLead") &&
              events.EnumerateArray().Any(e =>
                  e.GetProperty("kind").GetString() == "PlanApproved" &&
                  e.GetProperty("planSha256").GetString() == firstPlanHash &&
                  e.GetProperty("current").GetBoolean()) &&
              !body.RootElement.GetProperty("executionAvailable").GetBoolean(),
            "MAF Technical Lead handoffs and owner approval are durable and nonexecuting");
    }

    string secondPlanHash;
    using (var updated = await Post(client, leadPath + "/generate",
        leadRequest with { ExpectedRevision = 1 }))
    using (var body = await Body(updated))
    {
        secondPlanHash = body.RootElement.GetProperty("planSha256").GetString()!;
        Check(updated.StatusCode == HttpStatusCode.Created &&
              secondPlanHash != firstPlanHash &&
              body.RootElement.GetProperty("status").GetString() == "Proposed" &&
              leadModel.Attempts == 2,
            "replanning replaces previous approval with fresh unapproved version");
    }
    using (var historyResponse = await client.GetAsync(leadPath))
    using (var body = await Body(historyResponse))
    {
        var history = body.RootElement.GetProperty("history");
        Check(history.GetArrayLength() == 2 &&
              history[0].GetProperty("status").GetString() == "Superseded" &&
              history[1].GetProperty("status").GetString() == "Proposed",
            "superseded Technical Lead plan is still visible in history");
    }
    using (var oldApproval = await Post(client, leadPath + "/approve", approveLead))
        Check(oldApproval.StatusCode == HttpStatusCode.Conflict,
            "old approved revision cannot silently regain authority");
    using (var revisedApproval = await Post(client, leadPath + "/approve",
        new ApproveImplementationPlanRequest(1, 2, secondPlanHash,
            "APPROVE " + secondPlanHash)))
        Check(revisedApproval.StatusCode == HttpStatusCode.OK,
            "revised plan requires independent human approval");
    Check(new ImplementationPlanStore(tasks, interviews).Read(taskBound.Id)
        .Plan?.PlanSha256 == secondPlanHash,
        "restarted SQLite reader sees exact approved plan identity");

    // Phase 4: the fourth actual MAF role is exposed by the authenticated
    // roster, while engineer task execution is only a SOURCE-ONLY model call.
    using (var roster = await client.GetAsync("/api/agents/roster"))
    using (var body = await Body(roster))
    {
        var agents = body.RootElement.GetProperty("agents");
        Check(roster.StatusCode == HttpStatusCode.OK &&
              agents.GetArrayLength() == 5 &&
              agents.EnumerateArray().Any(a =>
                  a.GetProperty("id").GetString() == "SoftwareEngineer" &&
                  !a.GetProperty("canExecuteCode").GetBoolean() &&
                  a.GetProperty("backstory").GetString()!.Length > 25),
            "authenticated roster exposes distinct fourth MAF role and no tool rights");
    }
    var engineeringPath = "/api/tasks/" + taskBound.Id + "/engineering-runs";
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var denied = await anonymous.GetAsync(engineeringPath + "/latest"))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized,
            "owner-private engineering task ledger requires authentication");

    using (var emptyEngineering = await client.GetAsync(engineeringPath + "/latest"))
    using (var body = await Body(emptyEngineering))
        Check(emptyEngineering.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("snapshot").ValueKind == JsonValueKind.Null,
            "no engineering activity is faked before owner opts in");

    using (var denied = await Post(client, engineeringPath,
        new StartEngineeringRunRequest(2, firstPlanHash)))
        Check(denied.StatusCode == HttpStatusCode.Conflict &&
              softwareModel.AssignedIds.Count == 0,
            "wrong Technical Lead revision digest cannot start engineering");

    string engineeringRunId;
    using (var admitted = await Post(client, engineeringPath,
        new StartEngineeringRunRequest(2, secondPlanHash)))
    using (var body = await Body(admitted))
    {
        engineeringRunId = body.RootElement.GetProperty("id").GetString()!;
        Check(admitted.StatusCode == HttpStatusCode.Accepted &&
              body.RootElement.GetProperty("planSha256").GetString() == secondPlanHash &&
              body.RootElement.GetProperty("state").GetString() == "Running",
            "owner starts task-by-task engineer run bound to exact approved plan");
    }
    EngineeringRunSnapshot? engineeringResult = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        using var progress = await client.GetAsync(
            engineeringPath + "/" + engineeringRunId);
        using var document = await Body(progress);
        if (document.RootElement.GetProperty("job").GetProperty("state")
            .GetString() == "Running")
        {
            await Task.Delay(25);
            continue;
        }
        engineeringResult = new EngineeringRunStore(tasks,
            new ImplementationPlanStore(tasks, interviews))
            .Get(taskBound.Id, engineeringRunId);
        break;
    }
    Check(engineeringResult?.Job.State == "Generated" &&
          engineeringResult.Tasks.Count == 2 &&
          engineeringResult.Tasks.All(t => t.State == "Completed") &&
          engineeringResult.Files.Count == 2 &&
          engineeringResult.Files.Any(f => f.Path == "index.html") &&
          engineeringResult.Files.Any(f => f.Path == "app.js") &&
          engineeringResult.Events.Any(e => e.Kind == "EngineeringTaskStarted") &&
          engineeringResult.Events.Count(e => e.Kind == "EngineeringTaskCompleted") == 2,
        "each approved engineer task persists source and exact durable event history");
    Check(softwareModel.AssignedIds.SequenceEqual(["timer-interface","timer-state"]) &&
          softwareModel.PreviousFileCountOnSecondTask == 1 &&
          engineeringResult!.Files.Single(f => f.Path == "app.js")
              .LastWorkItemId == "timer-state",
        "Software Engineer gets prior source and records last task ownership");

    using (var zip = await client.GetAsync(
        engineeringPath + "/" + engineeringRunId + "/zip"))
    {
        var bytes = await zip.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Check(zip.StatusCode == HttpStatusCode.OK &&
              archive.Entries.Select(e => e.FullName)
                  .Order(StringComparer.Ordinal).SequenceEqual(["app.js","index.html"]),
            "owner can export all completed engineer source without executing code");
    }
    using (var handoffs = await client.GetAsync(
        "/api/tasks/" + taskBound.Id + "/agent-handoffs"))
    using (var body = await Body(handoffs))
        Check(body.RootElement.GetProperty("events").EnumerateArray()
              .Any(e => e.GetProperty("kind").GetString() ==
                  "EngineeringTaskCompleted" &&
                  e.GetProperty("fromAgent").GetString() == "SoftwareEngineer" &&
                  e.GetProperty("planSha256").GetString() == secondPlanHash),
            "authentic MAF Software Engineer handoffs reference exact approved plan");

    // Phase 5: independent QA must not run on unfinished or stale
    // engineering source. Human initiation is explicit; no hidden auto-QA.
    var qaPath = "/api/tasks/" + taskBound.Id + "/qa-reviews";
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var denied = await anonymous.GetAsync(qaPath + "/latest"))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized,
            "independent QA reports remain owner-private");
    using (var absent = await client.GetAsync(qaPath + "/latest"))
    using (var body = await Body(absent))
        Check(absent.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("snapshot").ValueKind == JsonValueKind.Null,
            "QA has not fabricated any report before owner requests it");

    var reviewStore = new QaReviewStore(tasks,
        new ImplementationPlanStore(tasks, interviews));
    var engineeringStore = new EngineeringRunStore(tasks,
        new ImplementationPlanStore(tasks, interviews));
    var unfinished = engineeringStore.Start(taskBound.Id,
        new ImplementationPlanStore(tasks, interviews).Read(taskBound.Id).Plan!);
    using (var forbidden = await Post(client, qaPath,
        new StartQaReviewRequest(unfinished.Id, secondPlanHash)))
        Check(forbidden.StatusCode == HttpStatusCode.Conflict &&
              qaModel.ReviewedFiles.Count == 0,
            "QA refuses unfinished engineering source before any model call");
    engineeringStore.Interrupt(taskBound.Id, unfinished.Id);

    using (var wrong = await Post(client, qaPath,
        new StartQaReviewRequest(engineeringRunId, firstPlanHash)))
        Check(wrong.StatusCode == HttpStatusCode.Conflict &&
              qaModel.ReviewedFiles.Count == 0,
            "QA refuses obsolete Technical Lead plan identity");
    string qaReviewId;
    using (var admitted = await Post(client, qaPath,
        new StartQaReviewRequest(engineeringRunId, secondPlanHash)))
    using (var body = await Body(admitted))
    {
        qaReviewId = body.RootElement.GetProperty("id").GetString()!;
        Check(admitted.StatusCode == HttpStatusCode.Accepted &&
              body.RootElement.GetProperty("engineeringRunId").GetString() ==
                  engineeringRunId &&
              body.RootElement.GetProperty("sourceSha256").GetString()!.Length == 64 &&
              body.RootElement.GetProperty("state").GetString() == "Running",
            "QA admission captures exact completed source fingerprint and approved plan");
    }
    QaReviewSnapshot? qaResult = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        using var progress = await client.GetAsync(qaPath + "/" + qaReviewId);
        using var document = await Body(progress);
        if (document.RootElement.GetProperty("job").GetProperty("state")
                .GetString() == "Running")
        {
            await Task.Delay(25);
            continue;
        }
        qaResult = reviewStore.Get(taskBound.Id, qaReviewId);
        break;
    }
    Check(qaResult?.Job.State == "Reviewed" &&
          qaResult.Reports.Count == 2 &&
          qaResult.Reports.Sum(report =>
              report.Assessment.Findings.Count) == 1 &&
          qaResult.Reports.Single(report => report.Path == "app.js")
              .Assessment.Findings.Single().Severity == "Major" &&
          qaResult.Events.Count(e => e.Kind == "QaFileReviewed") == 2 &&
          qaResult.Events.Any(e => e.Kind == "QaReviewCompleted"),
        "independent QA persists two real static file reports with potential findings");
    Check(qaModel.ReviewedFiles.Order(StringComparer.Ordinal)
              .SequenceEqual(["app.js", "index.html"]) &&
          qaResult!.Job.SourceSha256 ==
              QaReviewStore.Fingerprint(engineeringResult!.Files) &&
          reviewStore.Latest(taskBound.Id)?.Job.Id == qaReviewId,
        "static QA received each exact source file and reports survive SQLite reload");
    using (var savedHandoffs = await client.GetAsync(
        "/api/tasks/" + taskBound.Id + "/agent-handoffs"))
    using (var body = await Body(savedHandoffs))
        Check(body.RootElement.GetProperty("events").EnumerateArray()
            .Any(e => e.GetProperty("kind").GetString() == "QaStaticReportSaved" &&
                      e.GetProperty("fromAgent").GetString() == "QaEngineer" &&
                      e.GetProperty("planSha256").GetString() == secondPlanHash),
            "QA handoff event records a report, not a fabricated test PASS");
    using (var cross = await client.GetAsync(
        "/api/tasks/" + task.Id + "/qa-reviews/" + qaReviewId))
        Check(cross.StatusCode == HttpStatusCode.NotFound,
            "QA report cannot be read through another project's URL");

    using (var noHash = await Post(client, boundPath, approvedRequest))
        Check(noHash.StatusCode == HttpStatusCode.Conflict,
            "approved architecture cannot be silently ignored by old API callers");
    using (var substitutedHash = await Post(client, boundPath,
        new {
            specificationVersion = specBound.Version,
            specificationSha256 = specBound.Sha256,
            architectureSha256 = new string('f', 64)
        }))
        Check(substitutedHash.StatusCode == HttpStatusCode.Conflict,
            "draft admission refuses substituted architecture fingerprint");
    string boundDraftId;
    using (var accepted = await Post(client, boundPath,
        new {
            specificationVersion = specBound.Version,
            specificationSha256 = specBound.Sha256,
            architectureSha256 = architectureSha
        }))
    using (var body = await Body(accepted))
    {
        Check(accepted.StatusCode == HttpStatusCode.Accepted,
            "architecture-bound source job admitted on owner action");
        boundDraftId = body.RootElement.GetProperty("id").GetString()!;
    }
    ProjectDraftSnapshot? boundResult = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        boundResult = new ProjectDraftStore(tasks).Get(taskBound.Id, boundDraftId);
        if (boundResult.Job.State != "Running") break;
        await Task.Delay(25);
    }
    Check(boundResult is not null && boundResult.Job.State == "Generated",
        "source draft completed without running code");
    Check(boundResult!.Architecture?.Sha256 == architectureSha &&
          boundResult.Architecture.Choices.Count == 2 &&
          boundResult.Events.Any(e => e.Kind == "ArchitectureBound"),
        "source job persists immutable architecture options, hash and event");
    Check(boundModel.PlanInput.Contains("Static HTML and JavaScript") &&
          boundModel.PlanInput.Contains("Small event modules") &&
          boundModel.WriteInput.Contains("Static HTML and JavaScript") &&
          boundModel.WriteInput.Contains(architectureSha),
        "both plan and write model prompts consume exact owner selections");
    using (var archive = await client.GetAsync(
        boundPath + "/" + boundDraftId + "/zip"))
        Check(archive.StatusCode == HttpStatusCode.OK,
            "architecture-bound ZIP remains source-only downloadable output");

    // A small local model may ignore a selected stack on the first plan.
    // The bounded correction attempt must preserve the exact architecture
    // and generate only files allowed by that policy.
    boundModel.ReturnWrongStack = true;
    boundModel.RecoverOnCorrection = true;
    var previousPlans = boundModel.PlanAttempts;
    string correctedDraftId;
    using (var admitted = await Post(client, boundPath, new {
        specificationVersion = specBound.Version,
        specificationSha256 = specBound.Sha256,
        architectureSha256 = architectureSha
    }))
    using (var body = await Body(admitted))
    {
        Check(admitted.StatusCode == HttpStatusCode.Accepted,
            "architecture-bound plan correction is owner-initiated");
        correctedDraftId = body.RootElement.GetProperty("id").GetString()!;
    }
    ProjectDraftSnapshot? corrected = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        corrected = new ProjectDraftStore(tasks).Get(taskBound.Id, correctedDraftId);
        if (corrected.Job.State != "Running") break;
        await Task.Delay(25);
    }
    Check(corrected?.Job.State == "Generated" &&
          corrected.Files.Any(f => f.Path == "index.html") &&
          corrected.Files.All(f => f.Path.EndsWith(".html") ||
              f.Path.EndsWith(".js")),
        "one corrected plan becomes valid source-only files");
    Check(boundModel.PlanAttempts == previousPlans + 2 &&
          boundModel.PlanInput.Contains("FILE PLAN CORRECTION REQUIRED:") &&
          corrected!.Events.Count(e => e.Kind == "PlanCorrectionRequested") == 1,
        "exactly one documented correction attempt without changing approvals");
    Check(corrected?.Architecture?.Sha256 == architectureSha,
        "correction keeps original approved architecture fingerprint");

    // An otherwise well-formed static plan missing index.html is also
    // corrected, rather than rejecting every recoverable small-model output.
    boundModel.ReturnWrongStack = false;
    boundModel.OmitIndex = true;
    previousPlans = boundModel.PlanAttempts;
    string missingIndexId;
    using (var admitted = await Post(client, boundPath, new {
        specificationVersion = specBound.Version,
        specificationSha256 = specBound.Sha256,
        architectureSha256 = architectureSha
    }))
    using (var body = await Body(admitted))
        missingIndexId = body.RootElement.GetProperty("id").GetString()!;
    ProjectDraftSnapshot? recoveredIndex = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        recoveredIndex = new ProjectDraftStore(tasks).Get(taskBound.Id, missingIndexId);
        if (recoveredIndex.Job.State != "Running") break;
        await Task.Delay(25);
    }
    Check(recoveredIndex?.Job.State == "Generated" &&
          recoveredIndex.Files.Any(f => f.Path == "index.html") &&
          boundModel.PlanAttempts == previousPlans + 2,
        "missing index.html is recovered with one validated re-plan");

    // Independent static profile policy still rejects hallucinated C#
    // when both attempts violate the owner-approved browser-only stack.
    boundModel.OmitIndex = false;
    boundModel.ReturnWrongStack = true;
    boundModel.RecoverOnCorrection = false;
    previousPlans = boundModel.PlanAttempts;
    string rejectedId;
    using (var badBuild = await Post(client, boundPath, new {
        specificationVersion = specBound.Version,
        specificationSha256 = specBound.Sha256,
        architectureSha256 = architectureSha
    }))
    using (var body = await Body(badBuild))
        rejectedId = body.RootElement.GetProperty("id").GetString()!;
    ProjectDraftSnapshot? rejected = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        rejected = new ProjectDraftStore(tasks).Get(taskBound.Id, rejectedId);
        if (rejected.Job.State != "Running") break;
        await Task.Delay(25);
    }
    Check(rejected?.Job.State == "Failed" &&
          rejected.Job.ErrorCode == "ARCHITECTURE_PLAN_CONFLICT" &&
          rejected.Files.Count == 0 &&
          boundModel.PlanAttempts == previousPlans + 2 &&
          rejected.Events.Count(e => e.Kind == "PlanCorrectionRequested") == 1,
        "two invalid plans fail closed with no files and no further retries");

    // Regression for INVALID_PROJECT_PLAN after an approved Technical Lead
    // succeeds: malformed source-manifest JSON is NOT an engineering task plan.
    // A single local re-plan must recover, without weakening validation.
    async Task<ProjectDraftSnapshot> RunFileManifestCaseAsync()
    {
        using var admitted = await Post(client!, boundPath, new {
            specificationVersion = specBound.Version,
            specificationSha256 = specBound.Sha256,
            architectureSha256 = architectureSha
        });
        using var response = await Body(admitted);
        Check(admitted.StatusCode == HttpStatusCode.Accepted,
            "owner explicitly initiates a source-file manifest job");
        var id = response.RootElement.GetProperty("id").GetString()!;
        for (var attempt = 0; attempt < 160; attempt++)
        {
            var snapshot = new ProjectDraftStore(tasks).Get(taskBound.Id, id);
            if (snapshot.Job.State != "Running") return snapshot;
            await Task.Delay(25);
        }
        throw new Exception("Source-manifest case did not reach a terminal state");
    }

    boundModel.ReturnWrongStack = false;
    boundModel.RecoverOnCorrection = false;
    boundModel.InvalidMetadata = true;
    boundModel.RecoverInvalidManifest = true;
    previousPlans = boundModel.PlanAttempts;
    var repairedMetadata = await RunFileManifestCaseAsync();
    Check(repairedMetadata.Job.State == "Generated" &&
          repairedMetadata.Files.Any(file => file.Path == "index.html") &&
          repairedMetadata.Events.Count(e => e.Kind == "PlanCorrectionRequested") == 1 &&
          repairedMetadata.Events.Any(e => e.Kind == "PlanCorrectionRequested" &&
              e.Detail.Contains("INVALID_PROJECT_PLAN")) &&
          boundModel.PlanAttempts == previousPlans + 2,
        "invalid file purpose recovers with exactly one safe re-plan");

    boundModel.InvalidMetadata = false;
    boundModel.MalformedManifest = true;
    previousPlans = boundModel.PlanAttempts;
    var repairedJson = await RunFileManifestCaseAsync();
    Check(repairedJson.Job.State == "Generated" &&
          repairedJson.Files.Count == 2 &&
          boundModel.PlanAttempts == previousPlans + 2 &&
          repairedJson.Events.Count(e => e.Kind == "PlanCorrectionRequested") == 1,
        "unusable model JSON file manifest receives one corrective retry");

    boundModel.MalformedManifest = false;
    boundModel.InvalidMetadata = true;
    boundModel.RecoverInvalidManifest = false;
    previousPlans = boundModel.PlanAttempts;
    var rejectedMetadata = await RunFileManifestCaseAsync();
    Check(rejectedMetadata.Job.State == "Failed" &&
          rejectedMetadata.Job.ErrorCode == "INVALID_PROJECT_PLAN" &&
          rejectedMetadata.Files.Count == 0 &&
          boundModel.PlanAttempts == previousPlans + 2 &&
          rejectedMetadata.Events.Count(e => e.Kind == "PlanCorrectionRequested") == 1,
        "two malformed manifests fail closed without any source file saved");

    boundModel.InvalidMetadata = false;
    boundModel.InvalidPath = true;
    previousPlans = boundModel.PlanAttempts;
    var rejectedPath = await RunFileManifestCaseAsync();
    Check(rejectedPath.Job.State == "Failed" &&
          rejectedPath.Job.ErrorCode == "INVALID_PROJECT_PATH" &&
          rejectedPath.Files.Count == 0 &&
          boundModel.PlanAttempts == previousPlans + 2,
        "unsafe paths remain forbidden after the single correction attempt");
    boundModel.InvalidPath = false;

    // Changing a decision after job admission invalidates file writes and
    // terminal success *inside the same database transaction*.
    var boundStore = new ProjectDraftStore(tasks);
    var changedJob = boundStore.Start(taskBound.Id,
        specBound.Version, specBound.Sha256, specBound.Model, architectureSha);
    _ = decisionStore.Propose(taskBound.Id, new ProposeArchitectureDecisionRequest(
        1, specBound.Version, specBound.Sha256, "technology-stack", "static",
        "Revisit stack after the initial approval.", [
            new("static", "Static HTML and JavaScript",
                "Offline", "Manual DOM", "static-web"),
            new("react", "React and TypeScript",
                "Components", "Build tooling", null)
        ], 1));
    try
    {
        boundStore.SaveFile(taskBound.Id, changedJob.Id,
            new PlannedProjectFile("index.html", "Offline timer entry point"),
            "<html><body>Timer</body></html>");
        Check(false, "stale architecture must not save output");
    }
    catch (StoreFailure error) when (error.Code == "ARCHITECTURE_CONFLICT")
    {
        Check(true, "decision change invalidates transactional file persistence");
    }
    boundStore.Finish(taskBound.Id, changedJob.Id, true, null);
    Check(boundStore.Get(taskBound.Id, changedJob.Id).Job.State == "Failed" &&
          boundStore.Get(taskBound.Id, changedJob.Id).Job.ErrorCode ==
            "ARCHITECTURE_CONFLICT",
        "stale decision cannot be reported as generated successfully");
    using (var unready = await client.GetAsync(architecturePath))
    using (var body = await Body(unready))
        Check(body.RootElement.GetProperty("status").GetString() == "INCOMPLETE",
            "revised architecture requires a fresh explicit owner approval");
    using (var invalidated = await client.GetAsync(leadPath))
    using (var body = await Body(invalidated))
        Check(!body.RootElement.GetProperty("plan").GetProperty("current").GetBoolean() &&
              body.RootElement.GetProperty("plan")
                .GetProperty("effectiveStatus").GetString() == "STALE_APPROVALS",
            "architecture revision invalidates existing Technical Lead plan");
    using (var blocked = await Post(client, leadPath + "/generate",
        leadRequest with { ExpectedRevision = 2 }))
        Check(blocked.StatusCode == HttpStatusCode.Conflict &&
              leadModel.Attempts == 2,
            "stale architecture never reaches the planning agent");
    using (var blockedQa = await Post(client, qaPath,
        new StartQaReviewRequest(engineeringRunId, secondPlanHash)))
        Check(blockedQa.StatusCode == HttpStatusCode.Conflict &&
              qaModel.ReviewedFiles.Count == 2,
            "stale architecture cannot initiate new QA review of old source");

    Console.WriteLine($"PHASE 2 MODEL-FREE ACCEPTANCE: PASS {checks} checks");
}
finally
{
    client?.Dispose();
    if (app is not null)
    {
        await app.StopAsync();
        await app.DisposeAsync();
    }
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { /* Windows may release SQLite locks after process exit. */ }
}


sealed class Phase2Catalog : IOllamaModelCatalog
{
    public Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellation) =>
        Task.FromResult<IReadOnlyList<string>>(new[] { "qwen3.5:4b" });
}
sealed class Phase2Advisor : IArchitectureAdvisor
{
    public Task<ArchitectureRecommendation> RecommendAsync(
        string objective, string approvedSpecification, string model,
        string topic, CancellationToken cancellation)
    {
        if (model != "qwen3.5:4b" || topic != "architecture-style" ||
            !objective.Contains("calculator", StringComparison.OrdinalIgnoreCase) ||
            !approvedSpecification.Contains("offline", StringComparison.OrdinalIgnoreCase))
            throw new Exception("Advisor received wrong approved task/model context");
        return Task.FromResult(new ArchitectureRecommendation(
            "minimal-modules", "Keep offline calculation behavior straightforward.",
            new ArchitectureOption[] {
                new("minimal-modules", "Small modules",
                    "Direct and testable", "More manual boundaries", null),
                new("clean-layers", "Layered structure",
                    "Stricter dependencies", "More files and boilerplate", null)
            }));
    }
}


sealed class Phase2DraftModel : IProjectDraftModel
{
    public bool ReturnWrongStack { get; set; }
    public bool OmitIndex { get; set; }
    public bool RecoverOnCorrection { get; set; }
    public bool InvalidMetadata { get; set; }
    public bool InvalidPath { get; set; }
    public bool MalformedManifest { get; set; }
    public bool RecoverInvalidManifest { get; set; }
    public int PlanAttempts { get; private set; }
    public string PlanInput { get; private set; } = "";
    public string WriteInput { get; private set; } = "";

    public Task<IReadOnlyList<PlannedProjectFile>> PlanAsync(
        string objective, string approvedSpecification, string model,
        CancellationToken cancellation)
    {
        PlanInput = approvedSpecification;
        PlanAttempts++;
        var corrective = approvedSpecification.Contains(
            "FILE PLAN CORRECTION REQUIRED:", StringComparison.Ordinal);
        if (!corrective || !RecoverInvalidManifest)
        {
            if (MalformedManifest)
                throw new StoreFailure("INVALID_PROJECT_PLAN");
            if (InvalidMetadata)
                return Task.FromResult<IReadOnlyList<PlannedProjectFile>>(
                    [new PlannedProjectFile("index.html", "x")]);
            if (InvalidPath)
                return Task.FromResult<IReadOnlyList<PlannedProjectFile>>(
                    [new PlannedProjectFile("../outside.js", "Unsafe source path")]);
        }
        if (!corrective || !RecoverOnCorrection)
        {
            if (ReturnWrongStack)
                return Task.FromResult<IReadOnlyList<PlannedProjectFile>>(
                    [new PlannedProjectFile("Program.cs", "Unexpected .NET project")]);
            if (OmitIndex)
                return Task.FromResult<IReadOnlyList<PlannedProjectFile>>(
                    [new PlannedProjectFile("app.js", "Missing browser entry point")]);
        }
        return Task.FromResult<IReadOnlyList<PlannedProjectFile>>(
            [new PlannedProjectFile("index.html", "Offline timer interface"),
             new PlannedProjectFile("app.js", "Keyboard and mouse handlers")]);
    }

    public Task<string> WriteAsync(string objective, string approvedSpecification,
        string model, PlannedProjectFile file, IReadOnlyList<ProjectDraftFile> previous,
        CancellationToken cancellation)
    {
        WriteInput = approvedSpecification;
        return Task.FromResult(file.Path == "index.html"
            ? "<!DOCTYPE html><html><body><button id=\"start\">Start</button><script src=\"app.js\"></script></body></html>"
            : "document.getElementById('start').addEventListener('click', () => {});");
    }
}

sealed class Phase3FakeMafFactory : IMafChatClientFactory
{
    public List<MafAgentPurpose> Purposes { get; } = [];
    public List<string> Models { get; } = [];
    public int SystemPoliciesSeen { get; private set; }
    public int AllowedToolsSeen { get; private set; }

    public IChatClient Create(string model, MafAgentPurpose purpose)
    {
        Purposes.Add(purpose);
        Models.Add(model);
        return new FakeChatClient(this, purpose);
    }

    private sealed class FakeChatClient(
        Phase3FakeMafFactory parent, MafAgentPurpose purpose) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var history = messages.ToArray();
            if (history.Any(m => m.Role.Value == "system" &&
                m.Text?.Contains("You are", StringComparison.Ordinal) == true) ||
                options?.Instructions?.Contains("You are",
                    StringComparison.Ordinal) == true)
                parent.SystemPoliciesSeen++;
            parent.AllowedToolsSeen += options?.Tools?.Count ?? 0;
            var text = purpose switch
            {
                MafAgentPurpose.Interview =>
                    """{"message":"Who are your intended users?","readyForSpecification":false}""",
                MafAgentPurpose.Specification =>
                    ("# Project goal\nAn offline personal notes tracker. " +
                    "## Core workflow\nOne owner saves notes and searches them " +
                    "locally without cloud accounts. " +
                    "## Testable acceptance criteria\nGiven an offline browser, " +
                    "when the user creates a note, the note persists after restart. " +
                    "Given a search phrase, the results contain matching notes. " +
                    "## Constraints\nOnly local data, no remote dependencies."),
                MafAgentPurpose.Architecture =>
                    """{"recommendedOptionId":"static-web","rationale":"Offline browser storage is proportionate.","options":[{"id":"static-web","label":"Static HTML and JavaScript","benefit":"Works offline","tradeoff":"Manual DOM","runtimeProfileId":"static-web"},{"id":"react","label":"React and TypeScript","benefit":"Components","tradeoff":"Build tools","runtimeProfileId":null}]}""",
                MafAgentPurpose.TechnicalLead =>
                    """{"summary":"Build a minimal offline notes application with working controls and persistence.","tasks":[{"id":"create-ui","title":"Create the browser interface","description":"Create accessible controls for notes and search","dependsOn":[],"acceptanceCriteria":["A user can type into the note editor and click Save"]},{"id":"persist-notes","title":"Save notes and wire search","description":"Connect controls to browser local storage and implement search behavior","dependsOn":["create-ui"],"acceptanceCriteria":["Saved notes survive a full page reload","A search returns matching notes"]}],"testStrategy":["Independently check button clicks using keyboard and mouse","Reload and verify persistence"],"risks":["Static Web runtime is not yet independently qualified"]}""",
                _ => throw new Exception("Unknown MAF purpose")
            };
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, text)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Non-streaming probe.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

sealed class Phase2TechnicalLeadPlanner : IImplementationPlanner
{
    public int Attempts { get; private set; }

    public Task<ImplementationOutline> PlanAsync(
        string objective, string specification,
        DraftArchitectureReadiness architecture, string model,
        CancellationToken cancellation)
    {
        if (model != "qwen3.5:4b" ||
            !objective.Contains("timer", StringComparison.OrdinalIgnoreCase) ||
            !specification.Contains("offline browser timer", StringComparison.OrdinalIgnoreCase) ||
            !architecture.ReadyForSourceGeneration ||
            architecture.ApprovedChoices.Count != 2 ||
            architecture.ApprovedChoices[0].RuntimeProfileId != "static-web")
            throw new Exception("Technical Lead received wrong owner-approved handoff");
        Attempts++;
        return Task.FromResult(new ImplementationOutline(
            "Implement an offline timer in two separately testable milestones.",
            [
                new ImplementationWorkItem("timer-interface",
                    "Build accessible timer controls",
                    "Implement HTML controls using the approved static web stack.",
                    [],
                    ["Buttons are operable using mouse and keyboard"]),
                new ImplementationWorkItem("timer-state",
                    "Connect timer state behavior",
                    "Write plain JavaScript to manage start pause and reset.",
                    ["timer-interface"],
                    ["Start advances the displayed timer", "Reset returns to zero"])
            ],
            ["Execute independent browser mouse and keyboard checks",
             "Validate static file dependencies without network"],
            ["Static Web execution is not qualified on this host"]));
    }
}

sealed class Phase4EngineeringModel : IEngineeringTaskModel
{
    public List<string> AssignedIds { get; } = [];
    public int PreviousFileCountOnSecondTask { get; private set; } = -1;

    public Task<EngineeringChangeSet> ImplementAsync(EngineeringTaskInput input,
        CancellationToken cancellation)
    {
        if (input.Plan.Status != "Approved" || !input.Plan.Current ||
            input.Plan.PlanSha256.Length != 64 ||
            input.Plan.Model != "qwen3.5:4b" ||
            !input.Architecture.ReadyForSourceGeneration ||
            input.Architecture.ArchitectureSha256 != input.Plan.ArchitectureSha256)
            throw new Exception("SoftwareEngineer was not given the current approved plan");
        AssignedIds.Add(input.WorkItem.Id);
        if (input.WorkItem.Id == "timer-interface")
            return Task.FromResult(new EngineeringChangeSet(
                "Created a real browser interface with accessible timer controls.",
                [new EngineeringFileChange("index.html", "Browser entry point",
                    "<!doctype html><html><body><button id='start'>Start</button><script src='app.js'></script></body></html>")],
                ["Open the browser and check keyboard and mouse behavior"]));
        if (input.WorkItem.Id == "timer-state")
        {
            PreviousFileCountOnSecondTask = input.ExistingFiles.Count;
            return Task.FromResult(new EngineeringChangeSet(
                "Connected timer state to the Start button and reset behavior.",
                [new EngineeringFileChange("app.js", "Complete timer interactions",
                    "document.getElementById('start').addEventListener('click', () => { console.log('start'); });")],
                ["Verify timer movement and reset without model claims"]));
        }
        throw new Exception("Unapproved task sent to engineer");
    }
}

sealed class Phase4FakeMafFactory : IMafChatClientFactory
{
    public List<MafAgentPurpose> Purposes { get; } = [];
    public List<string> Models { get; } = [];
    public bool SystemPolicySeen { get; private set; }
    public bool NoTools { get; private set; }
    public bool TaskDescriptionSeen { get; private set; }

    public IChatClient Create(string model, MafAgentPurpose purpose)
    {
        Models.Add(model);
        Purposes.Add(purpose);
        return new Fake(this);
    }

    private sealed class Fake(Phase4FakeMafFactory parent) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var history = messages.ToArray();
            parent.SystemPolicySeen = (options?.Instructions?.Contains(
                "Professional backstory:", StringComparison.Ordinal) ?? false) ||
                history.Any(m => m.Role.Value == "system" &&
                    m.Text?.Contains("Professional backstory:", StringComparison.Ordinal) == true);
            parent.TaskDescriptionSeen = history.Any(m => m.Role.Value == "user" &&
                m.Text?.Contains("create-ui", StringComparison.Ordinal) == true);
            parent.NoTools = options?.Tools is null || options.Tools.Count == 0;
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant,
                    """{"summary":"Implemented complete browser UI for approved notes task.","files":[{"path":"index.html","purpose":"Complete notes entry point","content":"<!doctype html><html><body><button>Save</button></body></html>"}],"verificationNotes":["Test saving notes in browser later"]}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Task protocol is nonstreaming.");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

sealed class Phase5QaModel : IQaSourceReviewer
{
    public List<string> ReviewedFiles { get; } = [];

    public Task<QaFileAssessment> ReviewAsync(
        QaReviewInput input, CancellationToken cancellation)
    {
        if (input.SourceRun.State != "Generated" ||
            input.Plan.Status != "Approved" || !input.Plan.Current ||
            input.Plan.PlanSha256 != input.SourceRun.PlanSha256 ||
            input.Plan.Model != "qwen3.5:4b" ||
            !input.Architecture.ReadyForSourceGeneration ||
            input.Architecture.ArchitectureSha256 != input.Plan.ArchitectureSha256 ||
            input.AllFiles.Count != 2 ||
            !input.AllFiles.Any(f => f.Path == input.File.Path &&
                f.Sha256 == input.File.Sha256))
            throw new Exception("Independent QA received wrong source or stale approval");
        ReviewedFiles.Add(input.File.Path);
        return Task.FromResult(input.File.Path == "app.js"
            ? new QaFileAssessment(
                "The timer code registers a button listener but lacks a persistent timer.",
                [new QaFinding("Major", "Start advances timer state",
                    "The handler logs start but does not update a visible timer.",
                    "Implement timer state and check it with a real browser")],
                ["Click Start and observe the timer after several seconds"])
            : new QaFileAssessment(
                "The HTML source references a script and a clickable button.",
                [],
                ["Load index.html offline and verify the script is linked"]));
    }
}

sealed class Phase5FakeMafFactory : IMafChatClientFactory
{
    public List<MafAgentPurpose> Purposes { get; } = [];
    public List<string> Models { get; } = [];
    public bool QaRoleSeen { get; private set; }
    public bool SourceSeen { get; private set; }
    public bool NoTools { get; private set; }

    public IChatClient Create(string model, MafAgentPurpose purpose)
    {
        Models.Add(model);
        Purposes.Add(purpose);
        return new Fake(this);
    }

    private sealed class Fake(Phase5FakeMafFactory parent) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var history = messages.ToArray();
            parent.QaRoleSeen =
                (options?.Instructions?.Contains("Professional backstory:",
                    StringComparison.Ordinal) ?? false) &&
                (options?.Instructions?.Contains("independent QA Engineer",
                    StringComparison.Ordinal) ?? false);
            parent.SourceSeen = history.Any(m => m.Role.Value == "user" &&
                m.Text?.Contains("<html><body><button>Save",
                    StringComparison.Ordinal) == true);
            parent.NoTools = options?.Tools is null || options.Tools.Count == 0;
            return Task.FromResult(new ChatResponse(new ChatMessage(
                ChatRole.Assistant,
                """{"summary":"The static source exposes an interactive control but its behavior is unknown.","findings":[{"severity":"Minor","requirement":"The Save button must work","evidence":"A button is declared but no listener is present in this source","recommendation":"Inspect linked scripts and test saving notes in a browser"}],"manualChecks":["Click Save and reload the page in a real browser"]}""")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA only allows bounded nonstreaming turns.");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
