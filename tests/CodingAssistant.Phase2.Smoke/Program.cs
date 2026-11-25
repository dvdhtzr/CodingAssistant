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

// Exercise actual shared-capacity lease semantics without an installed model.
{
    var scheduler=new LocalInferenceScheduler(1,2,TimeSpan.FromSeconds(3));
    var first=await scheduler.AcquireAsync(CancellationToken.None);
    using var cancelWait=new CancellationTokenSource();
    var queued=0;
    var second=scheduler.AcquireAsync(cancelWait.Token,()=>Interlocked.Increment(ref queued));
    var third=scheduler.AcquireAsync(CancellationToken.None,()=>Interlocked.Increment(ref queued));
    Check(scheduler.Snapshot() is {Capacity:1,Active:1,Waiting:2} && queued==2 &&
        !second.IsCompleted && !third.IsCompleted,"capacity one queues other actual inference leases");
    try
    {
        using var overflow=await scheduler.AcquireAsync(CancellationToken.None);
        Check(false,"queue overflow must fail safely");
    }
    catch(StoreFailure e)when(e.Code=="INFERENCE_QUEUE_FULL"){Check(true,"bounded inference queue rejects overflow");}
    cancelWait.Cancel();
    try{using var ignored=await second;Check(false,"cancelled waiter cannot obtain lease");}
    catch(OperationCanceledException){Check(true,"queued cancellation stops without taking capacity");}
    first.Dispose();first.Dispose();
    using(var lease=await third)
        Check(scheduler.Snapshot() is {Active:1,Waiting:0},"slot transfers without duplicate release");
    Check(scheduler.Snapshot() is {Active:0,Waiting:0},"lease returns capacity exactly once");
    var timeoutScheduler=new LocalInferenceScheduler(1,2,TimeSpan.FromMilliseconds(30));
    using(var held=await timeoutScheduler.AcquireAsync(CancellationToken.None))
    {
        try{using var timeout=await timeoutScheduler.AcquireAsync(CancellationToken.None);Check(false,"wait timeout required");}
        catch(StoreFailure e)when(e.Code=="INFERENCE_QUEUE_TIMEOUT")
        {Check(timeoutScheduler.Snapshot() is {Active:1,Waiting:0},"timed-out waiter releases queue reservation");}
    }
    var parallel=new LocalInferenceScheduler(2);
    using(var a=await parallel.AcquireAsync(CancellationToken.None))
    using(var b=await parallel.AcquireAsync(CancellationToken.None))
        Check(parallel.Snapshot() is {Capacity:2,Active:2,Waiting:0},"explicit capacity two is honored");
}

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

// Live-model-free MAF regression: a repeated answered question must cause
// exactly one corrective call, then a new question or reviewable exit.
{
    var prior = new InterviewMessage[] {
        new(1, "assistant",
            "Should we only use publicly available demographic data from podcast platforms?",
            "2026-11-22T12:00:00Z"),
        new(2, "owner", "Yes", "2026-11-22T12:00:01Z")
    };
    var repeated =
        """{"message":"Should we only use publicly available demographic data from podcast platforms?","readyForSpecification":false}""";
    var corrected =
        """{"message":"Which geographic markets should podcast analytics cover?","readyForSpecification":false}""";
    var factory = new Phase3FakeMafFactory();
    factory.InterviewReplies.Enqueue(repeated);
    factory.InterviewReplies.Enqueue(corrected);
    var team = new MafAgentTeam(factory);
    var reply = await team.AskAsync("Build a podcast analytics dashboard",
        prior, "qwen3.5:4b", CancellationToken.None);
    Check(reply.Message.Contains("geographic markets") &&
          !reply.ReadyForSpecification &&
          factory.Purposes.Count == 2 &&
          factory.InterviewCorrectionPrompts.Count == 1,
        "MAF avoids repeating answered question and asks new decision");

    var stuck = new Phase3FakeMafFactory();
    stuck.InterviewReplies.Enqueue(repeated);
    stuck.InterviewReplies.Enqueue(repeated);
    var review = await new MafAgentTeam(stuck).AskAsync(
        "Build a podcast analytics dashboard", prior,
        "qwen3.5:4b", CancellationToken.None);
    Check(review.ReadyForSpecification && review.Choice is null &&
          stuck.Purposes.Count == 2,
        "MAF repetition loop ends with owner-controlled specification review");
}

// Regression for the live interview: thirteen owner answers, thirteen Product
// Owner questions, plus the deterministic completion handoff are 27 messages.
// All saved decisions must reach the model when drafting a specification.
{
    Check(InterviewStore.MaxTranscriptMessages == 27,
        "transcript limit includes the last answer and completion handoff");
    var completeHistory = Enumerable.Range(1, InterviewStore.MaxTranscriptMessages)
        .Select(index => new InterviewMessage(index,
            index % 2 == 0 ? "owner" : "assistant",
            index % 2 == 0
                ? $"Owner requirement decision {index / 2}: platform data only."
                : index == InterviewStore.MaxTranscriptMessages
                    ? "Your final answer has been saved. Product discovery is complete."
                    : $"Product Owner clarification question {(index + 1) / 2}?",
            "2026-11-22T17:00:00Z")).ToArray();
    var factory = new Phase3FakeMafFactory();
    var draft = await new MafAgentTeam(factory).DraftAsync(
        "Build a podcast audience analytics dashboard", completeHistory,
        "qwen3.5:4b", CancellationToken.None);
    Check(draft.Length >= 180 &&
          factory.Purposes.SequenceEqual([MafAgentPurpose.Specification]) &&
          factory.SpecificationLastMessages.Count == 1 &&
          factory.SpecificationLastMessages[0].StartsWith(
              "PRODUCT OWNER REQUEST: Draft the product specification NOW",
              StringComparison.Ordinal) &&
          factory.SpecificationOwnerDecisionCounts.SequenceEqual([13]) &&
          factory.AllowedToolsSeen == 0,
        "MAF drafts all 27 saved messages with an explicit final user command");
    try
    {
        _ = await new MafAgentTeam(new Phase3FakeMafFactory()).DraftAsync(
            "Build podcast analytics", [.. completeHistory,
                new InterviewMessage(28, "owner", "Unexpected extra turn",
                    "2026-11-22T17:01:00Z")],
            "qwen3.5:4b", CancellationToken.None);
        Check(false, "unbounded interviews must still be rejected");
    }
    catch (StoreFailure error) when (error.Code == "INVALID_INPUT")
    {
        Check(true, "messages beyond the documented limit still fail closed");
    }
}

// Capture only non-sensitive completion metadata when local generation
// returned no usable final answer. Never echo response or thinking text.
{
    var response = """{"done_reason":"stop","prompt_eval_count":900,"eval_count":4800,"message":{"role":"assistant","content":"","thinking":"PRIVATE SECRET TEXT"}}""";
    var safe = LocalMafChatClient.SafeCompletionSummary(response);
    Check(safe.Contains("done_reason=stop") &&
          safe.Contains("content_chars=0") &&
          safe.Contains("prompt_tokens=900") &&
          safe.Contains("generated_tokens=4800") &&
          safe.Contains("has_thinking=true") &&
          !safe.Contains("PRIVATE") && !safe.Contains("SECRET"),
        "Ollama empty response diagnostics never expose model content");
}

