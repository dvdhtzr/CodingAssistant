using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodingAssistant.Api;
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

var root = Path.Combine(Path.GetTempPath(),
    "ca-phase2-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
if (!OperatingSystem.IsWindows())
    File.SetUnixFileMode(root,
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
WebApplication? app = null;
HttpClient? client = null;
var boundModel = new Phase2DraftModel();
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
        architectureAdvisorOverride: new Phase2Advisor());
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
        architectureAdvisorOverride: new Phase2Advisor());
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