// A local Ollama response may finish with no final content, or with a
// truncated answer. Never leak thinking or admit an incomplete specification.
{
    var normal = """{"done_reason":"stop","message":{"role":"assistant","content":"# Final specification","thinking":"private model thoughts"}}""";
    Check(LocalMafChatClient.ReadAssistantContent(normal,
        MafAgentPurpose.Specification) == "# Final specification",
        "only final assistant content is used for Product Owner drafting");

    // The reported live failure was at the thinking-tag check, not the
    // empty-content check. Closed reasoning prefixes may have a complete,
    // reviewable specification afterward. Never leak the reasoning prefix.
    var finalSpec = "# Podcast Audience Dashboard\n\n" +
        "## Goal\nLet a user select one or more podcasts and view aggregated " +
        "audience demographics from authorized podcast-platform sources. " +
        "## Acceptance criteria\nWhen the selection changes, recompute the " +
        "aggregation while clearly displaying missing or unavailable data. " +
        "Do not infer private demographics or fabricate third-party sources.";
    foreach (var modelContent in new[] {
        "<think>Private draft planning, not part of the answer.</think>\n" + finalSpec,
        "</think>\n" + finalSpec,
        finalSpec
    })
    {
        var payload = JsonSerializer.Serialize(new {
            done_reason = "stop",
            message = new { role = "assistant", content = modelContent }
        });
        var usable = LocalMafChatClient.ReadAssistantContent(payload,
            MafAgentPurpose.Specification);
        Check(usable == finalSpec &&
              InterviewStore.ValidSpecification(usable) == finalSpec &&
              !usable.Contains("Private draft planning", StringComparison.Ordinal),
            "closed reasoning prefix is stripped and full final specification validated");
    }

    foreach (var pair in new[] {
        ("""{"done_reason":"stop","message":{"role":"assistant","content":"","thinking":"private model thoughts"}}""", "MODEL_THINKING_ONLY"),
        ("""{"done_reason":"stop","message":{"role":"assistant","content":"<think>private model thoughts</think>"}}""", "MODEL_THINKING_ONLY"),
        ("""{"done_reason":"stop","message":{"role":"assistant","content":"<think>private model thoughts"}}""", "MODEL_THINKING_ONLY"),
        ("""{"done_reason":"stop","message":{"role":"assistant","content":"An unrelated prefix <think>private</think> # Spec"}}""", "MODEL_THINKING_ONLY"),
        ("""{"done_reason":"stop","message":{"role":"assistant","content":"<think>nested <think>reasoning</think> # Spec"}}""", "MODEL_THINKING_ONLY"),
        ("""{"done_reason":"stop","message":{"role":"assistant","content":"</think> # Spec <think>restarted"}}""", "MODEL_THINKING_ONLY"),
        ("""{"done_reason":"length","message":{"role":"assistant","content":"","thinking":"private model thoughts"}}""", "MODEL_OUTPUT_TRUNCATED"),
        ("""{"done_reason":"length","message":{"role":"assistant","content":"# Partial draft"}}""", "MODEL_OUTPUT_TRUNCATED")
    })
    {
        try
        {
            _ = LocalMafChatClient.ReadAssistantContent(pair.Item1,
                MafAgentPurpose.Specification);
            Check(false, "empty or truncated output must not be accepted");
        }
        catch (StoreFailure error) when (error.Code == pair.Item2)
        {
            Check(true, "Ollama output rejected with precise bounded cause");
        }
    }

    foreach (var firstFailure in new[] {
        "MODEL_OUTPUT_REJECTED", "MODEL_OUTPUT_TRUNCATED"
    })
    {
        var model = new Phase3FakeMafFactory();
        model.SpecificationFailures.Enqueue(firstFailure);
        var draft = await new MafAgentTeam(model).DraftAsync(
            "Build a podcast demographics dashboard", [
                new InterviewMessage(1, "assistant",
                    "Which podcasts should be included?", "2026-11-22T18:00:00Z"),
                new InterviewMessage(2, "owner", "Multiple selected podcasts",
                    "2026-11-22T18:00:01Z")
            ], "qwen3:30b", CancellationToken.None);
        Check(draft.Length >= 180 &&
              model.Purposes.SequenceEqual([
                  MafAgentPurpose.Specification, MafAgentPurpose.Specification]) &&
              model.SpecificationCorrectionPrompts.Count == 1 &&
              model.SpecificationLastMessages[0].StartsWith(
                  "PRODUCT OWNER REQUEST: Draft the product specification NOW",
                  StringComparison.Ordinal) &&
              model.SpecificationLastMessages[1].Contains(
                  "previous response was empty", StringComparison.Ordinal) &&
              model.Models.All(x => x == "qwen3:30b") &&
              model.AllowedToolsSeen == 0,
            "MAF retries one blank or truncated specification with same local model");
    }

    var thinkingOnly = new Phase3FakeMafFactory();
    thinkingOnly.SpecificationFailures.Enqueue("MODEL_THINKING_ONLY");
    try
    {
        _ = await new MafAgentTeam(thinkingOnly).DraftAsync(
            "Build podcast analytics", [], "qwen3:30b", CancellationToken.None);
        Check(false, "thinking-only output cannot become a specification");
    }
    catch (StoreFailure error) when (error.Code == "MODEL_THINKING_ONLY")
    {
        Check(thinkingOnly.Purposes.SequenceEqual([
            MafAgentPurpose.Specification]),
            "thinking-only model fails with actionable code without futile retry");
    }

    var failsTwice = new Phase3FakeMafFactory();
    failsTwice.SpecificationFailures.Enqueue("MODEL_OUTPUT_REJECTED");
    failsTwice.SpecificationFailures.Enqueue("MODEL_OUTPUT_TRUNCATED");
    try
    {
        _ = await new MafAgentTeam(failsTwice).DraftAsync(
            "Build podcast analytics", [], "qwen3:30b", CancellationToken.None);
        Check(false, "persistent blank responses must not save a fake draft");
    }
    catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_TRUNCATED")
    {
        Check(failsTwice.Purposes.Count == 2 &&
              failsTwice.SpecificationCorrectionPrompts.Count == 1,
            "persistent output failure is bounded at two attempts");
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


// Contract diagnosis is bounded, schema-aware and never echoes untrusted output.
{
    const string goodPlan = """{"summary":"Build a minimal offline notes application with working controls and persistence.","tasks":[{"id":"create-ui","title":"Create the browser interface","description":"Create accessible controls for notes and search","dependsOn":[],"acceptanceCriteria":["A user can type into the note editor and click Save"]}],"testStrategy":["Independently check button clicks using keyboard and mouse"],"risks":[]}""";
    Check(ImplementationPlanContract.TryParse(goodPlan, out var validOutline,
            out var okReason) && validOutline!.Tasks.Count == 1 && okReason == "",
        "valid Technical Lead plan remains acceptable");
    Check(!ImplementationPlanContract.TryParse("not-json SECRET_SENTINEL",
            out _, out var syntaxReason) &&
          syntaxReason.StartsWith("JSON_SYNTAX:", StringComparison.Ordinal) &&
          !syntaxReason.Contains("SECRET_SENTINEL", StringComparison.Ordinal),
        "invalid JSON diagnostic never includes raw model content");
    var invalidOrder = goodPlan.Replace("\"dependsOn\":[]",
        "\"dependsOn\":[\"future-task\"]", StringComparison.Ordinal);
    Check(!ImplementationPlanContract.TryParse(invalidOrder,
            out _, out var orderReason) &&
          orderReason.StartsWith("DEPENDENCY_ORDER:", StringComparison.Ordinal),
        "dependency-order error receives a targeted correction hint");
    var invalidDescription = goodPlan.Replace(
        "Create accessible controls for notes and search", "short",
        StringComparison.Ordinal);
    Check(!ImplementationPlanContract.TryParse(invalidDescription,
            out _, out var descriptionReason) &&
          descriptionReason.StartsWith("TASK_DESCRIPTION:", StringComparison.Ordinal),
        "too-short task descriptions receive specific correction hints");

    var readiness = new DraftArchitectureReadiness("READY", true,
        new string('a', 64), [], [
            new DraftArchitectureChoice("technology-stack", 1,
                new string('b', 64), "static-web", "Static HTML and JavaScript",
                "Offline", "Manual DOM", "static-web"),
            new DraftArchitectureChoice("architecture-style", 1,
                new string('c', 64), "small-modules", "Small modules",
                "Simple to test", "Manual boundaries", null)
        ]);
    var correctingFactory = new Phase3FakeMafFactory();
    correctingFactory.TechnicalLeadReplies.Enqueue(invalidOrder);
    correctingFactory.TechnicalLeadReplies.Enqueue(goodPlan);
    var corrected = await new MafAgentTeam(correctingFactory).PlanAsync(
        "Build an offline notes tracker", new string('s', 180), readiness,
        "qwen3.5:4b", CancellationToken.None);
    Check(corrected.Tasks.Count == 1 &&
          correctingFactory.Purposes.Count == 2 &&
          correctingFactory.TechnicalLeadCorrectionPrompts.Any(p =>
              p.Contains("DEPENDENCY_ORDER:", StringComparison.Ordinal)),
        "Technical Lead recovers from invalid dependency order using validator hint");

    var rejectingFactory = new Phase3FakeMafFactory();
    for (var i = 0; i < 3; i++)
        rejectingFactory.TechnicalLeadReplies.Enqueue(invalidOrder);
    try
    {
        _ = await new MafAgentTeam(rejectingFactory).PlanAsync(
            "Build an offline notes tracker", new string('s', 180), readiness,
            "qwen3.5:4b", CancellationToken.None);
        Check(false, "persistently invalid Technical Lead plans must fail");
    }
    catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_REJECTED")
    {
        Check(rejectingFactory.Purposes.Count == 3,
            "Technical Lead fails closed after exactly three model attempts");
    }
}

// Five true MAF roles have separate goals, fictional work histories and
// no rights to use executable tools or load skills merely by naming them.
{
    var roster = EngineeringAgentRoster.All;
    Check(EngineeringRoleCapabilities.Allows("SoftwareEngineer",
              EngineeringCapability.WriteSource) &&
          !EngineeringRoleCapabilities.Allows("LeadSoftwareEngineer",
              EngineeringCapability.WriteSource) &&
          !EngineeringRoleCapabilities.Allows("QaEngineer",
              EngineeringCapability.WriteSource) &&
          !EngineeringRoleCapabilities.Allows("Unknown",
              EngineeringCapability.SendMessage),
        "host capability policy denies reviewer and unknown source writes");
    try
    {
        EngineeringRoleCapabilities.Require("QaEngineer", EngineeringCapability.WriteSource);
        Check(false, "QA source write must be rejected");
    }
    catch (StoreFailure error)
    {
        Check(error.Code == "AGENT_CAPABILITY_DENIED", "QA source write denied");
    }
    Check(roster.Count == 6 &&
          roster.Select(a => a.Id).Distinct().Count() == 6 &&
          roster.All(a => a.Goal.Length > 25 &&
              a.Backstory.Length > 25 && !a.CanExecuteCode) &&
          EngineeringAgentRoster.Get("SoftwareEngineer")
              .AllowedSkillScopes.Contains("source-implementation") &&
          EngineeringAgentRoster.Get("QaEngineer")
              .AllowedSkillScopes.Contains("static-code-review"),
        "six distinct role/goals/backstories and non-executable skill scopes");

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
    // Repair broken JSON, unsafe source paths and model refusals without
    // accepting any invalid files or attempting code execution.
    const string goodSource = """{"summary":"Implemented complete recipe ingredient input interactions.","files":[{"path":"index.html","purpose":"Complete ingredient input screen","content":"<!doctype html><html><body><input id='ingredients'><button>Compare</button></body></html>"}],"verificationNotes":["Verify ingredient entry in a browser"]}""";
    // Qwen may include a closed reasoning prefix even with think:false.
    // Never turn the private reasoning into source; only parse final JSON.
    foreach (var answer in new[] {
        "<think>PRIVATE reasoning not source</think>\n" + goodSource,
        "</think>\n" + goodSource,
        goodSource
    })
    {
        var payload = JsonSerializer.Serialize(new {
            done_reason = "stop", message = new { role = "assistant", content = answer }
        });
        var final = LocalMafChatClient.ReadAssistantContent(
            payload, MafAgentPurpose.SoftwareEngineer);
        Check(MafSoftwareEngineer.Parse(final).Files.Single().Path == "index.html" &&
              !final.Contains("PRIVATE reasoning", StringComparison.Ordinal),
            "Software Engineer accepts only JSON after CLOSED Qwen thinking prefix");
    }
    var literalThink = goodSource.Replace(
        "<button>Compare</button>", "<span><think>literal HTML</think></span>",
        StringComparison.Ordinal);
    var literalPayload = JsonSerializer.Serialize(new {
        done_reason = "stop",
        message = new { role = "assistant", content = literalThink }
    });
    Check(LocalMafChatClient.ReadAssistantContent(
        literalPayload, MafAgentPurpose.SoftwareEngineer) == literalThink,
        "literal think-like markup inside a JSON string is not stripped as reasoning");
    foreach (var response in new[] {
        "<think>never closed\n" + goodSource,
        "<think>private</think><think>restarted</think>" + goodSource
    })
    {
        var payload = JsonSerializer.Serialize(new {
            done_reason = "stop",
            message = new { role = "assistant", content = response }
        });
        try {
            _ = LocalMafChatClient.ReadAssistantContent(
                payload, MafAgentPurpose.SoftwareEngineer);
            Check(false, "incomplete or reopened reasoning prefix accepted");
        }
        catch (StoreFailure error) when (error.Code == "MODEL_THINKING_ONLY")
        {
            Check(true, "Software Engineer rejects unfinished or reopened thinking");
        }
    }
    Check(MafSoftwareEngineer.TryParse(goodSource, out var parsedSource,
        out var acceptedReason) && parsedSource!.Files.Count == 1 &&
        acceptedReason == "", "valid engineer output remains accepted");
    Check(MafSoftwareEngineer.TryParse("```json\n" + goodSource + "\n```",
        out var wrappedSource, out _) && wrappedSource!.Files.Count == 1,
        "complete JSON fenced output is recoverable without loosening validation");
    Check(!MafSoftwareEngineer.TryParse("SECRET_MODEL_CONTENT invalid",
        out _, out var syntaxReason) &&
        syntaxReason.StartsWith("JSON_SYNTAX:", StringComparison.Ordinal) &&
        !syntaxReason.Contains("SECRET_MODEL_CONTENT", StringComparison.Ordinal),
        "engineer source diagnostics never include model-generated content");
    Check(!MafSoftwareEngineer.TryParse(
        goodSource.Replace("index.html", "../escape.js", StringComparison.Ordinal),
        out _, out var pathReason) &&
        pathReason.StartsWith("FILE_PATH:", StringComparison.Ordinal),
        "unsafe source path rejected with targeted validation feedback");

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

    // M8.16: The Software Engineer genuinely asks the host to inspect
    // stored source, receives bounded evidence, and then produces source.
    // No native MAF tools or execution permissions are enabled.
    var inspectedFactory = new Phase4FakeMafFactory();
    inspectedFactory.Responses.Enqueue(
        """{"toolRequest":{"name":"atlas.find_symbols","argument":"index"}}""");
    inspectedFactory.Responses.Enqueue(goodSource);
    var inspectionEvidence = new List<AtlasInspectionObservation>();
    var inspectedAssignment = assignment with {
        ExistingFiles = [
            new EngineeringSourceFile("index.html", "Approved prior UI",
                "<!doctype html><html><body>Prior page</body></html>",
                new string('1', 64), "create-ui", "2026-11-22T00:00:00Z")
        ],
        OnAtlasInspection = observation => inspectionEvidence.Add(observation)
    };
    var inspectedOutput = await new MafSoftwareEngineer(inspectedFactory)
        .ImplementAsync(inspectedAssignment, CancellationToken.None);
    Check(inspectedFactory.Attempts == 2 &&
        inspectedFactory.AtlasObservations.Count == 1 &&
        inspectedFactory.AtlasObservations[0].Contains(
            "atlas.find_symbols", StringComparison.Ordinal) &&
        inspectedFactory.AtlasObservations[0].Contains(
            "\"SourceRevision\"", StringComparison.Ordinal) &&
        inspectionEvidence.Count == 1 &&
        inspectionEvidence[0].Name == "atlas.find_symbols" &&
        inspectionEvidence[0].SourceRevision.Length == 64 &&
        inspectedOutput.Files.Single().Path == "index.html" &&
        inspectedFactory.NoTools,
        "Software Engineer requests host-backed source evidence before code changes");
    var deniedFactory = new Phase4FakeMafFactory();
    deniedFactory.Responses.Enqueue(
        """{"toolRequest":{"name":"shell.run","argument":"whoami"}}""");
    deniedFactory.Responses.Enqueue(goodSource);
    var deniedEvents = new List<AtlasInspectionObservation>();
    var deniedOutput = await new MafSoftwareEngineer(deniedFactory)
        .ImplementAsync(inspectedAssignment with {
            OnAtlasInspection = observation => deniedEvents.Add(observation)
        }, CancellationToken.None);
    Check(deniedOutput.Files.Single().Path == "index.html" &&
          deniedFactory.Attempts == 2 &&
          deniedEvents.Count == 0 &&
          deniedFactory.CorrectionReasons.Any(x =>
              x.Contains("EMPTY_OR_INVALID_RESPONSE", StringComparison.Ordinal)),
        "unknown agent tool requests never execute or record observations");
    var overLimitFactory = new Phase4FakeMafFactory();
    for (var i = 0; i < 3; i++)
        overLimitFactory.Responses.Enqueue(
            """{"toolRequest":{"name":"atlas.find_symbols","argument":"index"}}""");
    var cappedEvents = new List<AtlasInspectionObservation>();
    try
    {
        _ = await new MafSoftwareEngineer(overLimitFactory)
            .ImplementAsync(inspectedAssignment with {
                OnAtlasInspection = observation => cappedEvents.Add(observation)
            }, CancellationToken.None);
        Check(false, "unbounded agent tool loop was accepted");
    }
    catch (StoreFailure error) when (error.Code == "ATLAS_TOOL_LIMIT")
    {
        Check(overLimitFactory.Attempts == 3 &&
            cappedEvents.Count == AgentAtlasInspectionGateway.MaximumRequestsPerTask,
            "source-inspection request loop has a hard two-request ceiling");
    }

    var correctedFactory = new Phase4FakeMafFactory();
    correctedFactory.Responses.Enqueue("truncated SECRET_MODEL_CONTENT");
    correctedFactory.Responses.Enqueue(goodSource);
    var correctedSource = await new MafSoftwareEngineer(correctedFactory)
        .ImplementAsync(assignment, CancellationToken.None);
    Check(correctedSource.Files.Count == 1 &&
          correctedFactory.Attempts == 2 &&
          correctedFactory.CorrectionReasons.Any(reason =>
              reason.Contains("JSON_SYNTAX:", StringComparison.Ordinal)) &&
          correctedFactory.CorrectionReasons.All(reason =>
              !reason.Contains("SECRET_MODEL_CONTENT", StringComparison.Ordinal)),
        "Software Engineer uses safe targeted correction and recovers");

    var thoughtOnly = new Phase4FakeMafFactory();
    thoughtOnly.Failures.Enqueue("MODEL_THINKING_ONLY");
    thoughtOnly.Responses.Enqueue(goodSource);
    var thoughtRejections = new List<EngineeringOutputRejection>();
    var resumedFromThinking = await new MafSoftwareEngineer(thoughtOnly)
        .ImplementAsync(assignment with {
            OnOutputRejected = reason => thoughtRejections.Add(reason)
        }, CancellationToken.None);
    Check(resumedFromThinking.Files.Single().Path == "index.html" &&
          thoughtOnly.Attempts == 2 &&
          thoughtRejections.Count == 1 &&
          thoughtRejections[0].Attempt == 1 &&
          thoughtRejections[0].Category == "MODEL_THINKING_ONLY" &&
          thoughtOnly.CorrectionReasons.Any(message =>
              message.Contains("MODEL_THINKING_ONLY", StringComparison.Ordinal)),
        "MAF recovers from thinking-only reply without exposing private thoughts");

    var launchTask = new ImplementationWorkItem(
        "launch-preparation", "Launch Preparation and Testing",
        "Prepare release setup documentation and source-only launch checklist.",
        ["create-ui"], ["A release checklist describes manual verification steps"]);
    var launchPlan = fakePlan with {
        Outline = fakePlan.Outline with {
            Tasks = [fakePlan.Outline.Tasks[0], launchTask]
        }
    };
    var launchFactory = new Phase4FakeMafFactory();
    launchFactory.Responses.Enqueue(JsonSerializer.Serialize(new {
        summary = "Prepared a source-only manual launch and verification checklist.",
        files = new[] { new { path = "README.md",
            purpose = "Complete launch checklist and manual testing guide",
            content = "# Launch readiness\\nReview the saved website source, " +
                "inspect authentication, and verify payment integration manually. " +
                "No tests have been executed by the Software Engineer." } },
        verificationNotes = new[] { "Independent QA must test the saved application" }
    }));
    var launchSource = await new MafSoftwareEngineer(launchFactory).ImplementAsync(
        assignment with {
            Plan = launchPlan, WorkItem = launchTask,
            ExistingFiles = [
                new EngineeringSourceFile("index.html", "Existing page",
                    new string('x', 16000), new string('d', 64),
                    "create-ui", "2026-11-23T00:00:00Z")
            ]
        }, CancellationToken.None);
    var launchPrior = launchFactory.UserPrompts.Single(message =>
        message.StartsWith("PREVIOUS SOURCE FILES", StringComparison.Ordinal));
    Check(launchFactory.Attempts == 1 &&
          launchSource.Files.Single().Path == "README.md" &&
          launchPrior.Length < 3000 &&
          launchPrior.Contains("originalLength", StringComparison.Ordinal) &&
          launchFactory.UserPrompts.Any(message =>
              message.Contains("SOURCE-ONLY LAUNCH PREPARATION",
                  StringComparison.Ordinal)),
        "legacy launch task is guided toward concise source-only deliverable with bounded prior context");

    // A QA repair must NOT require the full index.html entry point again
    // when the owner authorized a precise change in app.js. Large saved
    // source must be editable with a short JSON answer and preserve all
    // unrelated content and exact owner file scope.
    var brokenHandler =
        "document.getElementById('save').addEventListener('click', () => save(false));";
    var correctedHandler =
        "document.getElementById('save').addEventListener('click', () => save(true));";
    var largeScript = "const marker = 'preserve-all-existing-state';\n" +
        new string('x', 14000) + "\n" + brokenHandler + "\n" +
        "const footer = 'preserve-footer';\n";
    var savedRepairFiles = new[] {
        new EngineeringSourceFile("index.html", "Existing approved entry point",
            "<!doctype html><html><body><script src='app.js'></script></body></html>\n",
            new string('1', 64), "create-ui", "2026-11-22T00:00:00Z"),
        new EngineeringSourceFile("app.js", "Original interactive behavior",
            largeScript, new string('2', 64), "create-ui", "2026-11-22T00:00:00Z")
    };
    var authorizedRepair = new RepairTaskContext(
        new string('3', 32), new string('4', 64),
        [new RepairFindingReference("app.js", 0, "Major",
            "Save handler ignores the checkbox state",
            "The saved handler calls save(false) unconditionally",
            "Read checkbox state in the Save handler", "ImplementationDefect")],
        ["app.js"]);
    var patchJson = JsonSerializer.Serialize(new {
        summary = "Correct the exact saved Save handler while preserving all other code.",
        edits = new[] { new {
            path = "app.js", oldText = brokenHandler, newText = correctedHandler
        } },
        verificationNotes = new[] {
            "Independent QA must inspect the handler and test checkbox behavior."
        }
    });
    var appliedPatch = MafSoftwareEngineer.ApplyRepairEdits(
        patchJson, authorizedRepair, savedRepairFiles);
    Check(appliedPatch.Files.Count == 1 &&
          appliedPatch.Files[0].Path == "app.js" &&
          appliedPatch.Files[0].Content.Contains(correctedHandler, StringComparison.Ordinal) &&
          appliedPatch.Files[0].Content.Contains(
              "preserve-all-existing-state", StringComparison.Ordinal) &&
          appliedPatch.Files[0].Content.Contains(
              "preserve-footer", StringComparison.Ordinal) &&
          !appliedPatch.Files[0].Content.Contains(
              brokenHandler, StringComparison.Ordinal) &&
          savedRepairFiles[1].Content.Contains(brokenHandler, StringComparison.Ordinal),
        "exact repair patch updates selected file only and leaves immutable original untouched");
    var patchFactory = new Phase4FakeMafFactory();
    patchFactory.Responses.Enqueue(patchJson);
    var engineerRepair = await new MafSoftwareEngineer(patchFactory)
        .ImplementAsync(assignment with {
            ExistingFiles = savedRepairFiles,
            Repair = authorizedRepair
        }, CancellationToken.None);
    Check(patchFactory.Attempts == 1 &&
          engineerRepair.Files.Single().Path == "app.js" &&
          engineerRepair.Files.Single().Content.Contains(
              correctedHandler, StringComparison.Ordinal) &&
          patchFactory.UserPrompts.Any(message => message.Contains(
              "FULL REPAIR TARGET SOURCE app.js", StringComparison.Ordinal)),
        "QA repair uses short exact edit even for a first-task-owned static web file");
    // Build rejected variants from typed JSON. String.Replace on
    // serialized JSON is unreliable because System.Text.Json escapes
    // apostrophes inside source as Unicode escape sequences.
    string EditJson(string path, string oldText, string newText) =>
        JsonSerializer.Serialize(new {
            summary = "Update only the precise authorized source handler.",
            edits = new[] { new { path, oldText, newText } },
            verificationNotes = new[] { "Check saved interaction manually" }
        });
    var invalidPatches = new[] {
        (Raw: EditJson("app.js", "missing uniquely anchored source",
            correctedHandler), Code: "REPAIR_EDIT_NOT_UNIQUE"),
        (Raw: EditJson("index.html", brokenHandler, correctedHandler),
            Code: "REPAIR_SCOPE"),
        (Raw: EditJson("app.js", brokenHandler, brokenHandler),
            Code: "REPAIR_EDIT_NO_CHANGE"),
        (Raw: EditJson("app.js", new string('x', 32), "changed repeated text"),
            Code: "REPAIR_EDIT_NOT_UNIQUE")
    };
    foreach (var (raw, expectedCode) in invalidPatches)
    {
        Check(!MafSoftwareEngineer.TryParseRepair(raw, authorizedRepair,
              savedRepairFiles, out _, out var failure) &&
              failure.StartsWith(expectedCode + ":", StringComparison.Ordinal),
            "unsafe or ambiguous exact edits reject with safe category " + expectedCode);
    }
    var correctionFactory = new Phase4FakeMafFactory();
    correctionFactory.Responses.Enqueue(invalidPatches[0].Raw);
    correctionFactory.Responses.Enqueue(patchJson);
    var patchRejections = new List<EngineeringOutputRejection>();
    var correctedPatch = await new MafSoftwareEngineer(correctionFactory)
        .ImplementAsync(assignment with {
            ExistingFiles = savedRepairFiles, Repair = authorizedRepair,
            OnOutputRejected = notice => patchRejections.Add(notice)
        }, CancellationToken.None);
    Check(correctedPatch.Files.Single().Path == "app.js" &&
          correctionFactory.Attempts == 2 &&
          patchRejections.Count == 1 &&
          patchRejections[0].Category == "REPAIR_EDIT_NOT_UNIQUE" &&
          correctionFactory.CorrectionReasons.Any(reason =>
              reason.Contains("REPAIR_EDIT_NOT_UNIQUE", StringComparison.Ordinal)),
        "engineer retries stale exact-match target with bounded diagnostic and no source leak");

    var firstTaskFactory = new Phase4FakeMafFactory();
    firstTaskFactory.Responses.Enqueue(goodSource.Replace(
        "index.html", "app.js", StringComparison.Ordinal));
    firstTaskFactory.Responses.Enqueue(goodSource);
    var correctedEntry = await new MafSoftwareEngineer(firstTaskFactory)
        .ImplementAsync(assignment, CancellationToken.None);
    Check(firstTaskFactory.Attempts == 2 &&
          correctedEntry.Files.Single().Path == "index.html" &&
          firstTaskFactory.CorrectionReasons.Any(reason =>
              reason.Contains("STATIC_WEB_ENTRY:", StringComparison.Ordinal)),
        "first static web task must establish index.html before persistence");

    var wrongStackFactory = new Phase4FakeMafFactory();
    wrongStackFactory.Responses.Enqueue(goodSource.Replace(
        "index.html", "Program.cs", StringComparison.Ordinal));
    wrongStackFactory.Responses.Enqueue(goodSource);
    var correctedStack = await new MafSoftwareEngineer(wrongStackFactory)
        .ImplementAsync(assignment, CancellationToken.None);
    Check(wrongStackFactory.Attempts == 2 &&
          correctedStack.Files.Single().Path == "index.html" &&
          wrongStackFactory.CorrectionReasons.Any(reason =>
              reason.Contains("STATIC_WEB_PATH:", StringComparison.Ordinal)),
        "static web task rejects incompatible language before persistence");

    var rejectedFactory = new Phase4FakeMafFactory();
    for (var n = 0; n < 3; n++)
        rejectedFactory.Responses.Enqueue(
            goodSource.Replace("index.html", "../escape.js", StringComparison.Ordinal));
    try
    {
        _ = await new MafSoftwareEngineer(rejectedFactory)
            .ImplementAsync(assignment, CancellationToken.None);
        Check(false, "unsafe source should never pass retries");
    }
    catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_REJECTED")
    {
        Check(rejectedFactory.Attempts == 3,
            "invalid engineer outputs fail closed after three attempts");
    }

    // Sequential simulation of the five approved recipe milestones.
    var recipeFactory = new Phase4FakeMafFactory();
    var fiveTasks = Enumerable.Range(1, 5).Select(n =>
        new ImplementationWorkItem("recipe-task-" + n,
            "Recipe implementation " + n,
            "Build the next recipe comparison feature with saved source.",
            n == 1 ? [] : ["recipe-task-" + (n - 1)],
            ["User can interact with the resulting recipe page"])).ToArray();
    var recipePlan = fakePlan with {
        Outline = fakePlan.Outline with { Tasks = fiveTasks }
    };
    var existing = new List<EngineeringSourceFile>();
    for (var n = 1; n <= fiveTasks.Length; n++)
    {
        var html = "<!doctype html><html><body><input id='ingredients'>" +
            string.Concat(Enumerable.Range(1, n).Select(step =>
                "<section data-recipe-step='" + step + "'>Recipe feature " +
                step + "</section>")) + "</body></html>";
        recipeFactory.Responses.Enqueue(JsonSerializer.Serialize(new {
            summary = "Implemented recipe comparison milestone " + n,
            files = new[] { new {
                path = "index.html", purpose = "Complete recipe comparison interface",
                content = html
            } },
            verificationNotes = new[] { "Check the ingredient flow in browser" }
        }));
        var next = await new MafSoftwareEngineer(recipeFactory).ImplementAsync(
            assignment with { Plan = recipePlan, WorkItem = fiveTasks[n - 1],
                ExistingFiles = existing }, CancellationToken.None);
        foreach (var file in next.Files)
        {
            existing.RemoveAll(prior => prior.Path.Equals(file.Path,
                StringComparison.OrdinalIgnoreCase));
            existing.Add(new EngineeringSourceFile(file.Path, file.Purpose,
                file.Content, new string('d', 64), fiveTasks[n - 1].Id,
                "2026-11-22T00:00:00Z"));
        }
    }
    Check(recipeFactory.Attempts == 5 && existing.Count == 1 &&
          Enumerable.Range(1, 5).All(step => existing[0].Content.Contains(
              "data-recipe-step='" + step + "'", StringComparison.Ordinal)),
        "five sequential recipe assignments preserve the cumulative source");

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
    var leadFactory = new Phase5FakeMafFactory();
    leadFactory.ScriptedReplies.Enqueue("""
        {"disposition":"ChangesRequested","summary":"Static review found an unwired control.",
        "findings":[{"severity":"Major","category":"Correctness","path":"index.html",
        "line":1,"evidence":"<button>Save</button>","impact":"The visible control lacks demonstrated wiring.",
        "resolution":"Connect the approved save behavior.","verification":"Independently check save and reload."}]}
        """);
    var leadReport = await new MafLeadSoftwareEngineer(leadFactory).ReviewAsync(
        new QaReviewInput("Build notes", "Approved offline notes specification",
            fakePlan, arch, run, file, [file]), CancellationToken.None);
    Check(leadFactory.Purposes.SequenceEqual([MafAgentPurpose.LeadSoftwareEngineer]) &&
        leadFactory.Models.SequenceEqual([AgentRoleModelSettings.PreferredReviewer]) &&
        leadFactory.NoTools && leadFactory.SourceSeen && leadReport.Findings.Count==1,
        "sixth genuine MAF role reviews independently with exact source evidence and no tools");
    try {
        _ = MafLeadSoftwareEngineer.Parse(JsonSerializer.Serialize(leadReport with {
            Findings=[leadReport.Findings[0] with {Line=99}]}),file);
        Check(false,"invented line accepted");
    } catch (StoreFailure e) when(e.Code=="MODEL_OUTPUT_REJECTED") {
        Check(true,"reviewer cannot invent source evidence");
    }
    var assessment = await new MafQaEngineer(fakeFactory).ReviewAsync(
        new QaReviewInput("Build notes", "Approved offline notes specification",
            fakePlan, arch, run, file, [file]), CancellationToken.None);
    Check(assessment.Findings.Count == 1 &&
          fakeFactory.Purposes.SequenceEqual([MafAgentPurpose.QaEngineer]) &&
          fakeFactory.Models.SequenceEqual(["qwen3.5:4b"]) &&
          fakeFactory.QaRoleSeen && fakeFactory.NoTools &&
          fakeFactory.SourceSeen,
        "fifth real MAF agent independently inspects exact source without tools");
    var investigation = new EngineeringRunEvent(6,
        "EngineerAtlasInspected",
        "SoftwareEngineer used atlas.find_symbols on accepted source " +
            "revision 0123456789ab (2 results)",
        "2026-11-21T00:02:20Z");
    var reassessed = await new MafQaEngineer(fakeFactory).ReviewAsync(
        new QaReviewInput("Build notes", "Approved offline notes specification",
            fakePlan, arch, run, file, [file], [investigation]),
        CancellationToken.None);
    Check(reassessed.Findings.Count == 1 &&
          fakeFactory.InspectionContextSeen &&
          fakeFactory.NoTools,
        "independent QA receives bounded host-logged engineering investigation context without gaining tools");
    var focusedReview = await new MafQaEngineer(fakeFactory).ReviewAsync(
        new QaReviewInput("Build notes", "Approved offline notes specification",
            fakePlan, arch, run, file, [file], [investigation], null,
            [new RepairFindingReference("index.html", 0, "Major",
                "Save button is not connected to persistent storage",
                "Current source declares button without working save logic",
                "Recheck Save behavior after source edit", "ImplementationDefect")]),
        CancellationToken.None);
    Check(focusedReview.Findings.Count == 1 &&
          fakeFactory.RepairFocusSeen &&
          fakeFactory.NoTools,
        "post-repair independent QA receives owner-confirmed target as a recheck, not proof of repair");
}

// M8.18: independent MAF QA selects bounded source inspections, receives
// host-issued evidence, and still cannot access another file or run tools.
{
    var factory = new Phase5FakeMafFactory();
    var plan = new ImplementationPlan(new string('a', 32), 1, 1,
        new string('b', 64), new string('c', 64), "qwen3.5:4b",
        new ImplementationOutline("Independent QA investigation fixture.",
            [new ImplementationWorkItem("qa", "Assess source",
                "Investigate a saved file without executing it.", [],
                ["Report a concrete source concern"])], [], []),
        new string('d', 64), "Approved", "2026-11-21T00:00:00Z",
        "2026-11-21T00:01:00Z", true, "Approved");
    var arch = new DraftArchitectureReadiness("READY", true,
        new string('c', 64), [], []);
    var run = new EngineeringRun(new string('9', 32), plan.TaskId,
        1, plan.PlanSha256, 1, plan.SpecificationSha256,
        plan.ArchitectureSha256, plan.Model, "Generated", "SourceOnlyGenerated",
        null, "2026-11-21T00:02:00Z", "2026-11-21T00:03:00Z");
    var sourceFile = new EngineeringSourceFile("index.html", "Reviewed source",
        "<!doctype html><html><button>Save</button></html>",
        new string('8', 64), "qa", "2026-11-21T00:03:00Z");
    var privateFile = sourceFile with { Path = "secrets.cs",
        Content = "namespace Secret; public class RestrictedDetails {}",
        Sha256 = new string('7', 64) };
    var input = new QaReviewInput("Review controls",
        "Approved source assessment", plan, arch, run, sourceFile,
        [sourceFile, privateFile]);
    var found = QaAtlasInspectionGateway.Execute(input,
        new AtlasInspectionRequest("atlas.find_symbols", "index"));
    Check(found.Symbols.Count > 0 &&
          found.Symbols.All(s => s.Path == "index.html") &&
          found.ResultCount <= 12 && found.SourceRevision.Length == 64,
        "QA read-only tool returns only the assigned file's indexed source");
    var excluded = QaAtlasInspectionGateway.Execute(input,
        new AtlasInspectionRequest("atlas.find_symbols", "RestrictedDetails"));
    Check(excluded.Symbols.Count == 0,
        "QA selected lookup cannot reveal other saved file declarations");
    var foreignId = CodeAtlasIndexer.Index(run.Id, [sourceFile, privateFile])
        .Nodes.Single(n => n.Name == "RestrictedDetails").Id;
    var forbiddenExcerpt = QaAtlasInspectionGateway.Execute(input,
        new AtlasInspectionRequest("atlas.source_excerpt", foreignId));
    Check(forbiddenExcerpt.ResultCount == 0 &&
          forbiddenExcerpt.SourceEvidence is null,
        "foreign file symbol IDs cannot bypass QA's exact-file scope");
    try
    {
        _ = QaAtlasInspectionGateway.Execute(input,
            new AtlasInspectionRequest("shell.run", "echo hi"));
        Check(false, "QA must reject executable tool requests");
    }
    catch (StoreFailure e) when (e.Code == "INVALID_INPUT")
    {
        Check(true, "QA host broker rejects unapproved tools");
    }

    factory.ScriptedReplies.Enqueue(
        """{"toolRequest":{"name":"atlas.find_symbols","argument":"index"}}""");
    factory.ScriptedReplies.Enqueue(
        """{"summary":"The control is present but click behavior remains unverified.","findings":[],"manualChecks":["Click Save in an independent browser session"]}""");
    var observations = new List<AtlasInspectionObservation>();
    var report = await new MafQaEngineer(factory).ReviewAsync(
        input with { OnAtlasInspection = observations.Add },
        CancellationToken.None);
    Check(report.Findings.Count == 0 && observations.Count == 1 &&
          observations[0].SourceRevision == found.SourceRevision &&
          observations[0].Name == "atlas.find_symbols" &&
          factory.HostObservationSeen && factory.NoTools,
        "real QA MAF agent requests host observation then returns independent report");

    var limited = new Phase5FakeMafFactory();
    for (var i = 0; i < 3; i++)
        limited.ScriptedReplies.Enqueue(
            """{"toolRequest":{"name":"atlas.find_symbols","argument":"index"}}""");
    var count = 0;
    try
    {
        _ = await new MafQaEngineer(limited).ReviewAsync(
            input with { OnAtlasInspection = _ => count++ },
            CancellationToken.None);
        Check(false, "QA must never be allowed a third inspection");
    }
    catch (StoreFailure e) when (e.Code == "ATLAS_TOOL_LIMIT")
    {
        Check(count == 2, "QA hard tool-call budget cannot be bypassed");
    }
    var denied = new Phase5FakeMafFactory();
    denied.ScriptedReplies.Enqueue(
        """{"toolRequest":{"name":"shell.run","argument":"whoami"}}""");
    try
    {
        _ = await new MafQaEngineer(denied).ReviewAsync(input,
            CancellationToken.None);
        Check(false, "QA must never gain arbitrary host capabilities");
    }
    catch (StoreFailure e) when (e.Code == "MODEL_OUTPUT_REJECTED")
    {
        Check(true, "QA MAF rejects forbidden tool names before dispatch");
    }
}

// UX-6B: a pure host-owned static preflight catches the frozen Calculation
// Journal defect. It never evaluates model JavaScript or claims a browser pass.
{
    EngineeringSourceFile Source(string path, string content) =>
        new(path, "Static preflight fixture", content,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            "fixture", "2026-11-21T17:00:00Z");
    StaticWebPreflightReport CheckFiles(string state, params EngineeringSourceFile[] files)
    {
        var run = new EngineeringRun(new string('8', 32), new string('a', 32),
            2, new string('b', 64), 2, new string('c', 64),
            new string('d', 64), "qwen3.5:4b", state, "SourceOnlyGenerated",
            null, "2026-11-21T17:00:00Z", "2026-11-21T17:01:00Z");
        return StaticWebSourcePreflight.Inspect(new EngineeringRunSnapshot(
            run, [], [], files));
    }
    var broken = CheckFiles("Generated",
        Source("calculator.html", "<!doctype html><html><button data-number='2' class='equals'>=</button><script>/* TODO wire calculator.js */</script></html>"),
        Source("calculator.js", "document.querySelector('.number').addEventListener('click', () => {}); document.getElementById('equals').addEventListener('click', () => {});"));
    Check(broken.Status == "POTENTIAL_ISSUES" &&
          broken.Findings.Any(f => f.Code == "NO_LINKED_JAVASCRIPT") &&
          broken.Findings.Any(f => f.Code == "COMMENT_ONLY_INLINE_SCRIPT") &&
          broken.Findings.Count(f => f.Code == "SELECTOR_NOT_DECLARED") == 2 &&
          broken.VerificationStatus == "GENERATED_NOT_TESTED" &&
          broken.SourceSha256?.Length == 64,
        "frozen calculator regression detects unwired JS and missing selectors without execution");
    var healthy = CheckFiles("Generated",
        Source("index.html", "<!doctype html><html><head><link href='styles/app.css' rel='stylesheet'></head><body><button class='go' id='start'>Go</button><script src='scripts/app.js'></script></body></html>"),
        Source("styles/app.css", ".go{display:block}"),
        Source("scripts/app.js", "document.querySelector('.go'); document.getElementById('start');"));
    Check(healthy.Findings.Count == 0 && healthy.Status == "NO_ISSUES_DETECTED" &&
          healthy.HtmlFileCount == 1 && healthy.ReferencedAssets == 2 &&
          healthy.VerificationStatus == "GENERATED_NOT_TESTED",
        "coherent relative HTML/CSS/JS links have no detected preflight findings, remain untested");
    var escapes = CheckFiles("Failed",
        Source("pages/index.html", "<script src='../../secrets.js'></script><script src='https://example.com/inject.js'></script><link rel='stylesheet' href='../missing.css'>"),
        Source("pages/app.js", "alert('Never executed by source preflight');"));
    Check(escapes.Findings.Count(f => f.Code == "UNRESOLVED_ASSET_REFERENCE") == 2 &&
          escapes.Findings.Any(f => f.Code == "MISSING_LINKED_ASSET") &&
          escapes.VerificationStatus == "GENERATED_NOT_TESTED",
        "source preflight rejects escapes/external references without running scripts");
    var notWeb = CheckFiles("Generated", Source("Program.cs", "class Program {}"));
    Check(notWeb.Status == "NOT_STATIC_WEB" &&
          notWeb.Findings.Count == 0 &&
          notWeb.VerificationStatus == "GENERATED_NOT_TESTED",
        "non-HTML projects are not falsely classified as browser-verified");
    var active = CheckFiles("Running", Source("index.html", "<html></html>"));
    Check(active.Status == "PENDING_SOURCE" && active.SourceSha256 is null,
        "in-progress mutable source is not reported as a final preflight");
}

// M1 project-readiness: the real podcast-shaped server snapshot is NOT a
// static page, and missing npm manifests/dependencies/tests cannot be passed
// off as a build-ready application. No generated code executes.
{
    EngineeringSourceFile File(string path, string content) =>
        new(path, "Readiness test", content,
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(content))).ToLowerInvariant(),
            "fixture", "2026-11-22T18:00:00Z");
    ProjectSourceReadinessReport Inspect(string state,
        params EngineeringSourceFile[] files)
    {
        var run = new EngineeringRun(new string('9', 32), new string('a', 32),
            1, new string('b', 64), 1, new string('c', 64),
            new string('d', 64), "qwen3.5:4b", state,
            "SourceOnlyGenerated", null, "2026-11-22T18:00:00Z",
            "2026-11-22T18:01:00Z");
        return ProjectSourceReadiness.Inspect(
            new EngineeringRunSnapshot(run, [], [], files));
    }
    const string server = """
        import express from 'express';
        import axios from 'axios';
        const app = express();
        app.get('/podcasts', async (_, response) => response.json({}));
        app.listen(3000);
        """;
    var brokenPodcast = Inspect("Generated",
        File("index.html", "<html><body>Podcasts</body></html>"),
        File("server.js", server));
    Check(brokenPodcast.ProjectKind == "NODE_SERVER" &&
          brokenPodcast.RuntimeProfileId == "node-server" &&
          brokenPodcast.Status == "INCOMPLETE" &&
          !brokenPodcast.ExecutionAvailable &&
          brokenPodcast.VerificationStatus == "GENERATED_NOT_TESTED" &&
          brokenPodcast.Findings.Any(f => f.Code == "MISSING_PACKAGE_MANIFEST") &&
          brokenPodcast.Findings.Any(f => f.Code == "MISSING_TEST_FILES") &&
          brokenPodcast.SourceSha256?.Length == 64,
        "podcast server cannot masquerade as complete Static Web source");
    var missingDeps = Inspect("Generated",
        File("server.js", server),
        File("package.json", """{"scripts":{"start":"node server.js","test":"node --test"},"dependencies":{"express":"^4.0.0"}}"""),
        File("tests/server.test.js", "import test from 'node:test'; test('smoke', () => {});"),
        File("README.md", "Document installation and local launch."));
    Check(missingDeps.Status == "INCOMPLETE" &&
          missingDeps.Findings.Any(f => f.Code == "UNDECLARED_DEPENDENCY" &&
              f.Detail.Contains("axios", StringComparison.Ordinal)) &&
          !missingDeps.Findings.Any(f => f.Code == "UNDECLARED_DEPENDENCY" &&
              f.Detail.Contains("node:test", StringComparison.Ordinal)),
        "missing Express/Axios imports are detected without treating Node builtins as packages");
    var prepared = Inspect("Generated",
        File("index.html", "<html><body>Podcasts</body></html>"),
        File("server.js", server),
        File("package.json", """{"scripts":{"start":"node server.js","test":"node --test"},"dependencies":{"express":"^4.0.0","axios":"^1.0.0"}}"""),
        File("tests/server.test.js", "import test from 'node:test'; test('smoke', () => {});"),
        File("README.md", "Document installation and local launch."));
    Check(prepared.Status == "SOURCE_READY_FOR_REVIEW" &&
          prepared.Findings.Count == 0 &&
          !prepared.ExecutionAvailable &&
          prepared.VerificationStatus == "GENERATED_NOT_TESTED",
        "coherent Node server source is ready for review, never auto-verified");
    var malformed = Inspect("Generated",
        File("server.js", server), File("package.json", "{not json"));
    Check(malformed.Findings.Any(f => f.Code == "INVALID_PACKAGE_MANIFEST") &&
          malformed.Status == "INCOMPLETE",
        "malformed dependency manifest cannot silently pass readiness");
    var html = Inspect("Generated",
        File("index.html", "<html><body>Offline</body></html>"));
    Check(html.ProjectKind == "STATIC_WEB" &&
          html.RuntimeProfileId == "static-web" &&
          !html.ExecutionAvailable,
        "standalone HTML stays Static Web and remains not verified");
    var waiting = Inspect("Running", File("server.js", server));
    Check(waiting.Status == "PENDING_SOURCE" && waiting.SourceSha256 is null,
        "mutable in-progress engineering source has no readiness fingerprint");
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
var aetLeadModel = new AetLeadReviewFixture();
var aetIssueVerifier = new AetIssueVerifierFixture();
var aetCollaborationLead=new AetCollaborationLeadFixture();
var aetSmokeRunner=new AetSmokeFixture();
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
        qaReviewerOverride: qaModel, leadReviewerOverride: aetLeadModel,
        issueVerifierOverride: aetIssueVerifier,collaborationLeadOverride:aetCollaborationLead,sourceSmokeOverride:aetSmokeRunner);
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
        Check(descriptors.Length == 3, "three non-executing runtime descriptors");
        var web = descriptors.Single(p => p.GetProperty("id").GetString() == "static-web");
        Check(web.GetProperty("qualificationStatus").GetString() == "UNQUALIFIED" &&
              !web.GetProperty("executableViaRegistry").GetBoolean() &&
              web.GetProperty("toolchainImageDigest").ValueKind == JsonValueKind.Null,
            "Static Web cannot claim an image or executable admission");
        var node = descriptors.Single(p => p.GetProperty("id").GetString() == "node-server");
        Check(node.GetProperty("qualificationStatus").GetString() == "UNQUALIFIED" &&
              !node.GetProperty("executableViaRegistry").GetBoolean() &&
              node.GetProperty("toolchainImageDigest").ValueKind == JsonValueKind.Null,
            "Node server profile is descriptive only, not permission to execute");
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
        qaReviewerOverride: qaModel, leadReviewerOverride: aetLeadModel,
        issueVerifierOverride: aetIssueVerifier,collaborationLeadOverride:aetCollaborationLead,sourceSmokeOverride:aetSmokeRunner);
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
              agents.GetArrayLength() == 6 &&
              agents.EnumerateArray().Any(a =>
                  a.GetProperty("id").GetString() == "SoftwareEngineer" &&
                  !a.GetProperty("canExecuteCode").GetBoolean() &&
                  a.GetProperty("backstory").GetString()!.Length > 25),
            "authenticated roster exposes distinct fourth MAF role and no tool rights");
    }
    var engineeringPath = "/api/tasks/" + taskBound.Id + "/engineering-runs";
    var messagesPath = "/api/tasks/" + taskBound.Id + "/engineering-messages";
    var delegationPath = "/api/tasks/" + taskBound.Id + "/engineering-delegation";
    using (var response = await client.GetAsync(delegationPath))
    using (var body = await Body(response))
        Check(response.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("policy").ValueKind == JsonValueKind.Null,
            "delegation defaults to no authority");
    using (var denied = await Post(client, delegationPath + "/propose",
        new ProposeDelegationRequest(0, secondPlanHash, ["ExecuteCode"])))
        Check(denied.StatusCode == HttpStatusCode.BadRequest,
            "delegation cannot authorize generated code execution");
    string policySha;
    using (var aetDelegationProposal = await Post(client, delegationPath + "/propose",
        new ProposeDelegationRequest(0, secondPlanHash, ["SendMessage","ScheduleWork"])))
    using (var body = await Body(aetDelegationProposal))
    {
        Check(aetDelegationProposal.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("status").GetString() == "Proposed",
            "delegation proposal grants no authority");
        policySha = body.RootElement.GetProperty("policySha256").GetString()!;
    }
    using (var denied = await Post(client, delegationPath + "/approve",
        new ApproveDelegationRequest(1, policySha, "APPROVE wrong")))
        Check(denied.StatusCode == HttpStatusCode.Conflict,
            "delegation requires exact hash confirmation");
    using (var approval = await Post(client, delegationPath + "/approve",
        new ApproveDelegationRequest(1, policySha, "APPROVE " + policySha)))
        Check(approval.StatusCode == HttpStatusCode.OK,
            "owner explicitly approves exact delegation policy");
    using (var revoked = await Post(client, delegationPath + "/revoke",
        new ApproveDelegationRequest(1, policySha, "REVOKE " + policySha)))
        Check(revoked.StatusCode == HttpStatusCode.OK &&
              new EngineeringDelegationStore(tasks).Read(taskBound.Id)?.Status == "Revoked",
            "owner can revoke persisted delegation policy");
    using (var denied = await Post(client, delegationPath + "/approve",
        new ApproveDelegationRequest(1, policySha, "APPROVE " + policySha)))
        Check(denied.StatusCode == HttpStatusCode.Conflict,
            "revoked policy cannot be replayed");
    using (var response = await client.GetAsync(messagesPath))
    using (var body = await Body(response))
        Check(response.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("messages").GetArrayLength() == 0,
            "new project has no fabricated engineering messages");
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var denied = await anonymous.GetAsync(messagesPath))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized,
            "engineering messages require owner authentication");
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
          engineeringResult.Job.Model == "qwen2.5-coder:14b" &&
          engineeringResult.Tasks.Count == 2 &&
          engineeringResult.Tasks.All(t => t.State == "Completed") &&
          engineeringResult.Files.Count == 2 &&
          engineeringResult.Files.Any(f => f.Path == "index.html") &&
          engineeringResult.Files.Any(f => f.Path == "app.js") &&
          engineeringResult.Events.Any(e => e.Kind == "EngineeringTaskStarted") &&
          engineeringResult.Events.Count(e => e.Kind == "EngineeringTaskCompleted") == 2,
        "each approved engineer task persists source and exact durable event history");
    var aetReceipts = new EngineeringMessageStore(tasks).List(taskBound.Id);
    Check(aetReceipts.Count == 4 &&
          aetReceipts.Select(m => m.Kind).SequenceEqual(
              ["TaskAssignment","TaskCompleted","TaskAssignment","TaskCompleted"]) &&
          aetReceipts.All(m => m.RunId == engineeringRunId && m.PlanSha256 == secondPlanHash) &&
          aetReceipts[0].FromAgent == "TechnicalLead" &&
          aetReceipts[1].FromAgent == "SoftwareEngineer" &&
          aetReceipts[3].SourceSha256 == QaReviewStore.Fingerprint(engineeringResult!.Files),
        "assignment and completion receipts bind actual saved revisions");
    Check(new EngineeringMessageStore(tasks).List(taskBound.Id, aetReceipts[1].Sequence)
          .SequenceEqual(aetReceipts.Skip(2)),
        "durable message cursor replays without duplication");
    var aetActivityPath = "/api/tasks/" + taskBound.Id + "/engineering-activity";
    var aetActivity = new EngineeringActivityStore(tasks).Read(taskBound.Id);
    Check(aetActivity.Events.Select(e => e.Sequence).SequenceEqual(
              Enumerable.Range(1,aetActivity.Events.Count).Select(i => (long)i)) &&
          aetActivity.Events.Count(e => e.Kind == "TaskAssignment") == 2 &&
          aetActivity.Events.Count(e => e.Kind == "TaskCompleted") == 2 &&
          aetActivity.Events.Last(e => e.Kind == "TaskCompleted").SourceSha256 ==
              QaReviewStore.Fingerprint(engineeringResult!.Files),
        "project activity replays contiguous actual assignment and source evidence");
    var aetFirstPage = new EngineeringActivityStore(tasks).Read(taskBound.Id,0,2);
    Check(aetFirstPage.Events.Count == 2 &&
          new EngineeringActivityStore(tasks).Read(taskBound.Id,
              aetFirstPage.Events[^1].Sequence).Events.First().Sequence == 3,
        "activity cursor replays a bounded page without gaps");
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var denied = await anonymous.GetAsync(aetActivityPath))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized,
            "engineering activity requires owner authentication");
    using (var invalidActivity = await client.GetAsync(aetActivityPath + "?after=-1"))
        Check(invalidActivity.StatusCode == HttpStatusCode.BadRequest,
            "negative activity cursor fails closed");
    using (var response = await client.GetAsync(aetActivityPath + "?limit=2"))
    using (var body = await Body(response))
        Check(response.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("taskId").GetString() == taskBound.Id &&
              body.RootElement.GetProperty("events").GetArrayLength() == 2,
            "activity endpoint is scoped to one project and bounded page");
    // Exercise the same application-only publication boundary future role
    // invocations use. This is injected test data, not a live model result.
    var delegationStore = new EngineeringDelegationStore(tasks);
    var messagePolicy = delegationStore.Propose(taskBound.Id,
        new ProposeDelegationRequest(1, secondPlanHash, ["SendMessage"]));
    var approvedMessagePolicy = delegationStore.Approve(taskBound.Id,
        new ApproveDelegationRequest(messagePolicy.Revision,messagePolicy.PolicySha256,
            "APPROVE " + messagePolicy.PolicySha256));
    var communicationStore = new AgentCommunicationStore(tasks);
    var aetMessageContext = new AgentCommunicationContext(taskBound.Id,engineeringRunId,
        aetReceipts[2].WorkItemId,aetReceipts[2].Sequence,"SoftwareEngineer",
        aetReceipts[3].SourceSha256,approvedMessagePolicy.Revision,
        approvedMessagePolicy.PolicySha256,Guid.NewGuid().ToString("N"));
    var aetMessageDraft = new AgentCommunicationDraft("SolutionArchitect",
        "ClarificationRequested","Please clarify the approved storage approach.",
        EvidenceSequence: aetReceipts[3].Sequence);
    var aetSent = communicationStore.Publish(aetMessageContext,aetMessageDraft);
    Check(communicationStore.Publish(aetMessageContext,aetMessageDraft) == aetSent &&
          new AgentCommunicationStore(tasks).List(taskBound.Id,role:"SolutionArchitect",
              direction:"inbox").Single() == aetSent &&
          communicationStore.List(taskBound.Id,role:"SoftwareEngineer",
              direction:"outbox").Single() == aetSent,
        "durable inbox/outbox deduplicates identical host message and survives reopen");
    void RejectMessage(Action action, string expected)
    {
        try { action(); Check(false,"message should fail closed: " + expected); }
        catch (StoreFailure error) { Check(error.Code == expected,"message rejected: " + expected); }
    }
    RejectMessage(() => communicationStore.Publish(aetMessageContext,aetMessageDraft with { Summary = "Changed" }),
        "IDEMPOTENCY_CONFLICT");
    RejectMessage(() => communicationStore.Publish(aetMessageContext with {
        MessageId = Guid.NewGuid().ToString("N"), SourceSha256 = new string('0',64)
    },aetMessageDraft),"STALE_SOURCE_RUN");
    RejectMessage(() => communicationStore.Publish(aetMessageContext with {
        MessageId = Guid.NewGuid().ToString("N"), TaskId = new string('0',32)
    },aetMessageDraft),"NOT_FOUND");
    RejectMessage(() => communicationStore.Publish(aetMessageContext,aetMessageDraft with { Kind = "VerificationCompleted" }),
        "INVALID_AGENT_MESSAGE");
    var aetReplyContext = aetMessageContext with {
        FromAgent = "SolutionArchitect", MessageId = Guid.NewGuid().ToString("N")
    };
    var aetReply = communicationStore.Publish(aetReplyContext,new AgentCommunicationDraft(
        "SoftwareEngineer","ClarificationProvided","Follow the approved architecture.",aetSent.Sequence));
    Check(aetReply.Depth == 1 && aetReply.AssignmentSequence == aetSent.AssignmentSequence,
        "replies preserve assignment correlation and bounded depth");
    RejectMessage(() => communicationStore.Publish(aetMessageContext with {
        MessageId = Guid.NewGuid().ToString("N")
    },aetMessageDraft with { EvidenceSequence = long.MaxValue }),"INVALID_AGENT_MESSAGE");
    var aetLastReply = aetReply;
    for (var aetDepth = 2; aetDepth <= AgentCommunicationStore.MaxReplyDepth; aetDepth++)
    {
        aetLastReply = communicationStore.Publish(aetMessageContext with {
            FromAgent = aetLastReply.ToAgent, MessageId = Guid.NewGuid().ToString("N")
        },new AgentCommunicationDraft(aetLastReply.FromAgent,"ClarificationProvided",
            "Acknowledged the approved architecture.",aetLastReply.Sequence));
        Check(aetLastReply.Depth == aetDepth,"reply depth is computed by host");
    }
    RejectMessage(() => communicationStore.Publish(aetMessageContext with {
        FromAgent = aetLastReply.ToAgent, MessageId = Guid.NewGuid().ToString("N")
    },new AgentCommunicationDraft(aetLastReply.FromAgent,"ClarificationProvided",
        "Another reply.",aetLastReply.Sequence)),"AGENT_CONVERSATION_LIMIT");
    delegationStore.Revoke(taskBound.Id,new ApproveDelegationRequest(
        approvedMessagePolicy.Revision,approvedMessagePolicy.PolicySha256,
        "REVOKE " + approvedMessagePolicy.PolicySha256));
    RejectMessage(() => communicationStore.Publish(aetMessageContext with {
        MessageId = Guid.NewGuid().ToString("N")
    },aetMessageDraft),"DELEGATION_NOT_AUTHORIZED");
    using (var response = await client.GetAsync(
        "/api/tasks/" + taskBound.Id + "/agent-communications?role=SoftwareEngineer&direction=inbox"))
    using (var body = await Body(response))
        Check(response.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("messages").GetArrayLength() == 4,
            "authenticated agent inbox reports actual persisted replies");
    var aetProposalActivity = new EngineeringActivityStore(tasks).Read(taskBound.Id,
        aetActivity.LastSequence).Events.Where(e => e.Origin == "AgentProposal").ToArray();
    Check(aetProposalActivity.Length == 9 &&
          aetProposalActivity.Count(e => e.ReferenceId == aetSent.MessageId) == 1 &&
          aetProposalActivity.All(e => !e.Label.Contains("approved storage approach")),
        "activity distinguishes agent proposals from observations, deduplicates and omits raw text");
    // Exercise the observation boundary with injected metadata only. CI has no
    // real Ollama inference and must not claim provider or model qualification.
    var aetBeforeProvider = new EngineeringActivityStore(tasks).Read(taskBound.Id).LastSequence;
    Check(EngineeringProviderActivity.StartProvider("qwen2.5-coder:14b",
        MafAgentPurpose.SoftwareEngineer,CancellationToken.None) is null,
        "provider telemetry cannot attribute an unscoped request");
    using (EngineeringProviderActivity.Enter(tasks,taskBound.Id,"SoftwareEngineer",engineeringRunId))
    {
        Check(EngineeringProviderActivity.StartProvider("qwen2.5-coder:14b",
            MafAgentPurpose.QaEngineer,CancellationToken.None) is null,
            "provider telemetry rejects mismatched role attribution");
        using (var observed = EngineeringProviderActivity.StartProvider("qwen2.5-coder:14b",
            MafAgentPurpose.SoftwareEngineer,CancellationToken.None))
            observed!.Complete();
        using (var stopped = EngineeringProviderActivity.StartProvider("qwen2.5-coder:14b",
            MafAgentPurpose.SoftwareEngineer,CancellationToken.None)) { }
    }
    var aetProviderEvents = new EngineeringActivityStore(tasks).Read(taskBound.Id,
        aetBeforeProvider).Events;
    Check(aetProviderEvents.Select(e => e.Kind).SequenceEqual(
              ["OllamaRequestStarted","OllamaResponseAccepted",
               "OllamaRequestStarted","OllamaRequestStopped"]) &&
          aetProviderEvents.All(e => e.AgentId == "SoftwareEngineer" &&
              e.Model == "qwen2.5-coder:14b") &&
          aetProviderEvents[0].ReferenceId == aetProviderEvents[1].ReferenceId,
        "provider lifecycle metadata correlates start and result without model content");
    var aetLeaseStore=new InferenceLeaseStore(tasks);
    var aetLeaseHistory=aetLeaseStore.List(taskBound.Id);
    Check(aetLeaseHistory.Count==2 && aetLeaseHistory.Any(x=>x.State=="ResponseAccepted") &&
        aetLeaseHistory.Any(x=>x.State=="Stopped") &&
        aetLeaseHistory.All(x=>x.FinishedUtc is not null && x.ReferenceId==engineeringRunId),
        "actual scoped provider lifecycle has durable terminal lease receipts");
    using(EngineeringProviderActivity.Enter(tasks,taskBound.Id,"SoftwareEngineer",engineeringRunId))
    using(var abandoned=EngineeringProviderActivity.StartProvider("qwen2.5-coder:14b",
        MafAgentPurpose.SoftwareEngineer,CancellationToken.None))
    {
        var active=aetLeaseStore.List(taskBound.Id).Single(x=>x.State=="Active");
        new InferenceLeaseStore(tasks).Recover();
        new InferenceLeaseStore(tasks).Recover();
        try{abandoned!.Complete();Check(false,"interrupted lease cannot accept late result");}
        catch(StoreFailure e)when(e.Code=="INFERENCE_LEASE_INTERRUPTED")
        {Check(true,"late provider result is rejected after recovery");}
        Check(aetLeaseStore.List(taskBound.Id).Single(x=>x.Id==active.Id).State=="Interrupted",
            "startup reconciliation is idempotent and does not fabricate completion or replay work");
    }
    using(var aetLeaseResponse=await client.GetAsync("/api/tasks/"+taskBound.Id+"/inference-leases"))
    using(var aetLeaseBody=await Body(aetLeaseResponse))
        Check(aetLeaseResponse.StatusCode==HttpStatusCode.OK &&
            aetLeaseBody.RootElement.GetProperty("leases").GetArrayLength()==3,
            "owner can inspect recovered provider work receipts");
    // Persist a separate, read-only lead review against the exact generated revision.
    var aetLeadStore = new LeadReviewStore(tasks);
    var aetLeadPlan = new ImplementationPlanStore(tasks,interviews).Read(taskBound.Id).Plan!;
    var aetLeadSnapshot = engineeringStore.Get(taskBound.Id,engineeringRunId);
    var aetLeadJob = aetLeadStore.Start(taskBound.Id,aetLeadSnapshot,aetLeadPlan,"qwen3.5:4b");
    foreach(var aetLeadFile in aetLeadSnapshot.Files)
        aetLeadStore.Save(taskBound.Id,aetLeadJob.Id,aetLeadFile,
            new LeadFileAssessment("Approved","No static concerns in this injected fixture.",[]));
    aetLeadStore.Finish(taskBound.Id,aetLeadJob.Id,"Reviewed");
    var aetLeadSaved = new LeadReviewStore(tasks).Get(taskBound.Id,aetLeadJob.Id);
    Check(aetLeadSaved.Current && aetLeadSaved.Disposition=="Approved" &&
        aetLeadSaved.Reports.Count==aetLeadSnapshot.Files.Count &&
        QaReviewStore.Fingerprint(engineeringStore.Get(taskBound.Id,engineeringRunId).Files)==aetLeadJob.SourceSha256,
        "independent lead review persists exact revision without altering source");
    using(var aetLeadResponse=await client.GetAsync("/api/tasks/"+taskBound.Id+"/lead-reviews/latest"))
    using(var aetLeadBody=await Body(aetLeadResponse))
        Check(aetLeadResponse.StatusCode==HttpStatusCode.OK &&
            aetLeadBody.RootElement.GetProperty("snapshot").GetProperty("disposition").GetString()=="Approved",
            "owner can retrieve durable static lead review");
    var aetLeadPath = "/api/tasks/"+taskBound.Id+"/lead-reviews";
    using(var aetMissing=await Post(client,aetLeadPath,new StartQaReviewRequest(engineeringRunId,secondPlanHash)))
        Check(aetMissing.StatusCode==HttpStatusCode.ServiceUnavailable,
            "lead review cannot invoke an uninstalled default model");
    using(var aetSelect=await Post(client,"/api/tasks/"+taskBound.Id+"/agent-models/LeadSoftwareEngineer",
        new {model="qwen3.5:4b"}))
        Check(aetSelect.StatusCode==HttpStatusCode.OK,"owner may choose installed independent reviewer model");
    using(var aetStale=await Post(client,aetLeadPath,new StartQaReviewRequest(engineeringRunId,firstPlanHash)))
        Check(aetStale.StatusCode==HttpStatusCode.Conflict,"lead review rejects wrong approved plan digest");
    string aetLiveLeadId;
    using(var aetAdmitted=await Post(client,aetLeadPath,new StartQaReviewRequest(engineeringRunId,secondPlanHash)))
    using(var aetBody=await Body(aetAdmitted))
    {
        Check(aetAdmitted.StatusCode==HttpStatusCode.Accepted,"owner admits independent lead review through API");
        aetLiveLeadId=aetBody.RootElement.GetProperty("id").GetString()!;
    }
    LeadReviewSnapshot? aetLiveLead=null;
    for(int aetAttempt=0;aetAttempt<100;aetAttempt++)
    {
        aetLiveLead=aetLeadStore.Get(taskBound.Id,aetLiveLeadId);
        if(aetLiveLead.Job.State!="Running") break;
        await Task.Delay(50);
    }
    Check(aetLiveLead?.Job.State=="Reviewed" && aetLiveLead.Disposition=="ChangesRequested" &&
        aetLiveLead.Reports.SelectMany(x=>x.Assessment.Findings).Any(x=>x.Severity=="Major") &&
        QaReviewStore.Fingerprint(engineeringStore.Get(taskBound.Id,engineeringRunId).Files)==aetLeadJob.SourceSha256,
        "injected reviewer executes actual owner-admitted workflow, saves a concern and leaves source unchanged; state=" + aetLiveLead?.Job.State + ", error=" + aetLiveLead?.Job.ErrorCode);
    var aetIssues = new EngineeringIssueStore(tasks).Read(taskBound.Id);
    Check(aetIssues.CurrentSource && aetIssues.Issues.Count==1 &&
        aetIssues.Issues.Single().Origin=="LeadSoftwareEngineer" &&
        aetIssues.Issues.Single().State=="NeedsTriage" &&
        aetIssues.Issues.Single().SourceSha256==aetLeadJob.SourceSha256 &&
        aetIssues.Issues.Single().References.Single().ReviewId==aetLiveLeadId,
        "unified issue projection preserves actual independent finding and exact revision");
    using(var aetAnonymous=new HttpClient {BaseAddress=client.BaseAddress})
    using(var aetDenied=await aetAnonymous.GetAsync("/api/tasks/"+taskBound.Id+"/engineering-issues"))
        Check(aetDenied.StatusCode==HttpStatusCode.Unauthorized,"unified findings remain owner-private");
    var aetIssueId=aetIssues.Issues.Single().Id;
    var aetChallengePolicy=delegationStore.Propose(taskBound.Id,
        new ProposeDelegationRequest(2,secondPlanHash,["SendMessage","ScheduleWork"]));
    aetChallengePolicy=delegationStore.Approve(taskBound.Id,new ApproveDelegationRequest(
        aetChallengePolicy.Revision,aetChallengePolicy.PolicySha256,"APPROVE "+aetChallengePolicy.PolicySha256));
    var aetChallengeContext=aetMessageContext with {
        PolicyRevision=aetChallengePolicy.Revision,PolicySha256=aetChallengePolicy.PolicySha256,
        MessageId=Guid.NewGuid().ToString("N") };
    var aetArchitectureChoice=DraftArchitecturePolicy.Readiness(tasks,interviews,taskBound.Id)
        .ApprovedChoices.First().ProposalSha256;
    var aetChallengeDraft=new AgentCommunicationDraft("TechnicalLead","FindingChallenged",
        "Please reconcile this finding with the approved architecture.",
        IssueId:aetIssueId,ArchitectureChoiceSha256:aetArchitectureChoice);
    RejectMessage(()=>communicationStore.Publish(aetChallengeContext,
        aetChallengeDraft with {IssueId=new string('0',64)}),"INVALID_AGENT_MESSAGE");
    RejectMessage(()=>communicationStore.Publish(aetChallengeContext,
        aetChallengeDraft with {ArchitectureChoiceSha256=new string('0',64)}),"INVALID_AGENT_MESSAGE");
    RejectMessage(()=>communicationStore.Publish(aetChallengeContext with {FromAgent="QaEngineer"},
        aetChallengeDraft),"INVALID_AGENT_MESSAGE");
    var aetChallenge=communicationStore.Publish(aetChallengeContext,aetChallengeDraft);
    Check(communicationStore.Publish(aetChallengeContext,aetChallengeDraft)==aetChallenge,
        "identical issue challenge is idempotent");
    var aetChallenged=new EngineeringIssueStore(tasks).Read(taskBound.Id).Issues.Single();
    Check(aetChallenged.Id==aetIssueId && aetChallenged.State=="Challenged" &&
        aetChallenged.Severity=="Major" && aetChallenged.Challenges.Single().MessageId==aetChallenge.MessageId &&
        aetChallenged.Challenges.Single().ArchitectureChoiceSha256==aetArchitectureChoice,
        "engineer challenges actual finding against approved evidence without self-dismissal or downgrade");
    var aetDecisionContext=new IssueDecisionContext(taskBound.Id,engineeringRunId,aetLeadJob.SourceSha256,
        "TechnicalLead",aetChallengePolicy.Revision,aetChallengePolicy.PolicySha256);
    var aetDecisionDraft=new IssueDecisionDraft(aetIssueId,0,aetLiveLeadId,"ImplementationDefect",
        "The approved architecture does not excuse the observed defect.",aetChallenge.MessageId);
    var aetIssueStore=new EngineeringIssueStore(tasks);
    RejectMessage(()=>aetIssueStore.Decide(aetDecisionContext with {FromAgent="SoftwareEngineer"},
        aetDecisionDraft),"AGENT_CAPABILITY_DENIED");
    RejectMessage(()=>aetIssueStore.Decide(aetDecisionContext,
        aetDecisionDraft with {Classification="Deferred"}),"INVALID_INPUT");
    RejectMessage(()=>aetIssueStore.Decide(aetDecisionContext,
        aetDecisionDraft with {ChallengeMessageId=null}),"SOURCE_CHANGED");
    var aetDecision=aetIssueStore.Decide(aetDecisionContext,aetDecisionDraft);
    Check(aetIssueStore.Decide(aetDecisionContext,aetDecisionDraft)==aetDecision &&
        aetIssueStore.Read(taskBound.Id).Issues.Single() is {State:"ConfirmedDefect",AssignedOwner:"SoftwareEngineer"},
        "delegated Technical Lead arbitrates exact challenged finding and assigns repair ownership");
    RejectMessage(()=>aetIssueStore.Decide(aetDecisionContext,
        aetDecisionDraft with {Rationale="Conflicting decision cannot overwrite history."}),"IDEMPOTENCY_CONFLICT");
    delegationStore.Revoke(taskBound.Id,new ApproveDelegationRequest(aetChallengePolicy.Revision,
        aetChallengePolicy.PolicySha256,"REVOKE "+aetChallengePolicy.PolicySha256));
    // Both review origins use the original owner-confirmed repair gate.
    var aetLeadProposalPath="/api/tasks/"+taskBound.Id+"/repair-proposals";
    RepairProposal aetLeadProposal;
    using(var aetProposalResponse=await Post(client,aetLeadProposalPath,
        new CreateRepairProposalRequest(null,aetLiveLeadId)))
    using(var aetProposalBody=await Body(aetProposalResponse))
    {
        Check(aetProposalResponse.StatusCode==HttpStatusCode.Created &&
            aetProposalBody.RootElement.GetProperty("qaReviewId").ValueKind==JsonValueKind.Null &&
            aetProposalBody.RootElement.GetProperty("leadReviewId").GetString()==aetLiveLeadId,
            "lead repair proposal preserves independent origin without fabricating a QA review");
        aetLeadProposal=new RepairProposalStore(tasks).Latest(taskBound.Id)!;
    }
    using(var aetUntriaged=await Post(client,"/api/tasks/"+taskBound.Id+"/repair-cycles",
        new StartRepairCycleRequest(aetLeadProposal.Id,"REPAIR "+aetLeadProposal.SourceSha256,
            [new RepairSelection("app.js",0)])))
        Check(aetUntriaged.StatusCode==HttpStatusCode.Conflict,
            "Technical Lead arbitration alone does not bypass existing exact repair admission");
    using(var aetLeadTriage=await Post(client,aetLeadProposalPath+"/"+aetLeadProposal.Id+"/triage",
        new ClassifyRepairFindingRequest("app.js",0,"ImplementationDefect",
            "Owner confirms the independent source concern against approved scope.")))
        Check(aetLeadTriage.StatusCode==HttpStatusCode.OK,
            "existing owner triage accepts authentic Lead Software Engineer evidence");
    // Keep the generated source pinned to 14B but change QA alone.
    // A different selected QA model must NOT invalidate source/plan binding.
    using (var assignment = await Post(client,
        "/api/tasks/" + taskBound.Id + "/agent-models/QaEngineer",
        new { model = "qwen3.5:4b" }))
        Check(assignment.StatusCode == HttpStatusCode.OK,
            "owner can assign a distinct installed QA model before a new review");
    Check(softwareModel.AssignedIds.SequenceEqual(["timer-interface","timer-state"]) &&
          softwareModel.PreviousFileCountOnSecondTask == 1 &&
          engineeringResult!.Files.Single(f => f.Path == "app.js")
              .LastWorkItemId == "timer-state",
        "Software Engineer gets prior source and records last task ownership");

    // Read-only stack-aware source readiness follows the same owner/run ACL.
    var readinessPath = engineeringPath + "/" + engineeringRunId + "/source-readiness";
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var forbidden = await anonymous.GetAsync(readinessPath))
        Check(forbidden.StatusCode == HttpStatusCode.Unauthorized,
            "project source readiness requires owner authentication");
    using (var response = await client.GetAsync(readinessPath))
    using (var body = await Body(response))
        Check(response.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("runId").GetString() == engineeringRunId &&
              body.RootElement.GetProperty("executionAvailable").GetBoolean() == false &&
              body.RootElement.GetProperty("verificationStatus").GetString() ==
                  "GENERATED_NOT_TESTED" &&
              body.RootElement.GetProperty("projectKind").GetString() == "STATIC_WEB",
            "source-readiness reports saved run without invoking a test runner");
    using (var wrongRun = await client.GetAsync(
        engineeringPath + "/" + new string('0', 32) + "/source-readiness"))
        Check(wrongRun.StatusCode == HttpStatusCode.NotFound,
            "source-readiness cannot read a different run");
    using (var wrongTask = await client.GetAsync(
        "/api/tasks/" + new string('0', 32) + "/engineering-runs/" +
        engineeringRunId + "/source-readiness"))
        Check(wrongTask.StatusCode == HttpStatusCode.NotFound,
            "source-readiness cannot cross task boundary");

    // UX-6B HTTP: preflight is bounded to the requested task/run, requires
    // authentication and does not mutate source or perform execution.
    var preflightPath = engineeringPath + "/" + engineeringRunId + "/static-web-preflight";
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var forbidden = await anonymous.GetAsync(preflightPath))
        Check(forbidden.StatusCode == HttpStatusCode.Unauthorized,
            "static source preflight requires owner authentication");
    using (var preflight = await client.GetAsync(preflightPath))
    using (var body = await Body(preflight))
        Check(preflight.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("runId").GetString() == engineeringRunId &&
              body.RootElement.GetProperty("sourceSha256").GetString() ==
                  QaReviewStore.Fingerprint(engineeringResult!.Files) &&
              body.RootElement.GetProperty("status").GetString() == "NO_ISSUES_DETECTED" &&
              body.RootElement.GetProperty("verificationStatus").GetString() ==
                  "GENERATED_NOT_TESTED" &&
              body.RootElement.GetProperty("findings").GetArrayLength() == 0,
            "authenticated preflight scans the correct saved source without calling tests");
    using (var wrongRun = await client.GetAsync(
        engineeringPath + "/" + new string('0', 32) + "/static-web-preflight"))
        Check(wrongRun.StatusCode == HttpStatusCode.NotFound,
            "preflight never reads an unrelated or nonexistent engineering run");
    using (var wrongTask = await client.GetAsync(
        "/api/tasks/" + new string('0', 32) + "/engineering-runs/" +
        engineeringRunId + "/static-web-preflight"))
        Check(wrongTask.StatusCode == HttpStatusCode.NotFound,
            "preflight cannot mix task IDs or reuse another task's run");
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
          qaResult.Job.Model == "qwen3.5:4b" &&
          engineeringResult!.Job.Model == "qwen2.5-coder:14b" &&
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

    // R1 proposals are host-admitted evidence, never executable repair grants.
    var proposalPath = "/api/tasks/" + taskBound.Id + "/repair-proposals";
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var denied = await anonymous.GetAsync(proposalPath + "/latest"))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized,
            "repair proposals remain private to the authenticated owner");
    using (var invalid = await Post(client, proposalPath,
        new CreateRepairProposalRequest("not-an-id")))
        Check(invalid.StatusCode == HttpStatusCode.BadRequest,
            "malformed QA reference cannot be admitted");
    using (var wrongTask = await Post(client,
        "/api/tasks/" + task.Id + "/repair-proposals",
        new CreateRepairProposalRequest(qaReviewId)))
        Check(wrongTask.StatusCode == HttpStatusCode.NotFound,
            "QA evidence cannot be referenced from another task");
    string repairProposalId;
    using (var created = await Post(client, proposalPath,
        new CreateRepairProposalRequest(qaReviewId)))
    using (var body = await Body(created))
    {
        var repairJson = body.RootElement;
        repairProposalId = repairJson.GetProperty("id").GetString()!;
        var findings = repairJson.GetProperty("findings");
        Check(created.StatusCode == HttpStatusCode.Created &&
              repairJson.GetProperty("state").GetString() == "NeedsTriage" &&
              repairJson.GetProperty("qaReviewId").GetString() == qaReviewId &&
              repairJson.GetProperty("engineeringRunId").GetString() == engineeringRunId &&
              repairJson.GetProperty("sourceSha256").GetString() ==
                  qaResult!.Job.SourceSha256 &&
              findings.GetArrayLength() == 1 &&
              findings[0].GetProperty("classification").GetString() ==
                  "NeedsVerification" &&
              findings[0].GetProperty("path").GetString() == "app.js",
            "R1 captures exact approved source-bound QA finding as unverified");
    }
    using (var duplicate = await Post(client, proposalPath,
        new CreateRepairProposalRequest(qaReviewId)))
    using (var body = await Body(duplicate))
        Check(duplicate.StatusCode == HttpStatusCode.Created &&
              body.RootElement.GetProperty("id").GetString() == repairProposalId,
            "repeated proposal requests are idempotent");
    using (var latestProposal = await client.GetAsync(proposalPath + "/latest"))
    using (var body = await Body(latestProposal))
        Check(latestProposal.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("proposal")
                  .GetProperty("id").GetString() == repairProposalId,
            "R1 proposal survives a new SQLite read");

    // Host-owned classification is not authorization to repair or execute code.
    var triagePath = proposalPath + "/" + repairProposalId + "/triage";
    var classification = new ClassifyRepairFindingRequest(
        "app.js", 0, "NeedsVerification",
        "Static QA has not established a functional test failure");
    using (var invalidTriage = await Post(client, triagePath,
        new ClassifyRepairFindingRequest("not-a-file", 0,
            "ImplementationDefect", "No verified evidence exists for that file")))
        Check(invalidTriage.StatusCode == HttpStatusCode.BadRequest,
            "triage rejects finding identities absent from the immutable QA record");
    using (var unsafeTriage = await Post(client, triagePath,
        new ClassifyRepairFindingRequest("app.js", 0,
            "ExecuteAndRepair", "Attempt to authorize model execution directly")))
        Check(unsafeTriage.StatusCode == HttpStatusCode.BadRequest,
            "triage cannot become model execution authorization");
    using (var triaged = await Post(client, triagePath, classification))
    using (var body = await Body(triaged))
        Check(triaged.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("classification").GetString() ==
                  "NeedsVerification" &&
              body.RootElement.GetProperty("proposalId").GetString() ==
                  repairProposalId,
            "classification records a non-executing owner triage decision");
    using (var again = await Post(client, triagePath, classification))
        Check(again.StatusCode == HttpStatusCode.OK,
            "repeated identical owner classification is idempotent");
    using (var conflict = await Post(client, triagePath,
        classification with { Classification = "ImplementationDefect" }))
        Check(conflict.StatusCode == HttpStatusCode.Conflict,
            "append-only triage refuses conflicting reclassification");
    using (var decisions = await client.GetAsync(triagePath))
    using (var body = await Body(decisions))
        Check(decisions.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("decisions").GetArrayLength() == 1,
            "triage decision is durably readable without source mutation");
    var aetCombined=new EngineeringIssueStore(tasks).Read(taskBound.Id);
    Check(aetCombined.Issues.Count==2 &&
        aetCombined.Issues.Single(x=>x.Origin=="QaEngineer").Classification=="NeedsVerification" &&
        aetCombined.Issues.Single(x=>x.Origin=="QaEngineer").TriageHistory.Single().ReviewId==qaReviewId &&
        aetCombined.Issues.Single(x=>x.Origin=="LeadSoftwareEngineer").State=="ConfirmedDefect",
        "unified issue view combines independent origins and actual owner triage");

    // R2-R5 end-to-end fixture: explicit triage, authorized source-only
    // repair, preservation of original files, and independent QA reassessment.
    var latestRepairPath = "/api/tasks/" + taskBound.Id + "/repair-cycles";
    var repairProposal = new RepairProposalStore(tasks);
    var boundProposal = repairProposal.Latest(taskBound.Id)!;
    using (var premature = await Post(client, latestRepairPath,
        new StartRepairCycleRequest(boundProposal.Id,
            "REPAIR " + boundProposal.SourceSha256,
            [new RepairSelection("app.js", 0)])))
        Check(premature.StatusCode == HttpStatusCode.Conflict,
            "unconfirmed QA hypothesis cannot authorize Software Engineer repair");
    using (var classified = await Post(client, triagePath,
        new ClassifyRepairFindingRequest("app.js", 0,
            "ImplementationDefect",
            "Owner confirms this finding contradicts approved application behavior")))
        Check(classified.StatusCode == HttpStatusCode.Conflict,
            "existing append-only triage decision cannot silently be overwritten");

    // This separate QA fixture needs a fresh review/triage decision because
    // the first recorded decision was deliberately NeedsVerification.
    string freshQaId;
    using (var refreshedQa = await Post(client, qaPath,
        new StartQaReviewRequest(engineeringRunId, secondPlanHash)))
    using (var body = await Body(refreshedQa))
    {
        Check(refreshedQa.StatusCode == HttpStatusCode.Accepted,
            "owner can request a fresh independent QA review");
        freshQaId = body.RootElement.GetProperty("id").GetString()!;
    }
    QaReviewSnapshot? currentQa = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        currentQa = reviewStore.Get(taskBound.Id, freshQaId);
        if (currentQa.Job.State != "Running") break;
        await Task.Delay(25);
    }
    Check(currentQa?.Job.State == "Reviewed",
        "fresh independent QA report completed before repairs");
    using (var createdProposal = await Post(client, proposalPath,
        new CreateRepairProposalRequest(currentQa!.Job.Id)))
        Check(createdProposal.StatusCode == HttpStatusCode.Created,
            "fresh QA evidence can create a distinct repair proposal");
    var selectedRepairProposal = repairProposal.Latest(taskBound.Id)!;
    var newTriagePath = proposalPath + "/" + selectedRepairProposal.Id + "/triage";
    using (var ownerConfirmed = await Post(client, newTriagePath,
        new ClassifyRepairFindingRequest("app.js", 0, "ImplementationDefect",
            "Owner explicitly confirms Start handler defect against specification")))
        Check(ownerConfirmed.StatusCode == HttpStatusCode.OK,
            "owner can confirm and classify one source-bound defect");
    var aetRepeatIssue=new EngineeringIssueStore(tasks).Read(taskBound.Id).Issues.Single(x=>x.Origin=="QaEngineer");
    Check(aetRepeatIssue.References.Count==2 && aetRepeatIssue.State=="TriageConflict" &&
        aetRepeatIssue.TriageHistory.Count==2,
        "identical repeated findings deduplicate but conflicting decisions remain visible");

    var authorized = new StartRepairCycleRequest(selectedRepairProposal.Id,
        "REPAIR " + selectedRepairProposal.SourceSha256,
        [new RepairSelection("app.js", 0)]);
    string cycleId;
    using (var started = await Post(client, latestRepairPath, authorized))
    using (var body = await Body(started))
    {
        Check(started.StatusCode == HttpStatusCode.Accepted &&
              body.RootElement.GetProperty("attempt").GetInt32() == 1 &&
              body.RootElement.GetProperty("state").GetString() == "Running",
            "explicitly classified finding starts bounded source-only repair");
        cycleId = body.RootElement.GetProperty("id").GetString()!;
    }
    using (var replay = await Post(client, latestRepairPath, authorized))
        Check(replay.StatusCode == HttpStatusCode.Conflict,
            "same QA report cannot start a second overlapping repair");
    RepairCycleSnapshot? repairOutcome = null;
    for (var attempt = 0; attempt < 240; attempt++)
    {
        using var response = await client.GetAsync(latestRepairPath + "/" + cycleId);
        using var body = await Body(response);
        if (body.RootElement.GetProperty("cycle").GetProperty("state")
            .GetString() is "Running" or "Generated" or "QaRunning")
        {
            await Task.Delay(25);
            continue;
        }
        var cycleStore = new RepairCycleStore(tasks, engineeringStore,
            reviewStore, repairProposal);
        repairOutcome = cycleStore.Get(taskBound.Id, cycleId);
        break;
    }
    Check(repairOutcome?.Cycle.State == "QaReviewed" &&
          repairOutcome.Review?.Job.State == "Reviewed" &&
          repairOutcome.Engineering.Job.State == "Generated" &&
          repairOutcome.Cycle.BaseSourceSha256 ==
              QaReviewStore.Fingerprint(engineeringResult!.Files) &&
          repairOutcome.Engineering.Files.Single(f => f.Path == "index.html")
              .Sha256 == engineeringResult.Files.Single(f => f.Path == "index.html")
                  .Sha256 &&
          repairOutcome.Engineering.Files.Single(f => f.Path == "app.js")
              .Sha256 != engineeringResult.Files.Single(f => f.Path == "app.js")
                  .Sha256 &&
          repairOutcome.StaticPreflight.VerificationStatus ==
              "GENERATED_NOT_TESTED" &&
          qaModel.RepairFocusPaths.SequenceEqual(["app.js"]),
        "repaired source is distinct, original evidence immutable, QA independently rechecks owner-confirmed defect and no fake PASS");
    Check(engineeringStore.Get(taskBound.Id, engineeringRunId).Files
              .Single(f => f.Path == "app.js").Sha256 ==
          engineeringResult!.Files.Single(f => f.Path == "app.js").Sha256,
        "prior engineering run is unchanged after source-only repair");
    var aetHistoricalIssues=new EngineeringIssueStore(tasks).Read(taskBound.Id,engineeringRunId);
    var aetRepairedIssue=aetHistoricalIssues.Issues.Single(x=>x.Origin=="QaEngineer");
    Check(!aetHistoricalIssues.CurrentSource && aetRepairedIssue.Id==aetRepeatIssue.Id &&
        aetRepairedIssue.RepairCycleId==cycleId && aetRepairedIssue.RepairAttempt==1 &&
        aetRepairedIssue.AssignedOwner=="SoftwareEngineer" &&
        aetRepairedIssue.State=="AwaitingIndependentVerification",
        "historical issue remains unclosed even after independent static repair review");
    Check(new EngineeringIssueStore(tasks).Read(taskBound.Id).Issues.Any(x=>x.Origin=="QaEngineer"),
        "recurring issue remains visible on repaired source");
    var aetVerificationStore=new IssueVerificationStore(tasks);
    var aetVerifyRequest=new VerifyEngineeringIssueRequest(engineeringRunId,aetRepairedIssue.Id,
        repairOutcome!.Engineering.Job.Id,QaReviewStore.Fingerprint(repairOutcome.Engineering.Files));
    var aetVerifyInput=aetVerificationStore.Prepare(taskBound.Id,aetVerifyRequest);
    RejectMessage(()=>aetVerificationStore.Record(taskBound.Id,aetVerifyInput,
        "SoftwareEngineer","qwen3.5:4b",new IssueVerificationAssessment("StillPresent",
            "This is an unauthorized self-verification attempt.",1,"console.log",null)),
        "AGENT_CAPABILITY_DENIED");
    RejectMessage(()=>aetVerificationStore.Record(taskBound.Id,aetVerifyInput,
        "QaEngineer","qwen3.5:4b",new IssueVerificationAssessment("StillPresent",
            "This source citation was invented by the fixture.",999,"not present",null)),
        "MODEL_OUTPUT_REJECTED");
    var aetStaticResult=aetVerificationStore.Record(taskBound.Id,aetVerifyInput,
        "QaEngineer","qwen3.5:4b",new IssueVerificationAssessment("ResolvedByStaticEvidence",
            "Injected source observation does not establish functional behavior.",1,"console.log",null));
    var aetAfterRecheck=new EngineeringIssueStore(tasks).Read(taskBound.Id,engineeringRunId)
        .Issues.Single(x=>x.Id==aetRepairedIssue.Id);
    Check(aetAfterRecheck.State=="AwaitingQualifiedVerification" &&
        aetAfterRecheck.Verifications.Single().Id==aetStaticResult.Id &&
        aetAfterRecheck.Verifications.Single().Current,
        "explicit independent source recheck cannot close a functional concern or fabricate runtime verification");
    using(var aetVerifyAnonymous=new HttpClient {BaseAddress=client.BaseAddress})
    using(var aetDeniedVerify=await Post(aetVerifyAnonymous,"/api/tasks/"+taskBound.Id+"/engineering-issues/verify",aetVerifyRequest))
        Check(aetDeniedVerify.StatusCode==HttpStatusCode.Unauthorized,
            "independent recheck endpoint requires authenticated owner");
    using(var aetVerifyResponse=await Post(client,"/api/tasks/"+taskBound.Id+"/engineering-issues/verify",aetVerifyRequest))
    using(var aetVerifyBody=await Body(aetVerifyResponse))
        Check(aetVerifyResponse.StatusCode==HttpStatusCode.OK &&
            aetVerifyBody.RootElement.GetProperty("agentId").GetString()=="QaEngineer" &&
            aetVerifyBody.RootElement.GetProperty("assessment").GetProperty("outcome").GetString()=="StillPresent",
            "host selects the originating independent reviewer; injected model recheck is persisted");
    Check(new EngineeringIssueStore(tasks).Read(taskBound.Id,engineeringRunId).Issues
        .Single(x=>x.Id==aetRepairedIssue.Id) is {State:"StillPresent",Verifications.Count:2},
        "explicit repeated independent verification retains evidence and reopens the issue");

    // The QA proposal and its populated triage/cycle children must survive
    // the old mandatory-QA foreign key migration. Work only in a DB copy.
    var aetMigrationRoot=Path.Combine(root,"aet-legacy-proposals");
    Directory.CreateDirectory(aetMigrationRoot);
    if(!OperatingSystem.IsWindows())
        File.SetUnixFileMode(aetMigrationRoot,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    var aetMigrationPath=Path.Combine(aetMigrationRoot,"codingassistant.db");
    using(var aetOriginalDb=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(root,"codingassistant.db")+";Pooling=False"))
    using(var aetLegacyDb=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+aetMigrationPath+";Pooling=False"))
    {
        aetOriginalDb.Open();aetLegacyDb.Open();
        aetOriginalDb.BackupDatabase(aetLegacyDb);
        using(var aetForeignKeys=aetLegacyDb.CreateCommand())
        {aetForeignKeys.CommandText="PRAGMA foreign_keys=OFF";aetForeignKeys.ExecuteNonQuery();}
        using var aetLegacyTx=aetLegacyDb.BeginTransaction();
        using(var aetLegacySchema=aetLegacyDb.CreateCommand())
        {
            aetLegacySchema.Transaction=aetLegacyTx;
            aetLegacySchema.CommandText="""
                DELETE FROM repair_triage_decisions WHERE proposal_id IN
                    (SELECT id FROM repair_proposals WHERE lead_review_id IS NOT NULL);
                DELETE FROM repair_proposals WHERE lead_review_id IS NOT NULL;
                CREATE TABLE repair_proposals_old_fixture(
                    id TEXT PRIMARY KEY NOT NULL,task_id TEXT NOT NULL REFERENCES tasks(id),
                    qa_review_id TEXT NOT NULL UNIQUE REFERENCES qa_reviews(id),
                    engineering_run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                    source_sha256 TEXT NOT NULL,plan_revision INTEGER NOT NULL,plan_sha256 TEXT NOT NULL,
                    spec_version INTEGER NOT NULL,spec_sha256 TEXT NOT NULL,architecture_sha256 TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state='NeedsTriage'),findings_json TEXT NOT NULL,created_utc TEXT NOT NULL
                );
                INSERT INTO repair_proposals_old_fixture
                    SELECT id,task_id,qa_review_id,engineering_run_id,source_sha256,plan_revision,plan_sha256,
                        spec_version,spec_sha256,architecture_sha256,state,findings_json,created_utc FROM repair_proposals;
                DROP TABLE repair_proposals;
                ALTER TABLE repair_proposals_old_fixture RENAME TO repair_proposals;
                CREATE INDEX repair_proposal_by_task ON repair_proposals(task_id,created_utc DESC,id DESC);
                """;
            aetLegacySchema.ExecuteNonQuery();
        }
        aetLegacyTx.Commit();
    }
    var aetMigratedTasks=new TaskStore(aetMigrationRoot);
    var aetMigratedProposals=new RepairProposalStore(aetMigratedTasks);
    aetMigratedProposals.Initialize();
    aetMigratedProposals.Initialize();
    var aetMigratedProposal=aetMigratedProposals.Latest(taskBound.Id);
    Check(aetMigratedProposal?.Id==selectedRepairProposal.Id &&
        aetMigratedProposal.QaReviewId==selectedRepairProposal.QaReviewId &&
        aetMigratedProposal.LeadReviewId is null &&
        new RepairTriageStore(aetMigratedTasks).List(taskBound.Id,selectedRepairProposal.Id).Count==1,
        "populated QA-only proposal migration preserves exact proposal and child triage");
    using(var aetMigratedDb=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+aetMigrationPath+";Pooling=False"))
    {
        aetMigratedDb.Open();
        using var aetCheck=aetMigratedDb.CreateCommand();
        aetCheck.CommandText="PRAGMA foreign_key_check";
        using(var aetRows=aetCheck.ExecuteReader())
            Check(!aetRows.Read(),"legacy proposal migration preserves foreign key integrity");
        aetCheck.CommandText="SELECT proposal_id FROM repair_cycles WHERE id=@id";
        aetCheck.Parameters.AddWithValue("@id",cycleId);
        Check((string?)aetCheck.ExecuteScalar()==selectedRepairProposal.Id,
            "legacy proposal migration preserves populated repair cycle reference");
    }

    // A real Windows installation can already contain 1-3 approved repairs.
    // Exercise in-place migration of its populated SQLite CHECK constraint,
    // including preservation of all original cycle metadata and source refs.
    var repairDatabasePath = Path.Combine(root, "codingassistant.db");
    using (var oldDb = new Microsoft.Data.Sqlite.SqliteConnection(
        "Data Source=" + repairDatabasePath))
    {
        oldDb.Open();
        using var migration = oldDb.BeginTransaction();
        using (var old = oldDb.CreateCommand())
        {
            old.Transaction = migration;
            old.CommandText = """
                CREATE TABLE repair_cycles_old_fixture (
                    id TEXT PRIMARY KEY NOT NULL,
                    task_id TEXT NOT NULL REFERENCES tasks(id),
                    proposal_id TEXT NOT NULL UNIQUE REFERENCES repair_proposals(id),
                    base_run_id TEXT NOT NULL REFERENCES engineering_runs(id),
                    engineering_run_id TEXT NOT NULL UNIQUE REFERENCES engineering_runs(id),
                    qa_review_id TEXT REFERENCES qa_reviews(id),
                    base_source_sha256 TEXT NOT NULL,
                    work_item_id TEXT NOT NULL,
                    state TEXT NOT NULL CHECK(state IN
                        ('Running','Generated','QaRunning','QaReviewed','Failed','Interrupted')),
                    attempt INTEGER NOT NULL CHECK(attempt BETWEEN 1 AND 3),
                    error_code TEXT,
                    findings_json TEXT NOT NULL,
                    created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL
                );
                INSERT INTO repair_cycles_old_fixture
                SELECT * FROM repair_cycles;
                DROP TABLE repair_cycles;
                ALTER TABLE repair_cycles_old_fixture RENAME TO repair_cycles;
                CREATE INDEX repair_cycles_latest
                    ON repair_cycles(task_id,created_utc DESC,id DESC);
                """;
            _ = old.ExecuteNonQuery();
        }
        migration.Commit();
    }
    var upgradedRepairs = new RepairCycleStore(tasks, engineeringStore,
        reviewStore, repairProposal);
    upgradedRepairs.Initialize();
    var keptCycle = upgradedRepairs.Get(taskBound.Id, cycleId);
    using (var inspectedDb = new Microsoft.Data.Sqlite.SqliteConnection(
        "Data Source=" + repairDatabasePath))
    {
        inspectedDb.Open();
        using var schemaCheck = inspectedDb.CreateCommand();
        schemaCheck.CommandText = """
            SELECT sql FROM sqlite_master
            WHERE type='table' AND name='repair_cycles'
            """;
        var newSchema = (string?)schemaCheck.ExecuteScalar() ?? "";
        Check(keptCycle.Cycle.Id == cycleId &&
              keptCycle.Cycle.Attempt == 1 &&
              keptCycle.Cycle.ProposalId == selectedRepairProposal.Id &&
              keptCycle.Cycle.BaseSourceSha256 ==
                  repairOutcome!.Cycle.BaseSourceSha256 &&
              keptCycle.Engineering.Files.Count == repairOutcome.Engineering.Files.Count &&
              newSchema.Contains("attempt >= 1", StringComparison.OrdinalIgnoreCase) &&
              !newSchema.Contains("BETWEEN 1 AND 3", StringComparison.OrdinalIgnoreCase),
            "legacy populated three-cycle table migrates atomically with evidence intact and no project-wide limit");
    }
    upgradedRepairs.Initialize();
    Check(upgradedRepairs.Get(taskBound.Id, cycleId).Cycle.Attempt == 1,
        "repair schema migration is idempotent on repeated app startup");

    using (var cycleList = await client.GetAsync(latestRepairPath + "/latest"))
    using (var body = await Body(cycleList))
        Check(cycleList.StatusCode == HttpStatusCode.OK &&
              body.RootElement.GetProperty("snapshot")
                  .GetProperty("cycle").GetProperty("id").GetString() == cycleId,
            "repair cycle persists and appears in Mission Control status API");

    // Real FlowBoard failure: the first source file survives but a later
    // engineering task fails. Static QA must accept only the immutable saved
    // partial artifact, and the status must remain clearly incomplete.
    Check(QaReviewStore.IsReviewableSource("Generated", 1) &&
          QaReviewStore.IsReviewableSource("Failed", 1) &&
          QaReviewStore.IsReviewableSource("Interrupted", 1) &&
          !QaReviewStore.IsReviewableSource("Running", 1) &&
          !QaReviewStore.IsReviewableSource("Failed", 0),
        "only terminal source with saved files qualifies for static QA");

    var approvedEngineerPlan =
        new ImplementationPlanStore(tasks, interviews).Read(taskBound.Id).Plan!;
    var failedEngineerRun = engineeringStore.Start(taskBound.Id, approvedEngineerPlan);
    engineeringStore.StartTask(taskBound.Id, failedEngineerRun.Id, "timer-interface");
    engineeringStore.CompleteTask(taskBound.Id, failedEngineerRun.Id, "timer-interface",
        new EngineeringChangeSet(
            "Saved the browser entry before later implementation failed.",
            [new EngineeringFileChange("index.html", "Offline browser entry point",
                "<!doctype html><html><body><button id='start'>Start</button></body></html>")],
            ["Click Start later to verify the behavior"]));
    using (var deniedActive = await Post(client, qaPath,
        new StartQaReviewRequest(failedEngineerRun.Id, secondPlanHash)))
        Check(deniedActive.StatusCode == HttpStatusCode.Conflict &&
              qaModel.ReviewedFiles.Count == 6,
            "QA cannot read an active engineering job even when it saved source");
    engineeringStore.StartTask(taskBound.Id, failedEngineerRun.Id, "timer-state");
    // Persist only a bounded response-failure category, never returned text.
    engineeringStore.RecordOutputRejection(taskBound.Id, failedEngineerRun.Id,
        "timer-state", new EngineeringOutputRejection(1, "JSON_SYNTAX"));
    engineeringStore.RecordOutputRejection(taskBound.Id, failedEngineerRun.Id,
        "timer-state", new EngineeringOutputRejection(2, "MODEL_THINKING_ONLY"));
    var rejectedLedger = engineeringStore.Get(taskBound.Id, failedEngineerRun.Id);
    Check(rejectedLedger.Events.Count(e => e.Kind == "EngineerOutputRejected") == 2 &&
          rejectedLedger.Events.Any(e => e.Detail.Contains("timer-state: attempt 2/3, category MODEL_THINKING_ONLY",
              StringComparison.Ordinal)) &&
          rejectedLedger.Events.All(e => !e.Detail.Contains("SECRET_SOURCE_TEXT",
              StringComparison.Ordinal)),
        "each rejected model response produces a task-scoped content-free receipt");
    foreach (var invalid in new[] {
        new EngineeringOutputRejection(3, "SECRET_SOURCE_TEXT"),
        new EngineeringOutputRejection(1, "JSON_SYNTAX")
    })
    {
        try
        {
            engineeringStore.RecordOutputRejection(taskBound.Id,
                failedEngineerRun.Id, "timer-state", invalid);
            Check(false, "untrusted diagnostic category or duplicate attempt persisted");
        }
        catch (StoreFailure error) when (
            error.Code is "INVALID_INPUT" or "INVALID_STATE")
        {
            Check(true, "diagnostics require allowlisted category and next attempt number");
        }
    }
    // M8.16: only a running, approved engineering task may persist host
    // observations. Stored metadata never contains a model query or source.
    var savedForAtlas = engineeringStore.Get(taskBound.Id, failedEngineerRun.Id);
    var hostObservation = AgentAtlasInspectionGateway.Execute(
        approvedEngineerPlan.PlanSha256, savedForAtlas.Files,
        new AtlasInspectionRequest("atlas.find_symbols", "index"));
    engineeringStore.RecordAtlasInspection(
        taskBound.Id, failedEngineerRun.Id, "timer-state",
        new AtlasInspectionObservation(hostObservation.Name,
            hostObservation.SourceRevision, hostObservation.ResultCount));
    var inspectionLedger = engineeringStore.Get(
        taskBound.Id, failedEngineerRun.Id);
    Check(inspectionLedger.Events.Any(e =>
        e.Kind == "EngineerAtlasInspected" &&
        e.Detail.Contains("atlas.find_symbols", StringComparison.Ordinal) &&
        e.Detail.Contains(hostObservation.SourceRevision[..12],
            StringComparison.Ordinal) &&
        !e.Detail.Contains("index", StringComparison.OrdinalIgnoreCase)),
        "host-approved agent tool activity appears as private source-revision evidence");
    try
    {
        engineeringStore.RecordAtlasInspection(taskBound.Id,
            failedEngineerRun.Id, "timer-interface",
            new AtlasInspectionObservation(hostObservation.Name,
                hostObservation.SourceRevision, hostObservation.ResultCount));
        Check(false, "finished task admitted an unrelated inspection");
    }
    catch (StoreFailure error) when (error.Code == "INVALID_STATE")
    {
        Check(true, "completed work items cannot forge later tool activity");
    }
    try
    {
        engineeringStore.RecordAtlasInspection(taskBound.Id,
            failedEngineerRun.Id, "timer-state",
            new AtlasInspectionObservation("shell.run",
                hostObservation.SourceRevision, 1));
        Check(false, "unlisted agent operation persisted");
    }
    catch (StoreFailure error) when (error.Code == "INVALID_INPUT")
    {
        Check(true, "tool ledger rejects operations outside approved read-only scope");
    }
    engineeringStore.Finish(taskBound.Id, failedEngineerRun.Id, false, "MODEL_OUTPUT_REJECTED");
    var partialSource = engineeringStore.Get(taskBound.Id, failedEngineerRun.Id);
    Check(partialSource.Job.State == "Failed" &&
          partialSource.Files.Count == 1 &&
          partialSource.Tasks[0].State == "Completed" &&
          partialSource.Tasks[1].State == "Failed",
        "failed engineering job retains first file with correct task statuses");
    // M8.18: exact run/plan/source review admission, durable QA tool audit,
    // per-file tool budget and no acceptance of fabricated source revisions.
    var probeReview = reviewStore.Start(taskBound.Id, partialSource,
        approvedEngineerPlan);
    var probeSource = partialSource.Files.Single();
    var probeRevision = CodeAtlasIndexer.Index(
        partialSource.Job.Id, partialSource.Files).SourceRevision;
    var probeObservation = new AtlasInspectionObservation(
        "atlas.find_symbols", probeRevision, 1);
    reviewStore.RecordAtlasInspection(taskBound.Id, probeReview.Id,
        probeSource, probeObservation);
    var probeLedger = reviewStore.Get(taskBound.Id, probeReview.Id);
    Check(probeLedger.Events.Any(e => e.Kind == "QaAtlasInspected" &&
          e.Detail.Contains("atlas.find_symbols", StringComparison.Ordinal) &&
          e.Detail.Contains(probeRevision[..12], StringComparison.Ordinal) &&
          !e.Detail.Contains(probeSource.Content,
              StringComparison.Ordinal)),
        "QA source investigation persisted as host-only metadata, not raw source");
    foreach (var bad in new[] {
        new AtlasInspectionObservation("shell.run", probeRevision, 1),
        new AtlasInspectionObservation("atlas.find_symbols", new string('f', 64), 1)
    })
    {
        try
        {
            reviewStore.RecordAtlasInspection(taskBound.Id,
                probeReview.Id, probeSource, bad);
            Check(false, "QA audit accepted a fabricated operation or revision");
        }
        catch (StoreFailure e) when (
            e.Code is "INVALID_INPUT" or "SOURCE_CHANGED")
        {
            Check(true, "QA audit rejects forged tool evidence");
        }
    }
    reviewStore.RecordAtlasInspection(taskBound.Id, probeReview.Id,
        probeSource, probeObservation);
    try
    {
        reviewStore.RecordAtlasInspection(taskBound.Id, probeReview.Id,
            probeSource, probeObservation);
        Check(false, "QA audit must enforce per-file tool budget");
    }
    catch (StoreFailure e) when (e.Code == "ATLAS_TOOL_LIMIT")
    {
        Check(true, "QA audit enforces two observations per reviewed file");
    }
    reviewStore.Finish(taskBound.Id, probeReview.Id, false, "TEST_PROBE");
    try
    {
        reviewStore.RecordAtlasInspection(taskBound.Id, probeReview.Id,
            probeSource, probeObservation);
        Check(false, "QA audit accepted an observation after review completion");
    }
    catch (StoreFailure e) when (e.Code == "INVALID_STATE")
    {
        Check(true, "finished QA reviews cannot add new investigation evidence");
    }

    using (var zipResponse = await client.GetAsync(
        engineeringPath + "/" + failedEngineerRun.Id + "/zip"))
    {
        var data = await zipResponse.Content.ReadAsByteArrayAsync();
        using var archive = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read);
        Check(zipResponse.StatusCode == HttpStatusCode.OK &&
              archive.Entries.Count == 1 &&
              archive.Entries[0].FullName == "index.html" &&
              (zipResponse.Content.Headers.ContentDisposition?.FileName ?? "")
                  .Contains("PARTIAL", StringComparison.Ordinal),
            "failed engineering job exports labeled partial ZIP, never complete ZIP");
    }
    string partialReviewId;
    using (var admittedPartial = await Post(client, qaPath,
        new StartQaReviewRequest(failedEngineerRun.Id, secondPlanHash)))
    using (var body = await Body(admittedPartial))
    {
        Check(admittedPartial.StatusCode == HttpStatusCode.Accepted,
            "owner can ask QA to inspect saved Failed source");
        partialReviewId = body.RootElement.GetProperty("id").GetString()!;
    }
    QaReviewSnapshot? partialReport = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        using var response = await client.GetAsync(qaPath + "/" + partialReviewId);
        using var body = await Body(response);
        if (body.RootElement.GetProperty("job").GetProperty("state")
                .GetString() == "Running")
        {
            await Task.Delay(25);
            continue;
        }
        partialReport = reviewStore.Get(taskBound.Id, partialReviewId);
        break;
    }
    Check(partialReport?.Job.State == "Reviewed" &&
          partialReport.Events.Any(e =>
              e.Kind == "QaReceivedEngineerInspectionEvidence" &&
              e.Detail.Contains("1 saved source-inspection",
                  StringComparison.Ordinal) &&
              e.Detail.Contains("no build or tests executed",
                  StringComparison.Ordinal)) &&
          partialReport.SourceState == "Failed" &&
          partialReport.Reports.Count == 1 &&
          partialReport.Reports[0].Path == "index.html" &&
          partialReport.Events.Any(e => e.Detail.Contains("PARTIAL")) &&
          qaModel.ReviewedSourceStates.Last() == "Failed",
        "partial Failed source inspected without claiming a completed build");

    var interrupted = engineeringStore.Start(taskBound.Id, approvedEngineerPlan);
    engineeringStore.StartTask(taskBound.Id, interrupted.Id, "timer-interface");
    engineeringStore.CompleteTask(taskBound.Id, interrupted.Id, "timer-interface",
        new EngineeringChangeSet(
            "Saved a browser entry before the owner interrupted engineering.",
            [new EngineeringFileChange("index.html", "Offline timer browser entry",
                "<!doctype html><html><body><button id='start'>Start</button></body></html>")],
            ["Review the incomplete timer in a browser later"]));
    engineeringStore.Interrupt(taskBound.Id, interrupted.Id);
    string interruptReviewId;
    using (var accepted = await Post(client, qaPath,
        new StartQaReviewRequest(interrupted.Id, secondPlanHash)))
    using (var body = await Body(accepted))
    {
        Check(accepted.StatusCode == HttpStatusCode.Accepted,
            "owner can ask QA to review Interrupted source with saved files");
        interruptReviewId = body.RootElement.GetProperty("id").GetString()!;
    }
    QaReviewSnapshot? interruptedReport = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        using var response = await client.GetAsync(qaPath + "/" + interruptReviewId);
        using var body = await Body(response);
        if (body.RootElement.GetProperty("job").GetProperty("state")
                .GetString() == "Running")
        {
            await Task.Delay(25);
            continue;
        }
        interruptedReport = reviewStore.Get(taskBound.Id, interruptReviewId);
        break;
    }
    Check(interruptedReport?.Job.State == "Reviewed" &&
          interruptedReport.SourceState == "Interrupted" &&
          interruptedReport.Reports.Count == 1 &&
          qaModel.ReviewedSourceStates.Last() == "Interrupted",
        "Interrupted saved source receives a clearly labeled partial review");

    // A Windows launcher/API restart must not silently truncate the event
    // ledger after the final completed assignment. Both the terminal code and
    // event are committed atomically, while already saved source survives.
    var recovered = engineeringStore.Start(taskBound.Id, approvedEngineerPlan);
    engineeringStore.StartTask(taskBound.Id, recovered.Id, "timer-interface");
    engineeringStore.CompleteTask(taskBound.Id, recovered.Id, "timer-interface",
        new EngineeringChangeSet("Saved a first file before restart.",
            [new EngineeringFileChange("index.html", "Offline browser entry point",
                "<!doctype html><html><body>Saved before restart</body></html>")],
            ["Not executed"]));
    engineeringStore.StartTask(taskBound.Id, recovered.Id, "timer-state");
    var eventsBeforeRestart = engineeringStore.Get(taskBound.Id, recovered.Id).Events.Count;
    engineeringStore.MarkInterruptedOnStartup();
    var restartSnapshot = engineeringStore.Get(taskBound.Id, recovered.Id);
    Check(restartSnapshot.Job.State == "Interrupted" &&
          restartSnapshot.Job.ErrorCode == "PROCESS_INTERRUPTED" &&
          restartSnapshot.Events.Count == eventsBeforeRestart + 1 &&
          restartSnapshot.Events[^1].Sequence == eventsBeforeRestart + 1 &&
          restartSnapshot.Events[^1].Kind == "EngineeringRunInterrupted" &&
          restartSnapshot.Events[^1].Detail.Contains("PROCESS_INTERRUPTED",
              StringComparison.Ordinal) &&
          restartSnapshot.Tasks.Count == 2 &&
          restartSnapshot.Tasks[0].State == "Completed" &&
          restartSnapshot.Tasks[1].State == "Interrupted" &&
          restartSnapshot.Files.Count == 1 &&
          restartSnapshot.Files[0].Path == "index.html",
        "restart interruption persists one terminal event and preserves committed source");
    engineeringStore.MarkInterruptedOnStartup();
    Check(engineeringStore.Get(taskBound.Id, recovered.Id).Events.Count ==
          eventsBeforeRestart + 1,
        "repeated startup recovery does not duplicate interruption events");

    // Upgrade an older database that recorded an Interrupted run but lacked
    // the final activity receipt. Recovery must be additive and idempotent.
    var legacyInterrupted = engineeringStore.Start(taskBound.Id, approvedEngineerPlan);
    engineeringStore.StartTask(taskBound.Id, legacyInterrupted.Id, "timer-interface");
    engineeringStore.CompleteTask(taskBound.Id, legacyInterrupted.Id, "timer-interface",
        new EngineeringChangeSet("First file persisted by legacy run.",
            [new EngineeringFileChange("index.html", "Offline browser entry point",
                "<!doctype html><html><body>Persisted earlier</body></html>")],
            ["Not executed"]));
    engineeringStore.StartTask(taskBound.Id, legacyInterrupted.Id, "timer-state");
    var priorLegacyEvents = engineeringStore.Get(
        taskBound.Id, legacyInterrupted.Id).Events.Count;
    using (var legacyDb = new Microsoft.Data.Sqlite.SqliteConnection(
        "Data Source=" + Path.Combine(root, "codingassistant.db")))
    {
        legacyDb.Open();
        using var tx = legacyDb.BeginTransaction();
        using (var cmd = legacyDb.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE engineering_run_tasks SET state='Interrupted'
                  WHERE run_id=$id AND state='Running';
                UPDATE engineering_runs SET state='Interrupted',
                  phase='RestartInterrupted',error_code='PROCESS_INTERRUPTED'
                  WHERE id=$id AND state='Running';
                """;
            cmd.Parameters.AddWithValue("$id", legacyInterrupted.Id);
            _ = cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }
    engineeringStore.MarkInterruptedOnStartup();
    var backfilled = engineeringStore.Get(taskBound.Id, legacyInterrupted.Id);
    Check(backfilled.Job.State == "Interrupted" &&
          backfilled.Job.ErrorCode == "PROCESS_INTERRUPTED" &&
          backfilled.Events.Count == priorLegacyEvents + 1 &&
          backfilled.Events[^1].Kind == "EngineeringRunInterrupted" &&
          backfilled.Events[^1].Detail.Contains("Recovered missing",
              StringComparison.Ordinal) &&
          backfilled.Files.Count == 1 &&
          backfilled.Files[0].Content.Contains("Persisted earlier",
              StringComparison.Ordinal),
        "older interrupted run gets missing receipt with no source or state mutation");
    engineeringStore.MarkInterruptedOnStartup();
    Check(engineeringStore.Get(taskBound.Id, legacyInterrupted.Id).Events.Count ==
          priorLegacyEvents + 1,
        "legacy interruption event is backfilled once");

    // Even an approved plan does not make an older engineering submission
    // authoritative after a newer run exists; retain historical QA safely.
    var supersedingSource = engineeringStore.Start(taskBound.Id,
        approvedEngineerPlan);
    using (var historicalSource = await Post(client, qaPath,
        new StartQaReviewRequest(interrupted.Id, secondPlanHash)))
        Check(historicalSource.StatusCode == HttpStatusCode.Conflict &&
              qaModel.ReviewedFiles.Count == 8,
            "QA refuses superseded engineering source even under the same plan");
    engineeringStore.Interrupt(taskBound.Id, supersedingSource.Id);
    using (var staleProposal = await Post(client, proposalPath,
        new CreateRepairProposalRequest(qaReviewId)))
        Check(staleProposal.StatusCode == HttpStatusCode.Conflict,
            "repair proposal replay fails after a newer engineering source is saved");
    using (var staleTriage = await Post(client, triagePath,
        classification with { Classification = "ImplementationDefect" }))
        Check(staleTriage.StatusCode == HttpStatusCode.Conflict,
            "stale source cannot receive a new classification");



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

    // Failure recovery: the owner explicitly resumes the same immutable
    // approved plan from saved tasks, without replaying earlier model work.
    var resumeSource = engineeringStore.Start(taskBound.Id, approvedEngineerPlan);
    engineeringStore.StartTask(taskBound.Id, resumeSource.Id, "timer-interface");
    engineeringStore.CompleteTask(taskBound.Id, resumeSource.Id, "timer-interface",
        new EngineeringChangeSet("Saved the entry point before model failure.",
            [new EngineeringFileChange("index.html", "Offline browser entry point",
                "<!doctype html><html><body><button id='start'>Start</button><script src='app.js'></script></body></html>")],
            ["Test source remains unverified"]));
    engineeringStore.StartTask(taskBound.Id, resumeSource.Id, "timer-state");
    engineeringStore.Finish(taskBound.Id, resumeSource.Id, false,
        "MODEL_OUTPUT_REJECTED");
    var sourceCheckpoint = engineeringStore.Get(taskBound.Id, resumeSource.Id);
    Check(sourceCheckpoint.Job.State == "Failed" &&
          sourceCheckpoint.Job.ErrorCode == "MODEL_OUTPUT_REJECTED" &&
          sourceCheckpoint.Tasks[0].State == "Completed" &&
          sourceCheckpoint.Tasks[1].State == "Failed" &&
          sourceCheckpoint.Files.Count == 1,
        "failed run retains a valid one-file resume checkpoint");

    using (var deniedResume = await Post(client, engineeringPath,
        new StartEngineeringRunRequest(2, new string('f', 64), resumeSource.Id)))
        Check(deniedResume.StatusCode == HttpStatusCode.Conflict,
            "resume does not bypass current approved plan digest");
    using (var deniedResume = await Post(client, engineeringPath,
        new StartEngineeringRunRequest(2, secondPlanHash,
            new string('f', 32))))
        Check(deniedResume.StatusCode != HttpStatusCode.Accepted,
            "resume rejects foreign or nonexistent source run");
    var engineerCallsBeforeResume = softwareModel.AssignedIds.Count;
    string resumedRunId;
    using (var resumed = await Post(client, engineeringPath,
        new StartEngineeringRunRequest(2, secondPlanHash, resumeSource.Id)))
    using (var body = await Body(resumed))
    {
        Check(resumed.StatusCode == HttpStatusCode.Accepted,
            "owner explicitly starts a fresh resume run for the exact failed source");
        resumedRunId = body.RootElement.GetProperty("id").GetString()!;
    }
    EngineeringRunSnapshot? resumedDone = null;
    for (var attempt = 0; attempt < 160; attempt++)
    {
        var saved = engineeringStore.Get(taskBound.Id, resumedRunId);
        if (saved.Job.State != "Running")
        {
            resumedDone = saved;
            break;
        }
        await Task.Delay(25);
    }
    Console.WriteLine("Resume probe (source-only): state=" +
        (resumedDone?.Job.State ?? "NO_TERMINAL_STATE") +
        " error=" + (resumedDone?.Job.ErrorCode ?? "none") +
        " tasks=" + string.Join(",", resumedDone?.Tasks.Select(t => t.State) ??
            Enumerable.Empty<string>()) +
        " files=" + string.Join(",", resumedDone?.Files.Select(f => f.Path) ??
            Enumerable.Empty<string>()) +
        " events=" + string.Join(",", resumedDone?.Events.Select(e => e.Kind) ??
            Enumerable.Empty<string>()) +
        " modelCalls=" + string.Join(",",
            softwareModel.AssignedIds.Skip(engineerCallsBeforeResume)));
    Check(resumedDone?.Job.State == "Generated" &&
          resumedDone.Tasks.Count == 2 &&
          resumedDone.Tasks.All(item => item.State == "Completed") &&
          resumedDone.Files.Count == 2 &&
          resumedDone.Files.Any(file => file.Path == "index.html" &&
              file.LastWorkItemId == "timer-interface" &&
              file.Sha256 == sourceCheckpoint.Files.Single().Sha256) &&
          resumedDone.Files.Any(file => file.Path == "app.js" &&
              file.LastWorkItemId == "timer-state") &&
          resumedDone.Events.First().Kind == "EngineeringRunResumed" &&
          resumedDone.Events.Count(e => e.Kind == "EngineeringTaskStarted") == 1 &&
          resumedDone.Events.Count(e => e.Kind == "EngineeringTaskCompleted") == 1 &&
          softwareModel.AssignedIds.Skip(engineerCallsBeforeResume).SequenceEqual(["timer-state"]),
        "resume carries completed work and asks MAF to implement only failed assignment");
    var unchangedCheckpoint = engineeringStore.Get(taskBound.Id, resumeSource.Id);
    Check(unchangedCheckpoint.Job.State == "Failed" &&
          unchangedCheckpoint.Events.Count == sourceCheckpoint.Events.Count &&
          unchangedCheckpoint.Files.Single().Sha256 == sourceCheckpoint.Files.Single().Sha256,
        "resume never mutates original failed source or its failure event");
    using (var staleResume = await Post(client, engineeringPath,
        new StartEngineeringRunRequest(2, secondPlanHash, resumeSource.Id)))
        Check(staleResume.StatusCode == HttpStatusCode.Conflict,
            "cannot resume superseded failure checkpoint after a newer run exists");

    // Exercise the same persisted repair workflow for a genuine lead-review
    // origin without changing the original QA repair acceptance assertions.
    string aetResumedLeadId;
    using(var aetAdmittedLead=await Post(client,aetLeadPath,
        new StartQaReviewRequest(resumedRunId,secondPlanHash)))
    using(var aetAdmittedBody=await Body(aetAdmittedLead))
    {
        Check(aetAdmittedLead.StatusCode==HttpStatusCode.Accepted,"independent lead reviews resumed generated source");
        aetResumedLeadId=aetAdmittedBody.RootElement.GetProperty("id").GetString()!;
    }
    LeadReviewSnapshot? aetResumedLead=null;
    for(var aetAttempt=0;aetAttempt<160;aetAttempt++)
    {
        aetResumedLead=aetLeadStore.Get(taskBound.Id,aetResumedLeadId);
        if(aetResumedLead.Job.State!="Running") break;
        await Task.Delay(25);
    }
    Check(aetResumedLead?.Disposition=="ChangesRequested","independent lead finding saved on resumed revision");
    using(var aetLeadProposed=await Post(client,aetLeadProposalPath,new CreateRepairProposalRequest(null,aetResumedLeadId)))
        Check(aetLeadProposed.StatusCode==HttpStatusCode.Created,"lead-origin repair proposal admitted");
    var aetResumedProposal=new RepairProposalStore(tasks).Latest(taskBound.Id)!;
    using(var aetLeadConfirmed=await Post(client,aetLeadProposalPath+"/"+aetResumedProposal.Id+"/triage",
        new ClassifyRepairFindingRequest("app.js",0,"ImplementationDefect",
            "Owner confirms the source defect and approves only its existing file scope.")))
        Check(aetLeadConfirmed.StatusCode==HttpStatusCode.OK,"lead-origin finding independently classified");
    string aetLeadCycleId;
    using(var aetLeadRepair=await Post(client,latestRepairPath,new StartRepairCycleRequest(aetResumedProposal.Id,
        "REPAIR "+aetResumedProposal.SourceSha256,[new RepairSelection("app.js",0)])))
    using(var aetRepairBody=await Body(aetLeadRepair))
    {
        Check(aetLeadRepair.StatusCode==HttpStatusCode.Accepted,"lead-origin finding enters existing exact-scope source writer");
        aetLeadCycleId=aetRepairBody.RootElement.GetProperty("id").GetString()!;
    }
    RepairCycleSnapshot? aetLeadRepairResult=null;
    var aetUnifiedCycles=new RepairCycleStore(tasks,engineeringStore,reviewStore,repairProposal);
    for(var aetAttempt=0;aetAttempt<240;aetAttempt++)
    {
        aetLeadRepairResult=aetUnifiedCycles.Get(taskBound.Id,aetLeadCycleId);
        if(aetLeadRepairResult.Cycle.State is not ("Running" or "Generated" or "QaRunning"))break;
        await Task.Delay(25);
    }
    Check(aetLeadRepairResult?.Cycle.State=="QaReviewed" &&
        aetLeadRepairResult.Engineering.Files.Single(f=>f.Path=="index.html").Sha256==
            resumedDone!.Files.Single(f=>f.Path=="index.html").Sha256 &&
        aetLeadRepairResult.Engineering.Files.Single(f=>f.Path=="app.js").Sha256!=
            resumedDone!.Files.Single(f=>f.Path=="app.js").Sha256 &&
        aetLeadRepairResult.StaticPreflight.VerificationStatus=="GENERATED_NOT_TESTED",
        "lead-origin repair uses existing scope enforcement, immutable revision and independent static QA");
    var aetLeadRepairedIssue=new EngineeringIssueStore(tasks).Read(taskBound.Id,resumedRunId)
        .Issues.Single(x=>x.Origin=="LeadSoftwareEngineer");
    Check(aetLeadRepairedIssue.RepairCycleId==aetLeadCycleId &&
        aetLeadRepairedIssue.State=="AwaitingIndependentVerification",
        "lead-origin issue remains linked to actual repair evidence without false closure");
    var aetLeadVerifyRequest=new VerifyEngineeringIssueRequest(resumedRunId,aetLeadRepairedIssue.Id,
        aetLeadRepairResult!.Engineering.Job.Id,QaReviewStore.Fingerprint(aetLeadRepairResult.Engineering.Files));
    using(var aetLeadVerified=await Post(client,"/api/tasks/"+taskBound.Id+"/engineering-issues/verify",aetLeadVerifyRequest))
    using(var aetLeadVerifiedBody=await Body(aetLeadVerified))
        Check(aetLeadVerified.StatusCode==HttpStatusCode.OK &&
            aetLeadVerifiedBody.RootElement.GetProperty("agentId").GetString()=="LeadSoftwareEngineer",
            "originating lead reviewer independently rechecks repaired source");
    var aetMafVerifierFactory=new Phase5FakeMafFactory();
    aetMafVerifierFactory.ScriptedReplies.Enqueue("""
        {"outcome":"ResolvedByStaticEvidence","rationale":"Invented evidence is not accepted.","line":999,"evidence":"missing","architectureChoiceSha256":null}
        """);
    aetMafVerifierFactory.ScriptedReplies.Enqueue("""
        {"outcome":"StillPresent","rationale":"The candidate has the exact saved logging construct.","line":1,"evidence":"console.log","architectureChoiceSha256":null}
        """);
    var aetMafAssessment=await new MafEngineeringIssueVerifier(aetMafVerifierFactory).VerifyAsync(
        aetVerificationStore.Prepare(taskBound.Id,aetLeadVerifyRequest),"qwen3.5:4b",CancellationToken.None);
    Check(aetMafAssessment.Outcome=="StillPresent" &&
        aetMafVerifierFactory.Purposes.SequenceEqual([MafAgentPurpose.LeadSoftwareEngineer]) &&
        aetMafVerifierFactory.Models.SequenceEqual(["qwen3.5:4b"]) &&
        aetMafVerifierFactory.NoTools && aetMafVerifierFactory.ScriptedReplies.Count==0,
        "real MAF verification adapter rejects invented evidence, retries once and exposes no write or execution tools");
    Check(new EngineeringIssueStore(tasks).Read(taskBound.Id,resumedRunId).Issues
        .Single(x=>x.Id==aetLeadRepairedIssue.Id).State=="AwaitingQualifiedVerification" &&
        qaModel.ReviewedFiles.Count==10,
        "lead static correctness assessment cannot claim functional verification; actual QA reviewed two more files");

    // Adaptive scheduling preserves the exact approved scope and required
    // dependencies, while allowing safe priority and suspension decisions.
    var aetAdaptivePolicy=delegationStore.Propose(taskBound.Id,
        new ProposeDelegationRequest(3,secondPlanHash,["ScheduleWork"]));
    aetAdaptivePolicy=delegationStore.Approve(taskBound.Id,new ApproveDelegationRequest(
        aetAdaptivePolicy.Revision,aetAdaptivePolicy.PolicySha256,"APPROVE "+aetAdaptivePolicy.PolicySha256));
    var aetAdaptiveContext=new AdaptivePlanContext(taskBound.Id,"TechnicalLead",
        aetAdaptivePolicy.Revision,aetAdaptivePolicy.PolicySha256,secondPlanHash,
        aetLeadRepairResult!.Engineering.Job.Id,QaReviewStore.Fingerprint(aetLeadRepairResult.Engineering.Files));
    var aetWorkPlanStore=new AdaptiveWorkPlanStore(tasks);
    var aetSchedule=new WorkPackageSchedule[] {
        new("timer-interface",[],5,false,"Medium",["index.html"]),
        new("timer-state",["timer-interface"],90,false,"High",["app.js"])
    };
    var aetPlanDraft=new ReviseAdaptivePlanDraft(0,null,aetSchedule,"Prioritize state only after the required interface dependency.");
    RejectMessage(()=>aetWorkPlanStore.Revise(aetAdaptiveContext with {FromAgent="SoftwareEngineer"},
        aetPlanDraft),"AGENT_CAPABILITY_DENIED");
    RejectMessage(()=>aetWorkPlanStore.Revise(aetAdaptiveContext,
        aetPlanDraft with {Packages=[aetSchedule[0],aetSchedule[1] with {DependsOn=[]}]}),"INVALID_WORK_PLAN");
    RejectMessage(()=>aetWorkPlanStore.Revise(aetAdaptiveContext,
        aetPlanDraft with {Packages=[aetSchedule[0] with {DependsOn=["timer-state"]},aetSchedule[1]]}),"WORK_DEPENDENCY_CYCLE");
    var aetOperationalPlan=aetWorkPlanStore.Revise(aetAdaptiveContext,aetPlanDraft);
    var aetEmptyStates=new Dictionary<string,string>();
    Check(AdaptiveWorkPlanStore.Next(aetOperationalPlan.Packages,aetEmptyStates)=="timer-interface" &&
        AdaptiveWorkPlanStore.Evaluate(aetOperationalPlan.Packages,aetEmptyStates)
            .Single(x=>x.Id=="timer-state").State=="WaitingForDependency" &&
        aetOperationalPlan.Packages.All(x=>x.ArchitectureSha256==architectureSha),
        "adaptive priority cannot bypass mandatory dependency or approved architecture");
    var aetIndependentPackage=aetOperationalPlan.Packages[0] with {
        Id="independent-docs",Priority=99,DependsOn=[] };
    Check(AdaptiveWorkPlanStore.Next([..aetOperationalPlan.Packages,aetIndependentPackage],aetEmptyStates)
        =="independent-docs","independent ready work can be reprioritized without bypassing dependency constraints");
    var aetWriterStates=new Dictionary<string,string>{{"timer-interface","Running"}};
    Check(AdaptiveWorkPlanStore.Next(aetOperationalPlan.Packages,aetWriterStates) is null,
        "adaptive readiness never dispatches another writer while one is running");
    var aetSuspendDraft=new ReviseAdaptivePlanDraft(1,aetOperationalPlan.WorkPlanSha256,
        [aetSchedule[0] with {Suspended=true},aetSchedule[1]],"Suspend pending work while a dependency is investigated.");
    var aetSuspendedPlan=aetWorkPlanStore.Revise(aetAdaptiveContext,aetSuspendDraft);
    Check(AdaptiveWorkPlanStore.Next(aetSuspendedPlan.Packages,aetEmptyStates) is null &&
        aetWorkPlanStore.Read(taskBound.Id).History.Count==2,
        "revisioned suspension blocks dependent work and preserves history");
    RejectMessage(()=>aetWorkPlanStore.Revise(aetAdaptiveContext,
        new ReviseAdaptivePlanDraft(2,aetSuspendedPlan.WorkPlanSha256,
            [aetSchedule[0] with {AllowedPaths=null},aetSchedule[1]],"Attempt to widen an already narrowed source scope.")),
        "WORK_SCOPE_EXPANSION");
    RejectMessage(()=>aetWorkPlanStore.Revise(aetAdaptiveContext,aetPlanDraft),"STALE_WORK_PLAN");
    using(var aetReadPlan=await client.GetAsync("/api/tasks/"+taskBound.Id+"/adaptive-work-plan"))
    using(var aetReadBody=await Body(aetReadPlan))
        Check(aetReadPlan.StatusCode==HttpStatusCode.OK &&
            aetReadBody.RootElement.GetProperty("plan").GetProperty("workPlanSha256").GetString()==aetSuspendedPlan.WorkPlanSha256,
            "owner can inspect the durable operational work plan");
    // Exercise the actual scheduler and source transaction, not just a graph preview.
    var aetAdaptiveRun=engineeringStore.Start(taskBound.Id,approvedEngineerPlan,"qwen2.5-coder:14b",
        new AdaptivePlanSelection(aetSuspendedPlan.Revision,aetSuspendedPlan.WorkPlanSha256));
    RejectMessage(()=>engineeringStore.StartTask(taskBound.Id,aetAdaptiveRun.Id,"timer-state"),"WORK_NOT_READY");
    Check(engineeringStore.StartNextAdaptiveTask(taskBound.Id,aetAdaptiveRun.Id) is null,
        "suspended work prevents actual host dispatch");
    var aetActiveContext=aetAdaptiveContext with {ExpectedRunId=aetAdaptiveRun.Id,
        ExpectedSourceSha256=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Array.Empty<byte>())).ToLowerInvariant()};
    var aetActivePlan=aetWorkPlanStore.Revise(aetActiveContext,new ReviseAdaptivePlanDraft(2,
        aetSuspendedPlan.WorkPlanSha256,aetSchedule,"Resume the investigated dependency with unchanged approved scope."));
    var aetFirstDispatch=engineeringStore.StartNextAdaptiveTask(taskBound.Id,aetAdaptiveRun.Id);
    Check(aetFirstDispatch?.Id=="timer-interface","host dispatch follows current work plan revision");
    RejectMessage(()=>aetWorkPlanStore.Revise(aetActiveContext,new ReviseAdaptivePlanDraft(3,
        aetActivePlan.WorkPlanSha256,aetSchedule,"Unsafe revision while a source writer is active.")),"BUILD_BUSY");
    RejectMessage(()=>engineeringStore.CompleteTask(taskBound.Id,aetAdaptiveRun.Id,"timer-interface",
        new EngineeringChangeSet("This attempts a replacement outside the narrowed scope.",
            [new EngineeringFileChange("app.js","Out of scope source","console.log('invalid');")],
            ["Inspect source later."])),"WORK_SCOPE_VIOLATION");
    Check(engineeringStore.Get(taskBound.Id,aetAdaptiveRun.Id).Files.Count==0,
        "transactional source scope rejection leaves no persisted source");
    engineeringStore.CompleteTask(taskBound.Id,aetAdaptiveRun.Id,"timer-interface",
        new EngineeringChangeSet("Saved the approved browser interface source.",
            [new EngineeringFileChange("index.html","Browser interface","<!doctype html><html><body>Timer</body></html>")],
            ["Independently verify browser behavior."]));
    var aetAfterFirst=engineeringStore.Get(taskBound.Id,aetAdaptiveRun.Id);
    aetActiveContext=aetActiveContext with {ExpectedSourceSha256=QaReviewStore.Fingerprint(aetAfterFirst.Files)};
    RejectMessage(()=>aetWorkPlanStore.Revise(aetActiveContext,new ReviseAdaptivePlanDraft(3,
        aetActivePlan.WorkPlanSha256,[aetSchedule[0] with {Priority=99},aetSchedule[1]],
        "Attempt to rewrite a completed package scheduling record.")),"WORK_ALREADY_COMPLETED");
    var aetReprioritized=aetWorkPlanStore.Revise(aetActiveContext,new ReviseAdaptivePlanDraft(3,
        aetActivePlan.WorkPlanSha256,[aetSchedule[0],aetSchedule[1] with {Priority=100}],
        "Reprioritize only the pending package at the safe assignment boundary."));
    Check(engineeringStore.StartNextAdaptiveTask(taskBound.Id,aetAdaptiveRun.Id)?.Id=="timer-state",
        "revised pending package dispatches after completed dependency");
    engineeringStore.CompleteTask(taskBound.Id,aetAdaptiveRun.Id,"timer-state",
        new EngineeringChangeSet("Saved the approved timer behavior source.",
            [new EngineeringFileChange("app.js","Timer behavior","console.log('start');")],
            ["Independently verify timer functionality."]));
    engineeringStore.Finish(taskBound.Id,aetAdaptiveRun.Id,true,null);
    var aetAdaptiveView=aetWorkPlanStore.Read(taskBound.Id);
    Check(aetAdaptiveView.BoundRunId==aetAdaptiveRun.Id &&
        aetAdaptiveView.Assignments is {Count:2} &&
        aetAdaptiveView.Assignments[0].WorkPlanSha256==aetActivePlan.WorkPlanSha256 &&
        aetAdaptiveView.Assignments[1].WorkPlanSha256==aetReprioritized.WorkPlanSha256 &&
        aetAdaptiveView.Assignments[1].SourceBeforeSha256==aetAdaptiveView.Assignments[0].SourceAfterSha256 &&
        aetAdaptiveView.Readiness.All(x=>x.State=="Completed"),
        "actual assignments and source checkpoints bind their respective immutable scheduling revisions");
    var aetNextContext=aetAdaptiveContext with {ExpectedRunId=aetAdaptiveRun.Id,
        ExpectedSourceSha256=QaReviewStore.Fingerprint(engineeringStore.Get(taskBound.Id,aetAdaptiveRun.Id).Files)};
    var aetNextPlan=aetWorkPlanStore.Revise(aetNextContext,new ReviseAdaptivePlanDraft(4,
        aetReprioritized.WorkPlanSha256,aetSchedule,"Bind a fresh owner-admitted source run to the validated work graph."));
    using(var aetWrongSchedule=await Post(client,engineeringPath,
        new StartEngineeringRunRequest(2,secondPlanHash,WorkPlan:new AdaptivePlanSelection(4,aetReprioritized.WorkPlanSha256))))
        Check(aetWrongSchedule.StatusCode==HttpStatusCode.Conflict,"stale work plan cannot admit an engineering run");
    string aetCoordinatorRunId;
    using(var aetAdmittedAdaptive=await Post(client,engineeringPath,
        new StartEngineeringRunRequest(2,secondPlanHash,WorkPlan:new AdaptivePlanSelection(5,aetNextPlan.WorkPlanSha256))))
    using(var aetAdmittedAdaptiveBody=await Body(aetAdmittedAdaptive))
    {
        Check(aetAdmittedAdaptive.StatusCode==HttpStatusCode.Accepted,"owner selects exact adaptive plan through existing engineering API");
        aetCoordinatorRunId=aetAdmittedAdaptiveBody.RootElement.GetProperty("id").GetString()!;
    }
    EngineeringRunSnapshot? aetCoordinatorDone=null;
    for(var aetAttempt=0;aetAttempt<160;aetAttempt++)
    {
        aetCoordinatorDone=engineeringStore.Get(taskBound.Id,aetCoordinatorRunId);
        if(aetCoordinatorDone.Job.State!="Running")break;
        await Task.Delay(25);
    }
    Check(aetCoordinatorDone?.Job.State=="Generated" &&
        aetWorkPlanStore.Read(taskBound.Id).Assignments is {Count:2} &&
        aetCoordinatorDone.Files.Count==2,
        "existing engineering coordinator dispatches real bound packages and persists actual source");
    var aetResumeContext=aetAdaptiveContext with {ExpectedRunId=aetCoordinatorRunId,
        ExpectedSourceSha256=QaReviewStore.Fingerprint(aetCoordinatorDone!.Files)};
    var aetResumePlan=aetWorkPlanStore.Revise(aetResumeContext,new ReviseAdaptivePlanDraft(5,
        aetNextPlan.WorkPlanSha256,aetSchedule,"Use the same approved scope to exercise recovery after partial work."));
    var aetFailedAdaptive=engineeringStore.Start(taskBound.Id,approvedEngineerPlan,"qwen2.5-coder:14b",
        new AdaptivePlanSelection(6,aetResumePlan.WorkPlanSha256));
    Check(engineeringStore.StartNextAdaptiveTask(taskBound.Id,aetFailedAdaptive.Id)?.Id=="timer-interface",
        "adaptive run admits initial dependency");
    engineeringStore.CompleteTask(taskBound.Id,aetFailedAdaptive.Id,"timer-interface",
        new EngineeringChangeSet("Saved approved interface before interruption.",
            [new EngineeringFileChange("index.html","Browser interface","<!doctype html><html><body>Timer</body></html>")],
            ["Independently verify browser behavior."]));
    Check(engineeringStore.StartNextAdaptiveTask(taskBound.Id,aetFailedAdaptive.Id)?.Id=="timer-state",
        "adaptive run admits next dependency before failure");
    engineeringStore.Finish(taskBound.Id,aetFailedAdaptive.Id,false,"MODEL_OUTPUT_REJECTED");
    var aetAdaptiveCheckpoint=engineeringStore.Get(taskBound.Id,aetFailedAdaptive.Id);
    var aetCallsBeforeResume=softwareModel.AssignedIds.Count;
    string aetAdaptiveResumeId;
    using(var aetResumeResponse=await Post(client,engineeringPath,
        new StartEngineeringRunRequest(2,secondPlanHash,aetFailedAdaptive.Id)))
    using(var aetResumeBody=await Body(aetResumeResponse))
    {
        Check(aetResumeResponse.StatusCode==HttpStatusCode.Accepted,
            "owner can explicitly resume exact adaptive source checkpoint");
        aetAdaptiveResumeId=aetResumeBody.RootElement.GetProperty("id").GetString()!;
    }
    EngineeringRunSnapshot? aetAdaptiveResumed=null;
    for(var aetAttempt=0;aetAttempt<160;aetAttempt++)
    {
        aetAdaptiveResumed=engineeringStore.Get(taskBound.Id,aetAdaptiveResumeId);
        if(aetAdaptiveResumed.Job.State!="Running")break;
        await Task.Delay(25);
    }
    Check(aetAdaptiveResumed?.Job.State=="Generated" &&
        softwareModel.AssignedIds.Skip(aetCallsBeforeResume).SequenceEqual(["timer-state"]) &&
        aetAdaptiveResumed.Files.Single(f=>f.Path=="index.html").Sha256==aetAdaptiveCheckpoint.Files.Single().Sha256 &&
        aetWorkPlanStore.Read(taskBound.Id).Assignments is {Count:1},
        "adaptive resume preserves completed source and invokes only remaining package");
    // Decompose a parent without adding requirements or widening approved file scope.
    var aetStepCriteria=Enumerable.Range(0,approvedEngineerPlan.Outline.Tasks.Single(x=>x.Id=="timer-state").AcceptanceCriteria.Count).ToArray();
    var aetChildren=new WorkSubpackageSchedule[]{
        new("state-setup","Establish the timer state within approved behavior.",aetStepCriteria,["app.js"]),
        new("state-wiring","Complete the approved timer control interaction.",aetStepCriteria,["app.js"])
    };
    var aetChildContext=aetAdaptiveContext with {ExpectedRunId=aetAdaptiveResumeId,
        ExpectedSourceSha256=QaReviewStore.Fingerprint(aetAdaptiveResumed!.Files)};
    var aetChildDraft=new ReviseAdaptivePlanDraft(6,aetResumePlan.WorkPlanSha256,
        [aetSchedule[0],aetSchedule[1] with {Subpackages=aetChildren}],
        "Decompose the approved timer state item into bounded child source assignments.");
    RejectMessage(()=>aetWorkPlanStore.Revise(aetChildContext,aetChildDraft with {
        Packages=[aetSchedule[0],aetSchedule[1] with {Subpackages=[aetChildren[0],aetChildren[1] with {AllowedPaths=["index.html"]}]}]
    }),"WORK_SCOPE_EXPANSION");
    var aetChildPlan=aetWorkPlanStore.Revise(aetChildContext,aetChildDraft);
    var aetChildRun=engineeringStore.Start(taskBound.Id,approvedEngineerPlan,"qwen2.5-coder:14b",
        new AdaptivePlanSelection(7,aetChildPlan.WorkPlanSha256));
    Check(engineeringStore.StartNextAdaptiveTask(taskBound.Id,aetChildRun.Id)?.Id=="timer-interface",
        "parent dependencies remain enforced for decomposed work");
    engineeringStore.CompleteTask(taskBound.Id,aetChildRun.Id,"timer-interface",
        new EngineeringChangeSet("Saved approved interface for child work.",
            [new EngineeringFileChange("index.html","Browser interface","<!doctype html><html><body>Timer</body></html>")],
            ["Independently verify browser behavior."]));
    Check(engineeringStore.StartNextAdaptiveTask(taskBound.Id,aetChildRun.Id)?.Id=="timer-state" &&
        engineeringStore.StartNextSubpackage(taskBound.Id,aetChildRun.Id,"timer-state")?.Id=="state-setup",
        "host dispatches first bounded child only after parent dependency");
    var aetChildChange=new EngineeringChangeSet("Saved initial timer state before wiring.",
        [new EngineeringFileChange("app.js","Initial timer state","let timerState = 0;")],
        ["Independently verify timer interaction."]);
    RejectMessage(()=>engineeringStore.CompleteTask(taskBound.Id,aetChildRun.Id,"timer-state",aetChildChange),"WORK_NOT_READY");
    engineeringStore.CompleteTask(taskBound.Id,aetChildRun.Id,"timer-state",aetChildChange,"state-setup");
    var aetChildCheckpoint=engineeringStore.Get(taskBound.Id,aetChildRun.Id);
    Check(aetChildCheckpoint.Tasks.Single(x=>x.Id=="timer-state").State=="Running" &&
        aetWorkPlanStore.Read(taskBound.Id).SubpackageReceipts is {Count:1},
        "child source is persisted without prematurely completing parent");
    Check(engineeringStore.StartNextSubpackage(taskBound.Id,aetChildRun.Id,"timer-state")?.Id=="state-wiring",
        "next child waits for first committed source");
    engineeringStore.Finish(taskBound.Id,aetChildRun.Id,false,"MODEL_OUTPUT_REJECTED");
    var aetChildCalls=softwareModel.AssignedIds.Count;
    string aetChildResumeId;
    using(var aetChildResume=await Post(client,engineeringPath,new StartEngineeringRunRequest(2,secondPlanHash,aetChildRun.Id)))
    using(var aetChildResumeBody=await Body(aetChildResume))
    {
        Check(aetChildResume.StatusCode==HttpStatusCode.Accepted,"explicit resume carries completed child source checkpoint");
        aetChildResumeId=aetChildResumeBody.RootElement.GetProperty("id").GetString()!;
    }
    EngineeringRunSnapshot? aetChildDone=null;
    for(var aetAttempt=0;aetAttempt<160;aetAttempt++)
    {
        aetChildDone=engineeringStore.Get(taskBound.Id,aetChildResumeId);
        if(aetChildDone.Job.State!="Running")break;
        await Task.Delay(25);
    }
    var aetChildView=aetWorkPlanStore.Read(taskBound.Id);
    Check(aetChildDone?.Job.State=="Generated" &&
        softwareModel.AssignedIds.Skip(aetChildCalls).SequenceEqual(["timer-state"]) &&
        aetChildView.SubpackageReceipts is {Count:2} &&
        aetChildView.SubpackageReceipts[0].CarriedFromRunId==aetChildRun.Id &&
        aetChildView.SubpackageReceipts[1].SubpackageId=="state-wiring" &&
        aetChildView.SubpackageReceipts[0].SourceAfterSha256==aetChildView.SubpackageReceipts[1].SourceBeforeSha256,
        "resume invokes only incomplete child and retains exact checkpoint provenance");
    delegationStore.Revoke(taskBound.Id,new ApproveDelegationRequest(aetAdaptivePolicy.Revision,
        aetAdaptivePolicy.PolicySha256,"REVOKE "+aetAdaptivePolicy.PolicySha256));

    // Recovery is driven by evidence, not a lifetime three-repair cap.
    var aetProgressAttempts=Enumerable.Range(0,5).Select(i=>new RepairProgressAttempt(
        "fixture-"+i,"run-"+i,"run-"+(i+1),"hash-"+i,"hash-"+(i+1),"QaReviewed",
        [],"QaEngineer",true,false)).ToArray();
    Check(!RepairProgressStore.Evaluate("run-5",aetProgressAttempts,[]).StrategyChangeRequired,
        "five independently productive repair attempts do not exhaust a project-wide cap");
    Check(RepairProgressStore.Evaluate("run-5",[..aetProgressAttempts,
        aetProgressAttempts[^1] with {OriginalSourceSha256="hash-5",CandidateSourceSha256="hash-2"}],[])
        .Reason=="REPEATED_SOURCE_SNAPSHOT","oscillating source snapshots require strategy change");
    Check(RepairProgressStore.Evaluate("run-2",aetProgressAttempts.Take(2).Select(x=>x with {
        IndependentStaticResolution=false,RecurringExactFinding=true}).ToArray(),["fixture-issue"])
        .Reason=="RECURRING_UNRESOLVED_FINDING","consecutive identical unresolved concern requires changed strategy");
    var aetActualProgress=new RepairProgressStore(tasks).Read(taskBound.Id,aetLeadRepairResult!.Engineering.Job.Id);
    Check(aetActualProgress.Attempts.Count==1 && aetActualProgress.Attempts[0].CycleId==aetLeadCycleId &&
        aetActualProgress.Attempts[0].OriginalRunId==resumedRunId,
        "repair progress uses actual immutable source ancestry rather than project-wide count");

    var aetCollabPolicy=delegationStore.Propose(taskBound.Id,
        new ProposeDelegationRequest(4,secondPlanHash,["ScheduleWork","ReviewCode","ReviewAcceptance","RequestRepair","SendMessage"]));
    aetCollabPolicy=delegationStore.Approve(taskBound.Id,new ApproveDelegationRequest(aetCollabPolicy.Revision,
        aetCollabPolicy.PolicySha256,"APPROVE "+aetCollabPolicy.PolicySha256));
    var aetCollabRequest=new StartCollaborationRequest(aetChildResumeId,QaReviewStore.Fingerprint(aetChildDone!.Files),
        secondPlanHash,aetCollabPolicy.Revision,aetCollabPolicy.PolicySha256);
    var aetCollabPath="/api/tasks/"+taskBound.Id+"/collaboration";
    using(var aetBadCollab=await Post(client,aetCollabPath,aetCollabRequest with {PolicySha256=new string('0',64)}))
        Check(aetBadCollab.StatusCode==HttpStatusCode.Conflict,"stale delegation cannot start autonomous coordination");
    using(var aetAnonymousCollab=new HttpClient{BaseAddress=client.BaseAddress})
    using(var aetDeniedCollab=await Post(aetAnonymousCollab,aetCollabPath,aetCollabRequest))
        Check(aetDeniedCollab.StatusCode==HttpStatusCode.Unauthorized,"collaboration admission remains owner-private");
    var aetCollabStore=new CollaborationStore(tasks);
    // Crash after persisting an intent but before any independent review is
    // admitted. Its unlinked intent must survive, not be treated as work done.
    var aetInterruptedCollab=aetCollabStore.Start(taskBound.Id,aetCollabRequest,"qwen3.5:4b");
    var aetInterruptedStep=aetCollabStore.Record(taskBound.Id,aetInterruptedCollab.Id,
        new CollaborationDecision("ReviewCode","Request review before simulated process interruption."));
    aetCollabStore.Recover();aetCollabStore.Recover();
    Check(aetCollabStore.Get(taskBound.Id,aetInterruptedCollab.Id).Job.State=="Interrupted" &&
        aetInterruptedStep.ReferenceId is null,"interrupted coordination retains unexecuted intent");
    using(var aetStaleResume=await Post(client,aetCollabPath+"/"+aetInterruptedCollab.Id+"/resume",
        aetCollabRequest with {SourceSha256=new string('0',64)}))
        Check(aetStaleResume.StatusCode==HttpStatusCode.Conflict,"stale source cannot resume collaboration");
    string aetCollabId;
    using(var aetCollabStarted=await Post(client,aetCollabPath+"/"+aetInterruptedCollab.Id+"/resume",aetCollabRequest))
    using(var aetCollabBody=await Body(aetCollabStarted))
    {
        Check(aetCollabStarted.StatusCode==HttpStatusCode.Accepted,"owner resumes exact interrupted collaboration session");
        aetCollabId=aetCollabBody.RootElement.GetProperty("id").GetString()!;
    }
    CollaborationSnapshot? aetCollabResult=null;
    for(var aetAttempt=0;aetAttempt<240;aetAttempt++)
    {
        aetCollabResult=aetCollabStore.Get(taskBound.Id,aetCollabId);
        if(aetCollabResult.Job.State!="Running")break;
        await Task.Delay(25);
    }
    Check(aetCollabResult?.Job.State=="AwaitingOwner" &&
        aetCollabResult.Job.Reason=="EXACT_REPAIR_APPROVAL_REQUIRED" &&
        aetCollabId==aetInterruptedCollab.Id &&
        aetCollabResult.Steps.Select(x=>x.Action).SequenceEqual(["ReviewCode","ResumeInterrupted","ReviewCode","ClassifyIssue","RequestOwnerRepair"]) &&
        aetCollabResult.Steps[0].ReferenceId is null &&
        aetCollabResult.Steps.Skip(1).All(x=>x.ReferenceId is not null) &&
        aetCollaborationLead.Calls==3,
        "actual lead decision loop independently reviews, classifies and requests exact repair without bypassing owner gate");
    Check(engineeringStore.Latest(taskBound.Id)!.Job.Id==aetChildResumeId &&
        qaModel.ReviewedFiles.Count==10,
        "blocking lead finding prevents premature QA, source mutation and functional claims");
    var aetCollabProposal=new RepairProposalStore(tasks).Latest(taskBound.Id)!;
    Check(aetCollabProposal.Id==aetCollabResult!.Steps.Last().ReferenceId &&
        aetCollabProposal.LeadReviewId==aetCollabResult.Steps[2].ReferenceId,
        "actual independent review and repair proposal references are durable");
    Check(new AgentCommunicationStore(tasks).List(taskBound.Id,role:"SoftwareEngineer",direction:"inbox")
        .Any(x=>x.RunId==aetChildResumeId && x.FromAgent=="TechnicalLead" && x.Kind=="RepairRequested"),
        "actual Technical Lead repair request appears in engineer durable inbox");
    // The owner still approves exact repair scope; collaboration continuation
    // starts on the new immutable source and independently re-reviews it.
    using(var aetCollabTriage=await Post(client,aetLeadProposalPath+"/"+aetCollabProposal.Id+"/triage",
        new ClassifyRepairFindingRequest("app.js",0,"ImplementationDefect",
            "Owner confirms the identified defect and authorizes only this file.")))
        Check(aetCollabTriage.StatusCode==HttpStatusCode.OK,"owner records exact repair triage");
    using(var aetBadContinuation=await Post(client,latestRepairPath,
        new StartRepairCycleRequest(aetCollabProposal.Id,"REPAIR "+aetCollabProposal.SourceSha256,
            [new RepairSelection("app.js",0)],Guid.NewGuid().ToString("N"))))
        Check(aetBadContinuation.StatusCode==HttpStatusCode.NotFound,"unknown collaboration cannot attach repair continuation");
    string aetCollabCycleId;
    using(var aetCollabRepair=await Post(client,latestRepairPath,
        new StartRepairCycleRequest(aetCollabProposal.Id,"REPAIR "+aetCollabProposal.SourceSha256,
            [new RepairSelection("app.js",0)],aetCollabId)))
    using(var aetCollabRepairBody=await Body(aetCollabRepair))
    {
        Check(aetCollabRepair.StatusCode==HttpStatusCode.Accepted,"exact owner repair also opts into delegated follow-up");
        aetCollabCycleId=aetCollabRepairBody.RootElement.GetProperty("id").GetString()!;
    }
    CollaborationSnapshot? aetContinued=null;
    for(var aetAttempt=0;aetAttempt<320;aetAttempt++)
    {
        aetContinued=aetCollabStore.Latest(taskBound.Id);
        if(aetContinued?.Job.Id!=aetCollabId && aetContinued?.Job.State!="Running")break;
        await Task.Delay(25);
    }
    var aetContinuedCycle=new RepairCycleStore(tasks,engineeringStore,reviewStore,repairProposal).Get(taskBound.Id,aetCollabCycleId);
    Check(aetContinued?.Job.State=="AwaitingOwner" && aetContinued.Job.Id!=aetCollabId &&
        aetContinued.Job.RunId==aetContinuedCycle.Engineering.Job.Id &&
        aetContinued.Job.SourceSha256!=aetCollabRequest.SourceSha256 &&
        aetContinued.Steps.Select(s=>s.Action).SequenceEqual(["ReviewCode","RecheckIssue","ClassifyIssue","RequestOwnerRepair"]) &&
        aetContinuedCycle.Cycle.State=="Generated" &&
        qaModel.ReviewedFiles.Count==10,
        "owner-approved repair automatically returns to independent lead review and specific recheck of new source; blocking finding prevents QA");
    Check(new EngineeringIssueStore(tasks).Read(taskBound.Id,aetChildResumeId).Issues
        .Single(i=>i.Origin=="LeadSoftwareEngineer").Verifications.Single().Id==aetContinued!.Steps[1].ReferenceId,
        "original finding retains actual independent recheck evidence from automatic collaboration");
    delegationStore.Revoke(taskBound.Id,new ApproveDelegationRequest(aetCollabPolicy.Revision,
        aetCollabPolicy.PolicySha256,"REVOKE "+aetCollabPolicy.PolicySha256));

    // AET-210 negative acceptance scenarios use separate immutable source
    // roots. The fixture models do not represent qualified runtime evidence.
    string[] routineOps = ["SendMessage","ScheduleWork","ReviewCode","ReviewAcceptance","RequestRepair","WriteSource"];
    var routineRevision=5;
    softwareModel.RoutineMode=true;
    aetLeadModel.RoutineMode=true;
    aetIssueVerifier.RoutineMode=true;
    qaModel.RoutineMode=true;
    var negativeRepairStore=new RepairCycleStore(tasks,engineeringStore,reviewStore,repairProposal);
    foreach(var scenario in new[]{"budget","regression","revoke","cancel"})
    {
        var root=engineeringStore.Start(taskBound.Id,approvedEngineerPlan);
        engineeringStore.StartTask(taskBound.Id,root.Id,"timer-interface");
        engineeringStore.CompleteTask(taskBound.Id,root.Id,"timer-interface",
            new EngineeringChangeSet("Saved exact local fixture entry.",
                [new EngineeringFileChange("index.html","Entry","<!doctype html><html><body><button id='start'>Start</button><script src='app.js'></script></body></html>")],
                ["Independently verify browser interaction."]));
        engineeringStore.StartTask(taskBound.Id,root.Id,"timer-state");
        engineeringStore.CompleteTask(taskBound.Id,root.Id,"timer-state",
            new EngineeringChangeSet("Saved exact two-defect fixture.",
                [new EngineeringFileChange("app.js","Timer behavior","document.getElementById('start').addEventListener('click',()=>{console.log('routine-one');console.log('routine-two');});")],
                ["Independently verify timer behavior."]));
        engineeringStore.Finish(taskBound.Id,root.Id,true,null);
        var rootFiles=engineeringStore.Get(taskBound.Id,root.Id).Files;
        var rootHash=QaReviewStore.Fingerprint(rootFiles);
        var negativePolicy=delegationStore.Propose(taskBound.Id,
            new ProposeDelegationRequest(routineRevision,secondPlanHash,routineOps,
                new RoutineRepairScope(root.Id,rootHash,["app.js"],scenario=="budget"?1:4)));
        negativePolicy=delegationStore.Approve(taskBound.Id,new ApproveDelegationRequest(
            negativePolicy.Revision,negativePolicy.PolicySha256,"APPROVE "+negativePolicy.PolicySha256));
        routineRevision=negativePolicy.Revision;
        softwareModel.IntroduceRoutineRegression=scenario=="regression";
        softwareModel.RoutineGate=scenario is "revoke" or "cancel"?
            new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously):null;
        softwareModel.RoutineEntered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request=new StartCollaborationRequest(root.Id,rootHash,secondPlanHash,negativePolicy.Revision,negativePolicy.PolicySha256);
        using(var started=await Post(client,aetCollabPath,request))
            Check(started.StatusCode==HttpStatusCode.Accepted,"negative fixture collaboration admitted once: "+scenario);
        if(scenario is "revoke" or "cancel")
        {
            await softwareModel.RoutineEntered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            var pending=negativeRepairStore.Latest(taskBound.Id)!;
            Check(pending.Cycle.State=="Running" && pending.Cycle.BaseRunId==root.Id &&
                QaReviewStore.Fingerprint(pending.Engineering.Files)==rootHash,
                "paused candidate is an unchanged clone before "+scenario);
            if(scenario=="revoke")
            {
                delegationStore.Revoke(taskBound.Id,new ApproveDelegationRequest(negativePolicy.Revision,
                    negativePolicy.PolicySha256,"REVOKE "+negativePolicy.PolicySha256));
                // Exercise the actual transaction guard, not only the
                // coordinator's cooperative cancellation behavior.
                try
                {
                    engineeringStore.CompleteTask(taskBound.Id,pending.Engineering.Job.Id,"timer-state",
                        new EngineeringChangeSet("Attempt to save after revocation.",
                            [new EngineeringFileChange("app.js","Timer behavior","console.log('forbidden late write');")],
                            ["Independent verification required."]));
                    Check(false,"revoked routine candidate must not persist source");
                }
                catch(StoreFailure e) when(e.Code=="DELEGATION_NOT_AUTHORIZED")
                {Check(true,"transaction rejects revoked routine write");}
            }
            else
            {
                using var stopped=await Post(client,"/api/tasks/"+taskBound.Id+"/repair-cycles/"+pending.Cycle.Id+"/cancel",new {});
                Check(stopped.StatusCode==HttpStatusCode.OK,"owner can stop active delegated repair");
            }
            softwareModel.RoutineGate!.TrySetResult(true);
            RepairCycleSnapshot? terminal=null;
            for(var i=0;i<600;i++)
            {
                terminal=negativeRepairStore.Get(taskBound.Id,pending.Cycle.Id);
                if(terminal.Cycle.State is "Failed" or "Interrupted")break;
                await Task.Delay(25);
            }
            Check(terminal?.Cycle.State==(scenario=="revoke"?"Failed":"Interrupted") &&
                terminal.Cycle.ErrorCode==(scenario=="revoke"?"DELEGATION_NOT_AUTHORIZED":"OWNER_CANCELLED") &&
                QaReviewStore.Fingerprint(terminal.Engineering.Files)==rootHash,
                "stopped routine repair cannot persist candidate source: "+scenario);
        }
        else
        {
            CollaborationSnapshot? result=null;
            for(var i=0;i<600;i++)
            {
                result=aetCollabStore.Latest(taskBound.Id);
                if(result?.Job.State is "Escalated" or "Failed" or "AwaitingVerification")break;
                await Task.Delay(25);
            }
            var expected=scenario=="budget"?"ROUTINE_REPAIR_BUDGET_EXHAUSTED":"POTENTIAL_CANDIDATE_REGRESSION";
            Check(result?.Job.State=="Escalated" && result.Job.Reason==expected,
                "autonomy stops on concrete boundary: "+scenario+" ("+result?.Job.Reason+")");
            var candidate=engineeringStore.Latest(taskBound.Id)!;
            var progress=new RepairProgressStore(tasks).Read(taskBound.Id,candidate.Job.Id);
            Check(progress.Attempts.Count==1 &&
                progress.Attempts[0].OriginalRunId==root.Id &&
                (scenario!="regression" || progress.PotentialRegressionIssueIds is {Count:>0}),
                "no second candidate is admitted after "+scenario);
            if(scenario=="regression")
            {
                var evidence=new DeliveryEvidenceStore(tasks).Read(taskBound.Id);
                Check(evidence.RoutineCandidate is {Disposition:"REJECTED_POTENTIAL_REGRESSION"} &&
                    evidence.RoutineCandidate.RunId==candidate.Job.Id &&
                    evidence.RoutineCandidate.BaseRunId==root.Id &&
                    evidence.RoutineCandidate.BaseSourceSha256==rootHash &&
                    evidence.Blockers.Contains("POTENTIAL_CANDIDATE_REGRESSION") &&
                    !evidence.DeliveryAllowed && !evidence.EngineeringComplete,
                    "rejected candidate is explicitly distinct from preserved predecessor and cannot claim delivery");
            }
        }
        Check(QaReviewStore.Fingerprint(engineeringStore.Get(taskBound.Id,root.Id).Files)==rootHash,
            "original snapshot remains immutable after "+scenario);
        if(scenario!="revoke")
            delegationStore.Revoke(taskBound.Id,new ApproveDelegationRequest(negativePolicy.Revision,
                negativePolicy.PolicySha256,"REVOKE "+negativePolicy.PolicySha256));
        softwareModel.RoutineGate=null;
        softwareModel.IntroduceRoutineRegression=false;
    }

    // Smoke evidence must stay distinct from qualified runtime verification.
    var aetSmokeRun=engineeringStore.Start(taskBound.Id,approvedEngineerPlan);
    engineeringStore.StartTask(taskBound.Id,aetSmokeRun.Id,"timer-interface");
    engineeringStore.CompleteTask(taskBound.Id,aetSmokeRun.Id,"timer-interface",
        new EngineeringChangeSet("Saved complete local browser entry.",
            [new EngineeringFileChange("index.html","Entry","<!doctype html><html><body><button id='start'>Start</button><script src='app.js'></script></body></html>")],
            ["Independently verify browser interaction."]));
    engineeringStore.StartTask(taskBound.Id,aetSmokeRun.Id,"timer-state");
    engineeringStore.CompleteTask(taskBound.Id,aetSmokeRun.Id,"timer-state",
        new EngineeringChangeSet("Saved linked local JavaScript behavior.",
            [new EngineeringFileChange("app.js","Timer behavior","document.getElementById('start').addEventListener('click',()=>{console.log('routine-one');console.log('routine-two');});")],
            ["Independently verify timer behavior."]));
    engineeringStore.Finish(taskBound.Id,aetSmokeRun.Id,true,null);
    var aetScopeRoot=engineeringPath+"/"+aetSmokeRun.Id;
    string aetSmokeScope;
    using(var aetScopeResponse=await client.GetAsync(aetScopeRoot+"/verification-scope"))
    using(var aetScopeBody=await Body(aetScopeResponse))
    {
        Check(aetScopeResponse.StatusCode==HttpStatusCode.OK &&
            aetScopeBody.RootElement.GetProperty("status").GetString()=="SCOPE_CANDIDATE",
            "existing exact smoke scope can be admitted without runtime qualification");
        aetSmokeScope=aetScopeBody.RootElement.GetProperty("scopeSha256").GetString()!;
    }
    using(var aetNoConsent=await Post(client,aetScopeRoot+"/source-smoke",new StartSourceSmokeRequest(aetSmokeScope,false)))
        Check(aetNoConsent.StatusCode==HttpStatusCode.Conflict && aetSmokeRunner.Calls==0,
            "durable smoke ledger does not bypass explicit isolation acknowledgement");
    string aetSmokeEvidenceId;
    using(var aetSmoke=await Post(client,aetScopeRoot+"/source-smoke",new StartSourceSmokeRequest(aetSmokeScope,true)))
    using(var aetSmokeBody=await Body(aetSmoke))
    {
        Check(aetSmoke.StatusCode==HttpStatusCode.OK && !aetSmokeBody.RootElement.GetProperty("verified").GetBoolean() &&
            aetSmokeBody.RootElement.GetProperty("current").GetBoolean() && aetSmokeRunner.Calls==1,
            "authenticated injected smoke result remains explicitly not verified");
        aetSmokeEvidenceId=aetSmokeBody.RootElement.GetProperty("evidenceId").GetString()!;
    }
    var aetSmokeStore=new SourceSmokeEvidenceStore(tasks);
    Check(aetSmokeStore.List(taskBound.Id,aetSmokeRun.Id).Single() is
        {State:"Recorded",Current:true,Verified:false,Report.Status:"SMOKE_PASS_NOT_VERIFIED"} aetSavedSmoke &&
        aetSavedSmoke.Id==aetSmokeEvidenceId,"actual smoke result survives reread and preserves exact source scope");

    // AET-10: the actual persisted smoke pass is not delivery evidence.
    var deliveryPath="/api/tasks/"+taskBound.Id+"/delivery-evidence";
    using(var deliveryAnonymous=new HttpClient {BaseAddress=client.BaseAddress})
    using(var deliveryDenied=await deliveryAnonymous.GetAsync(deliveryPath))
        Check(deliveryDenied.StatusCode==HttpStatusCode.Unauthorized,"delivery evidence is owner private");
    using(var deliveryResponse=await client.GetAsync(deliveryPath))
    using(var deliveryBody=await Body(deliveryResponse))
    {
        var evidence=deliveryBody.RootElement;
        Check(deliveryResponse.StatusCode==HttpStatusCode.OK &&
            evidence.GetProperty("runId").GetString()==aetSmokeRun.Id &&
            evidence.GetProperty("implementationComplete").GetBoolean() &&
            !evidence.GetProperty("engineeringComplete").GetBoolean() &&
            !evidence.GetProperty("productAccepted").GetBoolean() &&
            !evidence.GetProperty("deliveryAllowed").GetBoolean(),
            "saved source completion is distinct from engineering and product completion");
        Check(evidence.GetProperty("smoke").EnumerateArray().Single().GetProperty("status").GetString()=="SMOKE_PASS_NOT_VERIFIED" &&
            evidence.GetProperty("blockers").EnumerateArray().Any(x=>x.GetString()=="QUALIFIED_VERIFICATION_REQUIRED") &&
            evidence.GetProperty("work").GetArrayLength()==2 &&
            evidence.GetProperty("artifacts").GetArrayLength()==2 &&
            evidence.GetProperty("verification").GetProperty("buildStatus").GetString()=="NOT_VERIFIED" &&
            evidence.GetProperty("verification").GetProperty("functionalStatus").GetString()=="NOT_VERIFIED" &&
            evidence.GetProperty("verification").GetProperty("runtimeQualificationStatus").GetString()=="UNQUALIFIED",
            "retrospective reports actual smoke evidence and approved work without treating smoke as verification");
    }

    // Changing a decision after job admission invalidates file writes and
    // terminal success *inside the same database transaction*.
    // AET-210: scope is optional, exact, bounded and included in the
    // approved policy digest. A proposal alone is never a repair admission.
    var routineScope = new RoutineRepairScope(aetSmokeRun.Id,
        QaReviewStore.Fingerprint(engineeringStore.Get(taskBound.Id,aetSmokeRun.Id).Files),
        ["index.html","app.js"], 4);
    foreach (var invalidScope in new[] {
        routineScope with { MaximumRepairs = 0 },
        routineScope with { MaximumRepairs = 9 },
        routineScope with { Paths = ["app.js","APP.js"] },
        routineScope with { Paths = ["missing.js"] },
        routineScope with { BaselineSourceSha256 = new string('0',64) }
    })
    {
        try
        {
            delegationStore.Propose(taskBound.Id,new ProposeDelegationRequest(routineRevision,secondPlanHash,routineOps,invalidScope));
            Check(false,"invalid routine repair scope cannot be proposed");
        }
        catch(StoreFailure e) when(e.Code is "INVALID_INPUT" or "REPAIR_SCOPE_VIOLATION" or "SOURCE_CHANGED")
        { Check(true,"invalid routine repair scope rejected"); }
    }
    try
    {
        delegationStore.Propose(taskBound.Id,new ProposeDelegationRequest(routineRevision,secondPlanHash,["SendMessage"],routineScope));
        Check(false,"routine scope without required role permissions cannot be proposed");
    }
    catch(StoreFailure e) when(e.Code=="INVALID_INPUT")
    { Check(true,"routine scope requires explicit source and independent review operations"); }
    var stalePolicy = delegationStore.Propose(taskBound.Id,
        new ProposeDelegationRequest(routineRevision,secondPlanHash,routineOps,routineScope));
    Check(stalePolicy.RoutineRepair is { MaximumRepairs:4 } &&
        stalePolicy.RoutineRepair.Paths.SequenceEqual(["app.js","index.html"]) &&
        delegationStore.Read(taskBound.Id)?.RoutineRepair?.BaselineRunId==aetSmokeRun.Id &&
        stalePolicy.Status=="Proposed","routine repair scope is canonical, durable and initially unapproved");
    stalePolicy = delegationStore.Approve(taskBound.Id,new ApproveDelegationRequest(
        stalePolicy.Revision,stalePolicy.PolicySha256,"APPROVE " + stalePolicy.PolicySha256));
    Check(stalePolicy.Current && stalePolicy.EffectiveStatus == "Approved" &&
          stalePolicy.RoutineRepair?.MaximumRepairs == 4,
        "delegation explicitly active before architecture revision");
    // Two actual bounded delegated source repairs, no owner triage or
    // per-finding repair POST. A lead finding and then an exact QA criterion
    // drive the same persisted candidate/recheck workflow.
    softwareModel.RoutineMode=true;
    aetLeadModel.RoutineMode=true;
    aetIssueVerifier.RoutineMode=true;
    qaModel.RoutineMode=true;
    var routineRequest=new StartCollaborationRequest(aetSmokeRun.Id,
        routineScope.BaselineSourceSha256,secondPlanHash,stalePolicy.Revision,stalePolicy.PolicySha256);
    string routineCollabId;
    using(var admittedRoutine=await Post(client,aetCollabPath,routineRequest))
    using(var routineBody=await Body(admittedRoutine))
    {
        Check(admittedRoutine.StatusCode==HttpStatusCode.Accepted,"owner starts exact delegated coordination once");
        routineCollabId=routineBody.RootElement.GetProperty("id").GetString()!;
    }
    CollaborationSnapshot? routineResult=null;
    for(var i=0;i<600;i++)
    {
        routineResult=aetCollabStore.Latest(taskBound.Id);
        if(routineResult?.Job.State is "AwaitingVerification" or "Escalated" or "Failed") break;
        await Task.Delay(25);
    }
    Check(routineResult?.Job.State=="AwaitingVerification" &&
        routineResult.Job.Id!=routineCollabId &&
        routineResult.Job.Reason=="QUALIFIED_VERIFICATION_REQUIRED",
        "two scoped routine repairs independently rechecked and stop at functional verification gate");
    var routineFinal=engineeringStore.Latest(taskBound.Id)!;
    var routineProgress=new RepairProgressStore(tasks).Read(taskBound.Id,routineFinal.Job.Id);
    Check(routineProgress.Attempts.Count==2 &&
        routineProgress.Attempts.All(a=>a.IndependentStaticResolution) &&
        routineProgress.Attempts[0].OriginalRunId==aetSmokeRun.Id &&
        routineProgress.Attempts[0].Origin=="LeadSoftwareEngineer" &&
        routineProgress.Attempts[1].Origin=="QaEngineer" &&
        routineFinal.Files.Single(f=>f.Path=="app.js").Content.Contains("routine-fixed-one") &&
        routineFinal.Files.Single(f=>f.Path=="app.js").Content.Contains("routine-fixed-two"),
        "two distinct independent findings are repaired and rechecked in a single owner-delegated lineage");
    var originalRoutineSource=engineeringStore.Get(taskBound.Id,aetSmokeRun.Id);
    Check(QaReviewStore.Fingerprint(originalRoutineSource.Files)==routineScope.BaselineSourceSha256 &&
        routineProgress.Attempts.All(a=>new EngineeringIssueStore(tasks).Read(taskBound.Id,a.OriginalRunId).Issues
            .Where(issue=>issue.RepairCycleId==a.CycleId).All(issue=>issue.TriageHistory.Count==0)),
        "routine repair retains immutable original source and does not fabricate owner triage");
    var routineQaCount=qaModel.ReviewedFiles.Count;
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
    using(var staleDelivery=await client.GetAsync(deliveryPath))
    using(var staleDeliveryBody=await Body(staleDelivery))
    {
        var evidence=staleDeliveryBody.RootElement;
        Check(staleDelivery.StatusCode==HttpStatusCode.OK &&
            !evidence.GetProperty("currentSource").GetBoolean() &&
            !evidence.GetProperty("implementationComplete").GetBoolean() &&
            !evidence.GetProperty("deliveryAllowed").GetBoolean() &&
            !evidence.GetProperty("smoke").EnumerateArray().Single().GetProperty("current").GetBoolean(),
            "architecture revision invalidates delivery and marks prior smoke evidence historical");
    }
    var effectivePolicy = delegationStore.Read(taskBound.Id);
    Check(effectivePolicy?.Status == "Approved" && !effectivePolicy.Current &&
          effectivePolicy.EffectiveStatus == "STALE_APPROVALS",
        "architecture revision invalidates effective delegated authority");
    Check(delegationStore.History(taskBound.Id).Count == routineRevision+1 &&
          delegationStore.History(taskBound.Id).Take(routineRevision).All(p => p.Status == "Revoked"),
        "delegation retains prior owner decisions");
    Check(delegationStore.Revoke(taskBound.Id,new ApproveDelegationRequest(
        stalePolicy.Revision,stalePolicy.PolicySha256,
        "REVOKE " + stalePolicy.PolicySha256)).Status == "Revoked",
        "owner may revoke stale delegation without reapproving architecture");
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
              qaModel.ReviewedFiles.Count == routineQaCount,
            "stale architecture cannot initiate new QA review of old source");
    Check(new EngineeringIssueStore(tasks).Read(taskBound.Id,resumedRunId).Issues
        .Single(x=>x.Id==aetLeadRepairedIssue.Id).Verifications.All(x=>!x.Current),
        "architecture revision invalidates previously recorded issue verification");
    Check(aetSmokeStore.List(taskBound.Id,aetSmokeRun.Id).Single() is {Current:false,Verified:false},
        "architecture revision makes historical smoke evidence stale without deleting it");

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
        Task.FromResult<IReadOnlyList<string>>(new[] { "qwen3.5:4b", "qwen2.5-coder:14b" });
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
    public Queue<string> TechnicalLeadReplies { get; } = new();
    public Queue<string> InterviewReplies { get; } = new();
    public Queue<string> SpecificationFailures { get; } = new();
    public List<string> SpecificationCorrectionPrompts { get; } = [];
    public List<string> SpecificationLastMessages { get; } = [];
    public List<int> SpecificationOwnerDecisionCounts { get; } = [];
    public List<string> InterviewCorrectionPrompts { get; } = [];
    public List<string> TechnicalLeadCorrectionPrompts { get; } = [];
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
            if (purpose == MafAgentPurpose.Interview)
                parent.InterviewCorrectionPrompts.AddRange(history
                    .Where(m => m.Role == ChatRole.User &&
                        m.Text?.Contains("previous response either repeated",
                            StringComparison.OrdinalIgnoreCase) == true)
                    .Select(m => m.Text!));
            if (purpose == MafAgentPurpose.Specification)
            {
                parent.SpecificationLastMessages.Add(history.Last().Text ?? "");
                parent.SpecificationOwnerDecisionCounts.Add(history.Count(m =>
                    m.Role == ChatRole.User &&
                    m.Text?.StartsWith("Owner requirement decision ",
                        StringComparison.Ordinal) == true));
                parent.SpecificationCorrectionPrompts.AddRange(history
                    .Where(m => m.Role == ChatRole.User &&
                        m.Text?.Contains("previous response was empty",
                            StringComparison.OrdinalIgnoreCase) == true)
                    .Select(m => m.Text!));
                if (parent.SpecificationFailures.Count > 0)
                    throw new StoreFailure(parent.SpecificationFailures.Dequeue());
            }
            if (purpose == MafAgentPurpose.TechnicalLead)
                parent.TechnicalLeadCorrectionPrompts.AddRange(history
                    .Where(m => m.Role == ChatRole.User &&
                        m.Text?.Contains("Reason:", StringComparison.Ordinal) == true)
                    .Select(m => m.Text!));
            if (purpose == MafAgentPurpose.Interview &&
                parent.InterviewReplies.Count > 0)
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant, parent.InterviewReplies.Dequeue())));
            if (purpose == MafAgentPurpose.TechnicalLead &&
                parent.TechnicalLeadReplies.Count > 0)
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant, parent.TechnicalLeadReplies.Dequeue())));
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
    public bool RoutineMode { get; set; }
    public bool IntroduceRoutineRegression { get; set; }
    public TaskCompletionSource<bool>? RoutineGate { get; set; }
    public TaskCompletionSource<bool>? RoutineEntered { get; set; }
    private async Task<EngineeringChangeSet> WaitForRoutineGate(EngineeringChangeSet changes,CancellationToken token)
    {
        RoutineEntered?.TrySetResult(true);
        if(RoutineGate is not null)await RoutineGate.Task.WaitAsync(token);
        return changes;
    }
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
        if (input.Repair is { } repair)
        {
            if (repair.Findings.Count != 1 ||
                repair.AllowedPaths.Single() != "app.js" ||
                !input.ExistingFiles.Any(f => f.Path == "app.js"))
                throw new Exception("Repair context was not bound to original QA source");
            // End-to-end acceptance uses the same exact-edit materialization
            // as the actual MAF agent, then persists its COMPLETE validated
            // replacement through the existing SQLite/QA coordinator.
            var source = input.ExistingFiles.Single(f => f.Path == "app.js");
            var before = source.Content.TrimEnd('\r', '\n');
            var after = RoutineMode
                ? repair.Findings.Single().Evidence.Contains("routine-one",StringComparison.Ordinal)
                    ? before.Replace("routine-one","routine-fixed-one",StringComparison.Ordinal)
                    : before.Replace("routine-two","routine-fixed-two",StringComparison.Ordinal)
                : before.Replace("console.log('start')",
                    "console.log('repaired start')", StringComparison.Ordinal);
            if (after == before)
                throw new Exception("Fixture repair did not find exact source");
            if(RoutineMode && IntroduceRoutineRegression)
                after+="\nconsole.log('routine-introduced-regression');";
            var edits = JsonSerializer.Serialize(new {
                summary = "Repaired the approved Start handler without modifying browser entry.",
                edits = new[] { new {
                    path = source.Path, oldText = before, newText = after
                } },
                verificationNotes = new[] { "Independently check Start button behavior" }
            });
            var changes=MafSoftwareEngineer.ApplyRepairEdits(edits, repair, input.ExistingFiles);
            return RoutineMode?WaitForRoutineGate(changes,cancellation):Task.FromResult(changes);
        }
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
    public Queue<string> Responses { get; } = new();
    public Queue<string> Failures { get; } = new();
    public List<string> CorrectionReasons { get; } = [];
    public List<string> UserPrompts { get; } = [];
    public List<string> AtlasObservations { get; } = [];
    public int Attempts { get; private set; }
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
            parent.Attempts++;
            if (parent.Failures.Count > 0)
                throw new StoreFailure(parent.Failures.Dequeue());
            var history = messages.ToArray();
            parent.UserPrompts.AddRange(history.Where(m => m.Role == ChatRole.User)
                .Select(m => m.Text ?? ""));
            parent.NoTools = options?.Tools is null || options.Tools.Count == 0;
            parent.AtlasObservations.AddRange(history.Where(m =>
                m.Role == ChatRole.User && m.Text?.StartsWith(
                    "HOST CODE ATLAS READ-ONLY RESULT",
                    StringComparison.Ordinal) == true).Select(m => m.Text!));
            parent.CorrectionReasons.AddRange(history
                .Where(m => m.Role == ChatRole.User &&
                    m.Text?.Contains("Reason:", StringComparison.Ordinal) == true)
                .Select(m => m.Text!));
            if (parent.Responses.Count > 0)
                return Task.FromResult(new ChatResponse(new ChatMessage(
                    ChatRole.Assistant, parent.Responses.Dequeue())));
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
    public bool RoutineMode { get; set; }
    public List<string> ReviewedFiles { get; } = [];
    public List<string> ReviewedSourceStates { get; } = [];
    public List<string> RepairFocusPaths { get; } = [];

    public Task<QaFileAssessment> ReviewAsync(
        QaReviewInput input, CancellationToken cancellation)
    {
        if (!QaReviewStore.IsReviewableSource(
                input.SourceRun.State, input.AllFiles.Count) ||
            input.Plan.Status != "Approved" || !input.Plan.Current ||
            input.Plan.PlanSha256 != input.SourceRun.PlanSha256 ||
            input.Plan.Model != "qwen3.5:4b" ||
            !input.Architecture.ReadyForSourceGeneration ||
            input.Architecture.ArchitectureSha256 != input.Plan.ArchitectureSha256 ||
            input.AllFiles.Count is < 1 or > 2 ||
            !input.AllFiles.Any(f => f.Path == input.File.Path &&
                f.Sha256 == input.File.Sha256))
            throw new Exception("Independent QA received wrong source or stale approval");
        ReviewedFiles.Add(input.File.Path);
        ReviewedSourceStates.Add(input.SourceRun.State);
        if (input.RepairFocus is { Count: > 0 } focus)
        {
            if (input.File.Path != "app.js" ||
                focus.Any(f => f.Path != input.File.Path ||
                    f.Classification != "ImplementationDefect"))
                throw new Exception("Repair QA got foreign file scope or unconfirmed finding");
            RepairFocusPaths.Add(input.File.Path);
        }
        if(RoutineMode)
            return Task.FromResult(new QaFileAssessment("Independent static routine fixture review.",
                input.File.Path=="app.js" && input.File.Content.Contains("routine-two",StringComparison.Ordinal)
                    ? [new QaFinding("Major","Start advances the displayed timer",
                        "console.log('routine-two')","Replace the second fixture defect in the exact source.")]
                    : [],
                ["Check timer behavior in a separately authorized runtime test."]));
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
    public bool InspectionContextSeen { get; private set; }
    public bool RepairFocusSeen { get; private set; }
    public bool NoTools { get; private set; }
    public bool HostObservationSeen { get; private set; }
    public Queue<string> ScriptedReplies { get; } = new();

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
            parent.InspectionContextSeen = history.Any(m =>
                m.Role.Value == "user" &&
                m.Text?.Contains("EngineerAtlasInspected",
                    StringComparison.Ordinal) == true &&
                m.Text.Contains("0123456789ab", StringComparison.Ordinal));
            parent.RepairFocusSeen |= history.Any(m =>
                m.Role.Value == "user" &&
                m.Text?.Contains("OWNER-CONFIRMED DEFECTS", StringComparison.Ordinal) == true &&
                m.Text.Contains("NOT proof of a fix", StringComparison.Ordinal) &&
                m.Text.Contains("Save button is not connected", StringComparison.Ordinal));
            parent.NoTools = options?.Tools is null || options.Tools.Count == 0;
            parent.HostObservationSeen |= history.Any(m =>
                m.Role.Value == "user" &&
                m.Text?.Contains("HOST QA CODE ATLAS OBSERVATION",
                    StringComparison.Ordinal) == true);
            var answer = parent.ScriptedReplies.Count > 0
                ? parent.ScriptedReplies.Dequeue()
                : """{"summary":"The static source exposes an interactive control but its behavior is unknown.","findings":[{"severity":"Minor","requirement":"The Save button must work","evidence":"A button is declared but no listener is present in this source","recommendation":"Inspect linked scripts and test saving notes in a browser"}],"manualChecks":["Click Save and reload the page in a real browser"]}""";
            return Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, answer)));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("QA only allows bounded nonstreaming turns.");
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}

sealed class AetLeadReviewFixture : ILeadSourceReviewer
{
    public bool RoutineMode { get; set; }
    public Task<LeadFileAssessment> ReviewAsync(QaReviewInput input,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(input.AgentModel!="qwen3.5:4b" || input.SourceRun.PlanSha256!=input.Plan.PlanSha256)
            throw new Exception("Unexpected independent review scope");
        if(RoutineMode && input.File.Path=="app.js" &&
            input.File.Content.Contains("routine-introduced-regression",StringComparison.Ordinal))
            return Task.FromResult(new LeadFileAssessment("ChangesRequested","New blocking fixture regression in candidate.",
                [new LeadFinding("Major","Correctness","app.js",2,"console.log('routine-introduced-regression')",
                    "Candidate introduced a new blocking marker.","Preserve predecessor and escalate the candidate.",
                    "Independently verify before accepting any candidate.")]));
        if(RoutineMode)
            return Task.FromResult(input.File.Path=="app.js" && input.File.Content.Contains("routine-one",StringComparison.Ordinal)
                ? new LeadFileAssessment("ChangesRequested","First exact routine fixture concern.",
                    [new LeadFinding("Major","Correctness","app.js",1,"console.log('routine-one')",
                        "First fixture marker requires correction.","Replace the first fixture marker.",
                        "Independently recheck the exact changed source.")])
                : new LeadFileAssessment("Approved","No remaining lead fixture concern.",[]));
        if(input.File.Path!="app.js")
            return Task.FromResult(new LeadFileAssessment("Approved","No static concern in this fixture file.",[]));
        var lines=input.File.Content.Split('\n');
        var line=Array.FindIndex(lines,l=>!string.IsNullOrWhiteSpace(l));
        var evidence=lines[line].Trim();
        if(evidence.Length>100)evidence=evidence[..100];
        return Task.FromResult(new LeadFileAssessment("ChangesRequested","Injected static architectural review concern.",
            [new LeadFinding("Major","Correctness",input.File.Path,line+1,evidence,
            "The timer fixture requires independent behavior verification.",
            "Inspect and correct the timer implementation.","Verify the timer updates in an independent check.")]));
    }
}

sealed class AetIssueVerifierFixture : IEngineeringIssueVerifier
{
    public bool RoutineMode { get; set; }
    public Task<IssueVerificationAssessment> VerifyAsync(IssueVerificationInput input,string model,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(RoutineMode)
        {
            var excerpt=input.Issue.Origin=="QaEngineer"?"console.log('routine-fixed-two')":"console.log('routine-fixed-one')";
            if(model!="qwen3.5:4b" || input.Before.Sha256==input.After.Sha256 ||
                !input.After.Content.Contains(excerpt,StringComparison.Ordinal))
                throw new Exception("Routine fixture recheck did not receive expected candidate");
            return Task.FromResult(new IssueVerificationAssessment("ResolvedByStaticEvidence",
                "Exact fixture marker changed; runtime verification is still required.",1,excerpt,null));
        }
        if(model!="qwen3.5:4b" || input.Before.Sha256==input.After.Sha256 ||
            !input.After.Content.Contains("console.log('repaired start')",StringComparison.Ordinal))
            throw new Exception("Fixture verifier received wrong role model or source revision");
        return Task.FromResult(new IssueVerificationAssessment(
            input.Issue.Origin=="QaEngineer"?"StillPresent":"ResolvedByStaticEvidence",
            "Injected source recheck records the exact changed construct; functionality remains untested.",
            1,"console.log('repaired start')",null));
    }
}

sealed class AetCollaborationLeadFixture:ICollaborationLead
{
    public int Calls{get;private set;}
    public Task<CollaborationDecision> DecideAsync(CollaborationInput input,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();Calls++;
        var decision=input.AllowedActions.Contains("ReviewCode")?
            new CollaborationDecision("ReviewCode","Request independent review of the exact generated source."):
            input.AllowedActions.Contains("RecheckIssue")?
            new("RecheckIssue","Independently recheck the original finding against the repaired source.",
                input.RecheckCandidates!.Single().Id):
            input.AllowedActions.Contains("ClassifyIssue")?
            new("ClassifyIssue","The actual source concern needs a scoped correction.",
                input.Issues.Single(x=>x.State=="NeedsTriage").Id,"ImplementationDefect"):
            input.AllowedActions.Contains("RequestOwnerRepair")?
            new("RequestOwnerRepair","Request exact scoped repair for the confirmed finding.",
                input.Issues.Single(x=>x.State=="ConfirmedDefect").Id):
            input.AllowedActions.Contains("ReviewAcceptance")?
            new("ReviewAcceptance","Request independent acceptance review of this revision."):
            input.AllowedActions.Contains("AwaitQualifiedVerification")?
            new("AwaitQualifiedVerification","Independent runtime evidence remains required."):
            new("Escalate","No safe further source correction is currently admitted.");
        return Task.FromResult(decision);
    }
}

sealed class AetSmokeFixture:ILocalSourceSmokeRunner
{
    public int Calls{get;private set;}
    public Task<LocalSourceSmokeResult> RunAsync(byte[] sourceZip,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();Calls++;
        using var zip=new ZipArchive(new MemoryStream(sourceZip),ZipArchiveMode.Read);
        if(zip.Entries.Select(e=>e.FullName).Order().SequenceEqual(new[]{"app.js","index.html"})==false)
            throw new Exception("Smoke received incorrect source archive");
        var checks=new[]{"exact_source_hashes","non_root","no_host_mounts","root_write_denied","network_denied",
            "javascript_syntax","local_assets_linked","browser_rendered","source_integrity_unchanged"}
            .ToDictionary(x=>x,_=>true);
        return Task.FromResult(new LocalSourceSmokeResult("SMOKE_PASS_NOT_VERIFIED",9,9,true,
            "DISABLED_CONTAINER_ONLY",null,checks));
    }
}
