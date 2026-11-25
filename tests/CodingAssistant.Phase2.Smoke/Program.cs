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

// Native MAF skill API compatibility probe. No provider is installed in
// production; the probe uses only in-memory text and a deterministic client.
{
    var nativeSkill = new Microsoft.Agents.AI.AgentInlineSkill(
        name: "compatibility-probe",
        description: "Model-free native skill API boundary probe",
        instructions: "Read the pinned instructions as advisory text only.");
    var nativeSkillContent = await nativeSkill.GetContentAsync(CancellationToken.None);
    Check(nativeSkillContent.Contains("Read the pinned instructions as advisory text only.",
        StringComparison.Ordinal), "native MAF inline skill API preserves the instruction text");
    var nativeProbeFactory = new Phase3FakeMafFactory();
    using var nativeProbeClient = nativeProbeFactory.Create("qwen3.5:4b", MafAgentPurpose.Interview);
    var nativeProbeAgent = new Microsoft.Agents.AI.ChatClientAgent(nativeProbeClient,
        new Microsoft.Agents.AI.ChatClientAgentOptions {
            Name = "NativeSkillCompatibilityProbe",
            ChatOptions = new ChatOptions { Instructions = "You are a model-free compatibility probe." },
            AIContextProviders = [new Microsoft.Agents.AI.AgentSkillsProvider(nativeSkill)]
        });
    _ = await nativeProbeAgent.RunAsync(
        new[] { new ChatMessage(ChatRole.User, "Inspect the native skill declaration.") },
        cancellationToken: CancellationToken.None);
    Check(nativeProbeFactory.AllowedToolsSeen > 0 &&
        nativeProbeFactory.LastNativeToolOptions?.Tools is { Count: > 0 },
        "native MAF provider introduces callable tools even for instruction-only skills");
    using var nativeRejectedClient = new LocalMafChatClient("qwen3.5:4b", MafAgentPurpose.Interview);
    // Only enter the real adapter after proving tool options are present;
    // its first validation rejects before creating an HTTP request.
    var nativeRejected = false;
    try
    {
        _ = await nativeRejectedClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "Compatibility check; never send this request.")],
            nativeProbeFactory.LastNativeToolOptions!, CancellationToken.None);
    }
    catch (StoreFailure error) { nativeRejected = error.Code == "INVALID_INPUT"; }
    Check(nativeRejected, "actual local Ollama adapter rejects native skill tools before transport");
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

// Acceptance identities and consolidation are deterministic and do not turn
// static silence or model assertions into runtime evidence.
{
    var evidencePlan = new ImplementationPlan(new string('a', 32), 2, 1,
        new string('b', 64), new string('c', 64), "qwen3.5:4b",
        new ImplementationOutline("Bounded acceptance evidence fixture.",
            [new ImplementationWorkItem("save", "Save notes", "Implement saving.", [],
                ["Save notes across browser restarts", "Handle invalid input"]),
             new ImplementationWorkItem("edit", "Edit notes", "Implement editing.", ["save"],
                ["Handle invalid input", "Edit an existing note"])], [], []),
        new string('d', 64), "Superseded", "2026-11-21T00:00:00Z",
        "2026-11-21T00:01:00Z", false, "STALE_APPROVALS");
    var evidenceReview = new QaReviewRun(new string('e', 32), evidencePlan.TaskId,
        new string('f', 32), new string('1', 64), evidencePlan.Revision,
        evidencePlan.PlanSha256, evidencePlan.SpecificationVersion,
        evidencePlan.SpecificationSha256, evidencePlan.ArchitectureSha256,
        evidencePlan.Model, "Reviewed", "StaticReviewComplete", null,
        "2026-11-21T00:02:00Z", "2026-11-21T00:03:00Z");
    var saveConcern = new QaFinding("Minor", "Save notes across browser restarts",
        "The source does not restore saved notes on startup.",
        "Inspect the initialization path and independently test restart.", "save:1");
    var ambiguousConcern = new QaFinding("Major", "Handle invalid input",
        "The submitted form handler does not reject an empty string.",
        "Check the handler against empty input.");
    var invalidLink = ambiguousConcern with { CriterionId = "unknown:1" };
    var explicitLink = ambiguousConcern with { CriterionId = "edit:1" };
    var evidenceReports = new[] {
        new QaFileReport("app.js", new string('2', 64),
            new QaFileAssessment("Static observations only.",
                [saveConcern, saveConcern with { Severity = "Major" },
                    ambiguousConcern, explicitLink, invalidLink], ["Test saving notes in a browser."]),
            "2026-11-21T00:03:00Z"),
        new QaFileReport("index.html", new string('3', 64),
            new QaFileAssessment("Static observations only.", [saveConcern],
                ["Test saving notes in a browser."]), "2026-11-21T00:03:00Z")
    };
    // Ground evidence on the primary source alone, not a companion, model
    // explanation or source inventory. Invalid quotes remain hypotheses.
    const string primaryEvidenceSource = "const save = () => localStorage.setItem('notes', 'saved');";
    var grounded = QaAcceptanceProjection.Ground(evidencePlan, primaryEvidenceSource,
        new QaFileAssessment("Static observations only.",
            [saveConcern with { SourceExcerpt = "localStorage.setItem" },
             ambiguousConcern with { SourceExcerpt = "fabricated event handler" }],
            ["Test saving notes in a browser."],
            [new QaImplementationEvidence("save:1", "localStorage.setItem",
                "The source contains a persistence call."),
             new QaImplementationEvidence("save:1", "localStorage.setItem",
                "Duplicate observation of persistence."),
             new QaImplementationEvidence("unknown:1", "localStorage.setItem",
                "This criterion does not exist in the plan."),
             new QaImplementationEvidence("edit:2", "fabricated event handler",
                "This quotation is not part of the primary source.")]));
    Check(grounded.Findings[0].SourceExcerpt == "localStorage.setItem" &&
          grounded.Findings[1].SourceExcerpt is null &&
          grounded.ImplementationEvidence?.Count == 1,
        "literal source grounding discards fabricated evidence and unknown criterion links");
    var groundedProjection = QaAcceptanceProjection.Build(evidenceReview, evidencePlan,
        [new QaFileReport("app.js", new string('2', 64), grounded,
            "2026-11-21T00:03:00Z")]);
    var groundedCriterion = groundedProjection.Criteria.Single(c => c.Criterion.Id == "save:1");
    Check(groundedCriterion.StaticStatus == "EvidenceAndConcern" &&
          groundedCriterion.ImplementationIndicators.Single().SourceExcerpt == "localStorage.setItem" &&
          groundedCriterion.ImplementationIndicators.Single().Path == "app.js" &&
          groundedCriterion.VerificationStatus == "RequiresTesting",
        "positive static evidence can coexist with an unresolved concern without passing a test");
    var positiveOnly = QaAcceptanceProjection.Build(evidenceReview, evidencePlan,
        [new QaFileReport("app.js", new string('2', 64), grounded with { Findings = [] },
            "2026-11-21T00:03:00Z")]);
    Check(positiveOnly.Criteria.Single(c => c.Criterion.Id == "save:1").StaticStatus ==
          "ImplementationEvidence" &&
          positiveOnly.Criteria.All(c => c.VerificationStatus == "RequiresTesting"),
        "positive-only evidence never implies verified behavior");

    var crossCause = saveConcern with { SourceExcerpt = "localStorage.setItem" };
    var causeFile = new EngineeringSourceFile("app.js", "Persistence", primaryEvidenceSource,
        new string('2', 64), "save", "2026-11-21T00:03:00Z");
    var observation = new QaCrossFileObservation("save:1", "app.js",
        "localStorage.setItem", "The form depends on the companion storage handler.");
    var related = QaAcceptanceProjection.GroundCrossFile(evidencePlan, "index.html",
        [causeFile], new QaFileAssessment("Static cross-file observations.",
            [], ["Check the saved data in an independent browser."],
            CrossFileObservations: [observation,
                observation with { Explanation = "Repeated observation of the same cause." },
                observation with { CausePath = "../app.js" },
                observation with { CauseExcerpt = "fabricated handler" },
                observation with { CriterionId = "unknown:1" },
                observation with { CausePath = "index.html" }]));
    Check(related.CrossFileObservations?.Count == 1,
        "cross-file evidence requires exact admitted companion path, excerpt and criterion");
    var reconciled = QaAcceptanceProjection.Build(evidenceReview, evidencePlan,
        [new QaFileReport("app.js", causeFile.Sha256,
            new QaFileAssessment("Static cause review only.", [crossCause],
                ["Independently test restoration."]), "2026-11-21T00:03:00Z"),
         new QaFileReport("index.html", new string('3', 64), related,
            "2026-11-21T00:03:00Z")]);
    Check(reconciled.Findings.Count == 1 &&
          reconciled.Findings.Single().Path == "app.js" &&
          reconciled.CrossFileEvidence.Single().RelatedFindingIndex == 0 &&
          reconciled.CrossFileEvidence.Single().ObservedFromPath == "index.html" &&
          reconciled.Criteria.All(c => c.VerificationStatus == "RequiresTesting"),
        "cross-file observation links to the independently reviewed cause without adding a repair target");
    var unmatched = QaAcceptanceProjection.Build(evidenceReview, evidencePlan,
        [new QaFileReport("index.html", new string('3', 64), related,
            "2026-11-21T00:03:00Z")]);
    Check(unmatched.Findings.Count == 0 &&
          unmatched.CrossFileEvidence.Single().RelatedFindingIndex is null,
        "unresolved companion observation cannot create a repair finding");
    var ambiguousCause = QaAcceptanceProjection.Build(evidenceReview, evidencePlan,
        [new QaFileReport("app.js", causeFile.Sha256,
            new QaFileAssessment("Two distinct static concerns.",
                [crossCause, crossCause with { Recommendation = "Check a different restoration problem." }],
                ["Independently test restoration."]), "2026-11-21T00:03:00Z"),
         new QaFileReport("index.html", new string('3', 64), related,
            "2026-11-21T00:03:00Z")]);
    Check(ambiguousCause.Findings.Count == 2 &&
          ambiguousCause.CrossFileEvidence.Single().RelatedFindingIndex is null,
        "ambiguous cause matches remain unresolved and distinct findings are preserved");

    var evidence = QaAcceptanceProjection.Build(evidenceReview, evidencePlan, evidenceReports);
    Check(evidence.PlanSha256 == evidencePlan.PlanSha256 &&
          evidence.SourceSha256 == evidenceReview.SourceSha256 &&
          evidence.Criteria.Count == 4 && evidence.RawFindingCount == 6 &&
          evidence.Findings.Count == 4 && evidence.UnmappedFindingCount == 1,
        "acceptance projection binds historical plan and groups exact same-file observations");
    var savedGroup = evidence.Findings.Single(g => g.Path == "app.js" && g.CriterionId == "save:1");
    Check(savedGroup.Index == 1 && savedGroup.OriginalIndices.SequenceEqual([0, 1]) &&
          evidence.Findings.Any(g => g.Path == "index.html" && g.CriterionId == "save:1") &&
          evidence.Criteria.Single(c => c.Criterion.Id == "save:1").ConcernCount == 2 &&
          evidence.Criteria.Single(c => c.Criterion.Id == "edit:1").ConcernCount == 1 &&
          evidence.Criteria.All(c => c.VerificationStatus == "RequiresTesting") &&
          evidence.Criteria.Single(c => c.Criterion.Id == "edit:2").StaticStatus == "NoLinkedConcern",
        "duplicate group keeps exact repair index and differing file scope; no criterion passes");
    try
    {
        _ = QaAcceptanceProjection.Build(evidenceReview,
            evidencePlan with { PlanSha256 = new string('9', 64) }, evidenceReports);
        Check(false, "acceptance must reject another plan");
    }
    catch (StoreFailure error) when (error.Code == "SOURCE_CHANGED")
    {
        Check(true, "acceptance rejects mismatched plan identity");
    }
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

    // Static Web companion evidence is a new host-issued DATA message.
    // The existing assigned-file Code Atlas broker still denies cross-file
    // symbol IDs and arbitrary tools. Non-web sources remain out of scope.
    var webArch = arch with { ApprovedChoices = [
        new DraftArchitectureChoice("technology-stack", 1, new string('e', 64),
            "static-web", "Static Web", "Offline", "Manual DOM", "static-web")
    ] };
    var htmlFile = sourceFile with { Content =
        "<html><body><button id='save'>Save</button><script src='app.js'></script></body></html>" };
    var scriptFile = sourceFile with { Path = "app.js",
        Content = "document.getElementById('save').addEventListener('click', () => localStorage.setItem('notes', 'saved'));",
        Sha256 = new string('6', 64) };
    var styleFile = sourceFile with { Path = "styles.css",
        Content = "button { display: block; }", Sha256 = new string('5', 64) };
    var oversizedFile = sourceFile with { Path = "large.js",
        Content = "/*" + new string('z', 5000) + "*/", Sha256 = new string('4', 64) };
    var webInput = input with { Architecture = webArch, File = htmlFile,
        AllFiles = [htmlFile, scriptFile, styleFile, oversizedFile, privateFile] };
    var companionFactory = new Phase5FakeMafFactory();
    companionFactory.ScriptedReplies.Enqueue(
        """{"summary":"The linked script contains a Save handler; browser behavior remains unverified.","findings":[],"implementationEvidence":[{"criterionId":"qa:1","sourceExcerpt":"id='save'","explanation":"The primary file declares a named control."}],"crossFileObservations":[{"criterionId":"qa:1","causePath":"app.js","causeExcerpt":"localStorage.setItem","explanation":"The control depends on the companion persistence handler."},{"criterionId":"qa:1","causePath":"large.js","causeExcerpt":"zzzzzzzzzzzz","explanation":"This companion was omitted from the review context."},{"criterionId":"qa:1","causePath":"secrets.cs","causeExcerpt":"RestrictedDetails","explanation":"This non-web file was not admitted as context."}],"manualChecks":["Click Save and reload in an independent browser"]}""");
    var hostPreflight = new StaticWebPreflightReport(run.Id, run.State,
        QaReviewStore.Fingerprint(webInput.AllFiles), "POTENTIAL_ISSUES",
        "GENERATED_NOT_TESTED", 1, 1,
        [new StaticWebPreflightFinding("MISSING_LINKED_ASSET", "Error", htmlFile.Path,
            "Fixture source-text observation only.", "<script src='app.js'>", "app.js")]);
    var companionReport = await new MafQaEngineer(companionFactory).ReviewAsync(
        webInput with { StaticPreflight = hostPreflight }, CancellationToken.None);
    var companionEvidence = companionFactory.LastCompanionContext ?? "";
    Check(companionFactory.NoTools && companionFactory.StaticPreflightSeen &&
          companionReport.Findings.Count == 0 &&
          companionReport.ImplementationEvidence?.Single().SourceExcerpt == "id='save'" &&
          companionReport.CrossFileObservations?.Count == 1 &&
          companionReport.CrossFileObservations?.Single().CausePath == "app.js" &&
          companionEvidence.Contains("localStorage.setItem", StringComparison.Ordinal) &&
          companionEvidence.Contains("button { display: block; }", StringComparison.Ordinal) &&
          companionEvidence.Contains(scriptFile.Sha256, StringComparison.Ordinal) &&
          companionEvidence.Contains("large.js", StringComparison.Ordinal) &&
          !companionEvidence.Contains(new string('z', 100), StringComparison.Ordinal) &&
          companionEvidence.Contains("secrets.cs", StringComparison.Ordinal) &&
          !companionEvidence.Contains("RestrictedDetails", StringComparison.Ordinal) &&
          companionEvidence.Contains("2 full companion file(s)", StringComparison.Ordinal),
        "static-web QA receives bounded linked-file evidence and explicit omissions, not unrelated source");
    Check(factory.LastCompanionContext is null && !factory.StaticPreflightSeen,
        "non-static-web QA does not receive expanded companion context");
    try
    {
        _ = await new MafQaEngineer(companionFactory).ReviewAsync(
            webInput with { StaticPreflight = hostPreflight with { SourceSha256 = new string('0', 64) } },
            CancellationToken.None);
        Check(false, "QA must reject a preflight from another snapshot");
    }
    catch (StoreFailure error) when (error.Code == "SOURCE_CHANGED")
    {
        Check(true, "QA rejects preflight evidence with mismatched fingerprint");
    }
    try
    {
        _ = await new MafQaEngineer(companionFactory).ReviewAsync(
            webInput with { File = htmlFile with { Content = "different source" } },
            CancellationToken.None);
        Check(false, "primary file content must match saved review snapshot");
    }
    catch (StoreFailure error) when (error.Code == "INVALID_INPUT")
    {
        Check(true, "QA rejects changed primary source before review");
    }

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
    var missingAsset = escapes.Findings.Single(f => f.Code == "MISSING_LINKED_ASSET");
    Check(missingAsset.Path == "pages/index.html" &&
          missingAsset.SourceExcerpt == "<link rel='stylesheet' href='../missing.css'>" &&
          missingAsset.ReferencedPath == "missing.css" &&
          escapes.VerificationStatus == "GENERATED_NOT_TESTED",
        "deterministic preflight exposes exact source candidate and resolved missing asset without runtime claims");
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
        interviewModelOverride: new Phase2RejectUnexpectedInterview(),
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
        interviewModelOverride: new Phase2RejectUnexpectedInterview(),
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
    using (var anonymous = new HttpClient { BaseAddress = client.BaseAddress })
    using (var denied = await anonymous.GetAsync("/api/agents/skills"))
        Check(denied.StatusCode == HttpStatusCode.Unauthorized,
            "skill candidate catalog requires owner authentication");
    using (var catalogResponse = await client.GetAsync("/api/agents/skills"))
    using (var catalogBody = await Body(catalogResponse))
    {
        var skills = catalogBody.RootElement.GetProperty("skills");
        Check(catalogResponse.StatusCode == HttpStatusCode.OK &&
              skills.GetArrayLength() == 3 &&
              skills.EnumerateArray().All(skill =>
                  !skill.GetProperty("approved").GetBoolean() &&
                  !skill.GetProperty("active").GetBoolean() &&
                  skill.GetProperty("status").GetString() == "DISCOVERED_NOT_APPROVED" &&
                  skill.GetProperty("requiredToolCategories").GetArrayLength() == 0 &&
                  skill.GetProperty("contentSha256").GetString()!.Length == 64 &&
                  skill.GetProperty("instructions").GetString()!.Length > 30),
            "exact embedded skill candidates are inspectable, not approved or active");
    }
    var candidateSkills = SkillCatalog.Discover();
    Check(candidateSkills.All(skill =>
        EngineeringAgentRoster.Get(skill.Role).AllowedSkillScopes.Contains(skill.Scope)),
        "every skill candidate is mapped to an existing role scope");
    foreach (var malformed in new[] {
        "---\nname: bad\ndescription: !include file\n---\n\ntext\n",
        "---\nname: bad\ndescription: text\ntools: shell\n---\n\ntext\n",
        "---\nname: ../bad\ndescription: text\n---\n\ntext\n",
        "---\nname: bad\ndescription: text\n---\n\n---\n"
    })
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(malformed);
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var skillInputRejected = false;
        try { SkillCatalog.Validate(bytes, "bad", digest); }
        catch (StoreFailure) { skillInputRejected = true; }
        Check(skillInputRejected, "malformed or authority-bearing frontmatter rejected");
    }
    {
        var candidate = candidateSkills[0];
        var changed = System.Text.Encoding.UTF8.GetBytes(
            "---\nname: " + candidate.Name + "\ndescription: " +
            candidate.Description + "\n---\n\nChanged instructions\n");
        var skillInputRejected = false;
        try { SkillCatalog.Validate(changed, candidate.Name, candidate.ContentSha256); }
        catch (StoreFailure) { skillInputRejected = true; }
        Check(skillInputRejected, "changed skill text fails the reviewed content hash");
    }
    // Fixed embedded reference loading: no path resolution or arbitrary files.
    {
        var referenceCandidate = candidateSkills.Single(s => s.Name == SkillReviewStore.QaSkill);
        var fixedReference = referenceCandidate.References!.Single();
        Check(referenceCandidate.Version == "2" &&
            referenceCandidate.SkillFileSha256 ==
                "a44462505576db34ccf61b57aa9952281828ced76402111389007c50f5f391e6" &&
            referenceCandidate.ContentSha256 ==
                "057bbd8f17179ac760ea38eec4255700419e5fc673d785e0809ab5ccc00e86d4" &&
            SkillCatalog.PackageHash(referenceCandidate.SkillFileSha256!,
                fixedReference.RelativePath, fixedReference.ContentSha256) == referenceCandidate.ContentSha256 &&
            candidateSkills.Where(s => s.Name != SkillReviewStore.QaSkill)
                .All(s => s.Version == "1" && s.References is null),
            "reference package has exact file hashes and does not alter unrelated approvals");
        var referenceBytes = System.Text.Encoding.UTF8.GetBytes(fixedReference.Text + "\n");
        Check(SkillCatalog.ValidateReference(referenceBytes, fixedReference.RelativePath,
            fixedReference.ContentSha256) == fixedReference.Text,
            "approved embedded reference is bounded canonical text");
        foreach (var disallowedReference in new[] { "../review-checklist.md",
            "/references/review-checklist.md", "references/../references/review-checklist.md",
            "references\\review-checklist.md", "C:/review-checklist.md",
            "references/%2e%2e/review-checklist.md", "scripts/run.ps1", "references/other.md" })
        {
            var referenceRejected = false;
            try { SkillCatalog.ValidateReference(referenceBytes, disallowedReference,
                fixedReference.ContentSha256); }
            catch (StoreFailure failure) { referenceRejected = failure.Code == "SKILL_SCOPE_DENIED"; }
            Check(referenceRejected, "reference path outside exact allowlist rejected");
        }
        foreach (var alteredReference in new[] {
            System.Text.Encoding.UTF8.GetBytes(fixedReference.Text + "\nchanged"),
            new byte[] { 0xff }, new byte[4097] })
        {
            var referenceRejected = false;
            try { SkillCatalog.ValidateReference(alteredReference, fixedReference.RelativePath,
                fixedReference.ContentSha256); }
            catch (StoreFailure) { referenceRejected = true; }
            Check(referenceRejected, "changed, invalid or oversized reference rejected");
        }
        var oldReferenceDescriptor = referenceCandidate with {
            Version = "1", SourceCommit = "e12ea7c3b86b03abef633ddc7003d0e284722b89",
            SourceUrl = "https://github.com/dvdhtzr/CodingAssistant/blob/e12ea7c3b86b03abef633ddc7003d0e284722b89/src/CodingAssistant.Api/Skills/static-web-verification/SKILL.md",
            ContentSha256 = referenceCandidate.SkillFileSha256!, SkillFileSha256 = null,
            Compatibility = "MAF local Ollama instruction-only candidate; live activation not qualified",
            References = null };
        var oldReferenceReviewHash = SkillReviewStore.ReviewSha256(taskBound.Id, oldReferenceDescriptor);
        var newReferenceReviewHash = SkillReviewStore.ReviewSha256(taskBound.Id, referenceCandidate);
        Check(oldReferenceReviewHash != newReferenceReviewHash,
            "new reference package invalidates old QA approval identity");
        using (var oldReferenceApproval = await Post(client,
            "/api/tasks/" + taskBound.Id + "/skill-reviews/" + referenceCandidate.Name,
            new SkillReviewRequest(1, 0, oldReferenceReviewHash, "ApproveLocalUse",
                "APPROVE LOCAL USE " + oldReferenceReviewHash, true)))
            Check(oldReferenceApproval.StatusCode == HttpStatusCode.Conflict,
                "prior instruction-only review cannot approve expanded QA package");
    }
    // Skill review is project-scoped, explicitly owner-confirmed and never activation.
    {
        var skillReviewRoute = "/api/tasks/" + taskBound.Id + "/skill-reviews";
        var projectSkillReviewStore = new SkillReviewStore(tasks);
        var initialSkill = projectSkillReviewStore.Read(taskBound.Id)[0];
        var skillPath = skillReviewRoute + "/" + initialSkill.Skill.Name;
        var approval = new SkillReviewRequest(1, 0, initialSkill.ReviewSha256,
            "ApproveLocalUse", "APPROVE LOCAL USE " + initialSkill.ReviewSha256, true);
        Check(initialSkill.Revision == 0 && !initialSkill.Active &&
            !initialSkill.ApprovedForLocalInstructionReview,
            "skill review starts unapproved and inactive");
        using (var anonymousSkillReviewClient = new HttpClient { BaseAddress = client.BaseAddress })
        {
            using var hiddenSkillReview = await anonymousSkillReviewClient.GetAsync(skillReviewRoute);
            using var deniedSkillReview = await Post(anonymousSkillReviewClient, skillPath, approval);
            Check(hiddenSkillReview.StatusCode == HttpStatusCode.Unauthorized &&
                deniedSkillReview.StatusCode == HttpStatusCode.Unauthorized,
                "skill reviews require authentication for reads and writes");
        }
        foreach (var invalidReview in new[] {
            approval with { LocalUsePermissionConfirmed = false },
            approval with { Confirmation = "APPROVE" },
            approval with { Decision = "Activate" },
            approval with { SchemaVersion = 2 },
        })
        {
            using var deniedSkillReview = await Post(client, skillPath, invalidReview);
            Check(!deniedSkillReview.IsSuccessStatusCode,
                "skill review rejects missing permission, confirmation or unknown action");
        }
        using (var wrongBinding = await Post(client, skillPath,
            approval with { ReviewSha256 = new string('0', 64) }))
            Check(wrongBinding.StatusCode == HttpStatusCode.Conflict,
                "wrong skill review fingerprint blocks approval");
        using (var unknownSkill = await Post(client, skillReviewRoute + "/unknown", approval))
            Check(unknownSkill.StatusCode == HttpStatusCode.NotFound,
                "unregistered skill cannot be reviewed");
        Check(projectSkillReviewStore.Read(taskBound.Id)[0].Revision == 0,
            "rejected reviews do not create events");
        using (var acceptedReview = await Post(client, skillPath, approval))
        using (var acceptedBody = await Body(acceptedReview))
        {
            Check(acceptedReview.StatusCode == HttpStatusCode.OK &&
                acceptedBody.RootElement.GetProperty("approvedForLocalInstructionReview").GetBoolean() &&
                !acceptedBody.RootElement.GetProperty("active").GetBoolean() &&
                acceptedBody.RootElement.GetProperty("revision").GetInt32() == 1,
                "owner approves only exact local instruction review, not activation");
        }
        var otherReviewTask = tasks.CreateTask(new CreateTaskRequest(
            "Skill review isolation", "Check independent project review state",
            Guid.NewGuid().ToString("N")));
        var otherSkill = projectSkillReviewStore.Read(otherReviewTask.Id)[0];
        Check(otherSkill.ReviewSha256 != initialSkill.ReviewSha256 &&
            !otherSkill.ApprovedForLocalInstructionReview,
            "approval never transfers to another project");
        using (var crossProject = await Post(client,
            "/api/tasks/" + otherReviewTask.Id + "/skill-reviews/" + initialSkill.Skill.Name,
            approval))
            Check(crossProject.StatusCode == HttpStatusCode.Conflict,
                "another project's exact review hash cannot authorize approval");
        Check(SkillReviewStore.ReviewSha256(taskBound.Id,
            initialSkill.Skill with { License = "different-license" }) != initialSkill.ReviewSha256 &&
            SkillReviewStore.ReviewSha256(taskBound.Id,
            initialSkill.Skill with { Version = "2" }) != initialSkill.ReviewSha256 &&
            SkillReviewStore.ReviewSha256(taskBound.Id,
            initialSkill.Skill with { Role = "QaEngineer" }) != initialSkill.ReviewSha256 &&
            SkillReviewStore.ReviewSha256(taskBound.Id,
            initialSkill.Skill with { ContentSha256 = new string('0', 64) }) != initialSkill.ReviewSha256,
            "license, version, role and content changes invalidate the review fingerprint");
        using (var replayedSkillApproval = await Post(client, skillPath, approval))
            Check(replayedSkillApproval.StatusCode == HttpStatusCode.Conflict,
                "replayed approval is rejected by exact review revision");
        var restartedReview = new SkillReviewStore(new TaskStore(root)).Read(taskBound.Id)[0];
        Check(restartedReview.ApprovedForLocalInstructionReview && restartedReview.Revision == 1 &&
            !restartedReview.Active && restartedReview.History.Count == 1,
            "owner review persists across SQLite store recreation");
        var revocation = approval with { ExpectedRevision = 1, Decision = "Revoke",
            Confirmation = "REVOKE " + initialSkill.ReviewSha256,
            LocalUsePermissionConfirmed = false };
        using (var revokedReview = await Post(client, skillPath, revocation))
            Check(revokedReview.StatusCode == HttpStatusCode.OK,
                "owner can explicitly revoke exact current review");
        var revokedSkill = projectSkillReviewStore.Read(taskBound.Id)[0];
        Check(revokedSkill.ReviewStatus == "REVOKED" &&
            !revokedSkill.ApprovedForLocalInstructionReview && !revokedSkill.Active &&
            revokedSkill.History.Count == 2 && revokedSkill.History[0].Decision == "ApproveLocalUse" &&
            revokedSkill.History[1].Decision == "Revoke",
            "approval and revocation retain ordered audit events");
        using (var globalCatalogResponse = await client.GetAsync("/api/agents/skills"))
        using (var globalCatalogBody = await Body(globalCatalogResponse))
            Check(globalCatalogBody.RootElement.GetProperty("skills").EnumerateArray()
                .All(skill => !skill.GetProperty("active").GetBoolean()),
                "project review never activates the global catalog");
        // Exercise deletion with persisted review history on a separate disposable project.
        projectSkillReviewStore.Review(otherReviewTask.Id, otherSkill.Skill.Name,
            approval with { ReviewSha256 = otherSkill.ReviewSha256,
                Confirmation = "APPROVE LOCAL USE " + otherSkill.ReviewSha256 });
        using (var deletedReviewTask = await client.DeleteAsync("/api/tasks/" + otherReviewTask.Id))
            Check(deletedReviewTask.IsSuccessStatusCode,
                "project deletion removes skill review events before parent task");
        using (var missingReviewTask = await client.GetAsync(
            "/api/tasks/" + otherReviewTask.Id + "/skill-reviews"))
            Check(missingReviewTask.StatusCode == HttpStatusCode.NotFound,
                "deleted project cannot expose review history");
    }
    {
        var skillActivationStore = new SkillReviewStore(tasks);
        var architectSkillReview = skillActivationStore.Read(taskBound.Id)
            .Single(x => x.Skill.Name == SkillReviewStore.ArchitectureSkill);
        var currentSkillSpec = interviews.Read(taskBound.Id).Specification!;
        var currentSkillRevision = decisionStore.List(taskBound.Id)
            .Single(x => x.Topic == "technology-stack").Revision;
        var architectSkillRequest = new RecommendArchitectureRequest(1,
            currentSkillSpec.Version, currentSkillSpec.Sha256, "technology-stack",
            currentSkillRevision, new ArchitectureSkillRequest(
                architectSkillReview.Skill.Name, 1, architectSkillReview.ReviewSha256,
                Guid.NewGuid().ToString("N")));
        const string skillModel = "qwen3.5:4b";
        void DenySkillAdmission(RecommendArchitectureRequest req, string code)
        {
            try
            {
                _ = skillActivationStore.AdmitArchitecture(taskBound.Id, req, skillModel);
                Check(false, "unexpected skill admission");
            }
            catch (StoreFailure failure)
            {
                Check(failure.Code == code, "skill admission rejects " + code);
            }
        }
        DenySkillAdmission(architectSkillRequest, "STALE_SKILL_REVIEW");
        var architectSkillApproval = new SkillReviewRequest(1, 0,
            architectSkillReview.ReviewSha256, "ApproveLocalUse",
            "APPROVE LOCAL USE " + architectSkillReview.ReviewSha256, true);
        skillActivationStore.Review(taskBound.Id, architectSkillReview.Skill.Name,
            architectSkillApproval);
        DenySkillAdmission(architectSkillRequest with { Skill =
            architectSkillRequest.Skill! with { Name = "requirements-discovery" } }, "INVALID_INPUT");
        DenySkillAdmission(architectSkillRequest with { SpecificationSha256 =
            new string('0', 64) }, "SPECIFICATION_CONFLICT");
        DenySkillAdmission(architectSkillRequest with { ExpectedRevision =
            currentSkillRevision + 1 }, "STALE_DECISION");
        Check(skillActivationStore.ReadUses(taskBound.Id).Count == 0,
            "rejected skill admissions leave no invented model receipts");
        // The API test uses a non-MAF advisor override. Explicit skill use
        // must never silently fall back to that adapter.
        using (var adapterDenied = await Post(client, "/api/tasks/" + taskBound.Id +
            "/architecture-decisions/recommend", architectSkillRequest))
            Check(adapterDenied.StatusCode == HttpStatusCode.BadRequest &&
                skillActivationStore.ReadUses(taskBound.Id).Count == 0,
                "unsupported adapter cannot silently ignore a requested skill");
        var architectSkillContext = skillActivationStore.AdmitArchitecture(
            taskBound.Id, architectSkillRequest, skillModel);
        DenySkillAdmission(architectSkillRequest, "IDEMPOTENCY_CONFLICT");
        var skillMafFactory = new Phase3FakeMafFactory();
        var skillMafTeam = new MafAgentTeam(skillMafFactory);
        var skillAdvice = await skillMafTeam.RecommendWithSkillAsync(taskBound.Objective,
            currentSkillSpec.Content, skillModel, "technology-stack",
            architectSkillContext, CancellationToken.None);
        skillActivationStore.FinishArchitecture(architectSkillContext, true);
        Check(skillAdvice.Options.Count == 2 && skillMafFactory.AllowedToolsSeen == 0 &&
            skillMafFactory.SkillContextsSeen == 1,
            "real MAF adapter receives reviewed instructions once and no tools");
        await skillMafTeam.RecommendAsync(taskBound.Objective, currentSkillSpec.Content,
            skillModel, "technology-stack", CancellationToken.None);
        Check(skillMafFactory.SkillContextsSeen == 1,
            "ordinary recommendation has no ambient skill context");
        try
        {
            await skillMafTeam.RecommendWithSkillAsync(taskBound.Objective,
                currentSkillSpec.Content, skillModel, "technology-stack",
                architectSkillContext, CancellationToken.None);
            Check(false, "skill context must be one-shot");
        }
        catch (StoreFailure failure)
        {
            Check(failure.Code == "IDEMPOTENCY_CONFLICT", "one-shot context rejects replay");
        }
        var incompleteSkillUse = skillActivationStore.AdmitArchitecture(taskBound.Id,
            architectSkillRequest with { Skill = architectSkillRequest.Skill! with {
                RequestId = Guid.NewGuid().ToString("N") } }, skillModel);
        skillActivationStore.FinishArchitecture(incompleteSkillUse, false);
        var skillUseRoute = "/api/tasks/" + taskBound.Id + "/skill-uses";
        using (var privateSkillClient = new HttpClient { BaseAddress = client.BaseAddress })
        using (var deniedUseRead = await privateSkillClient.GetAsync(skillUseRoute))
            Check(deniedUseRead.StatusCode == HttpStatusCode.Unauthorized, "usage is owner-private");
        using (var skillUseResponse = await client.GetAsync(skillUseRoute))
        using (var skillUseBody = await Body(skillUseResponse))
        {
            var usageEvents = skillUseBody.RootElement.GetProperty("events");
            Check(skillUseResponse.StatusCode == HttpStatusCode.OK &&
                usageEvents.GetArrayLength() == 2 &&
                usageEvents.EnumerateArray().Any(e =>
                    e.GetProperty("status").GetString() == "ProposalReturned") &&
                usageEvents.EnumerateArray().Any(e =>
                    e.GetProperty("status").GetString() == "CallFailed"),
                "audit distinguishes a parsed proposal and an unsuccessful call");
        }
        skillActivationStore.Review(taskBound.Id, architectSkillReview.Skill.Name,
            architectSkillApproval with { ExpectedRevision = 1, Decision = "Revoke",
                Confirmation = "REVOKE " + architectSkillReview.ReviewSha256,
                LocalUsePermissionConfirmed = false });
        DenySkillAdmission(architectSkillRequest with { Skill =
            architectSkillRequest.Skill! with { RequestId = Guid.NewGuid().ToString("N") } },
            "STALE_SKILL_REVIEW");
    }
    // Product Owner skill context is reconstructed by the host, not caller text.
    {
        var discoverySkillTask = tasks.CreateTask(new CreateTaskRequest(
            "Discovery skill test", "Build an offline notes tracker", Guid.NewGuid().ToString("N")));
        var discoverySkillStore = new SkillReviewStore(tasks);
        var discoverySkillReview = discoverySkillStore.Read(discoverySkillTask.Id)
            .Single(x => x.Skill.Name == SkillReviewStore.DiscoverySkill);
        var discoverySkillSelection = new ProductOwnerSkillRequest(
            discoverySkillReview.Skill.Name, 1, discoverySkillReview.ReviewSha256,
            Guid.NewGuid().ToString("N"));
        const string discoverySkillModel = "qwen3.5:4b";
        void DenyDiscoverySkill(ProductOwnerSkillRequest selection, string operation,
            int sequence, string? answer, string expected)
        {
            try
            {
                _ = discoverySkillStore.AdmitDiscovery(discoverySkillTask.Id,
                    selection, operation, sequence, answer, discoverySkillModel);
                Check(false, "unexpected discovery skill admission");
            }
            catch (StoreFailure failure)
            {
                Check(failure.Code == expected, "discovery skill rejects " + expected);
            }
        }
        DenyDiscoverySkill(discoverySkillSelection, "interview-turn", 0, null,
            "STALE_SKILL_REVIEW");
        var discoverySkillApproval = new SkillReviewRequest(1, 0,
            discoverySkillReview.ReviewSha256, "ApproveLocalUse",
            "APPROVE LOCAL USE " + discoverySkillReview.ReviewSha256, true);
        discoverySkillStore.Review(discoverySkillTask.Id, discoverySkillReview.Skill.Name,
            discoverySkillApproval);
        DenyDiscoverySkill(discoverySkillSelection with { Name = "architecture-tradeoffs" },
            "interview-turn", 0, null, "INVALID_INPUT");
        DenyDiscoverySkill(discoverySkillSelection, "specification-draft", 0, null, "STALE_INTERVIEW");
        DenyDiscoverySkill(discoverySkillSelection, "interview-turn", 2, null, "STALE_INTERVIEW");
        using (var rejectedDiscoveryAdapter = await Post(client,
            "/api/tasks/" + discoverySkillTask.Id + "/interview/turn",
            new InterviewTurnRequest(0, null, discoverySkillModel, discoverySkillSelection)))
        using (var rejectedDiscoveryBody = await Body(rejectedDiscoveryAdapter))
            Check(rejectedDiscoveryAdapter.StatusCode == HttpStatusCode.BadRequest &&
                rejectedDiscoveryBody.RootElement.GetProperty("error").GetString() ==
                    "SKILL_ADAPTER_UNAVAILABLE" &&
                discoverySkillStore.ReadDiscoveryUses(discoverySkillTask.Id).Count == 0,
                "non-MAF override cannot silently ignore selected Product Owner skill");
        var discoverySkillContext = discoverySkillStore.AdmitDiscovery(discoverySkillTask.Id,
            discoverySkillSelection, "interview-turn", 0, null, discoverySkillModel);
        DenyDiscoverySkill(discoverySkillSelection, "interview-turn", 0, null,
            "IDEMPOTENCY_CONFLICT");
        var discoverySkillFactory = new Phase3FakeMafFactory();
        var discoverySkillTeam = new MafAgentTeam(discoverySkillFactory);
        try
        {
            _ = await discoverySkillTeam.DraftWithSkillAsync(discoverySkillContext, CancellationToken.None);
            Check(false, "wrong phase should not consume a discovery context");
        }
        catch (StoreFailure failure)
        {
            Check(failure.Code == "SKILL_SCOPE_DENIED", "interview context cannot draft specifications");
        }
        var discoverySkillReply = await discoverySkillTeam.AskWithSkillAsync(
            discoverySkillContext, CancellationToken.None);
        discoverySkillStore.FinishDiscovery(discoverySkillContext, true);
        var discoverySkillFirstTurn = interviews.CommitTurn(discoverySkillTask.Id,
            new InterviewTurnRequest(0, null, discoverySkillModel), discoverySkillReply);
        var discoverySkillAnswer = interviews.CommitTurn(discoverySkillTask.Id,
            new InterviewTurnRequest(discoverySkillFirstTurn.LastSequence,
                "Just me, working offline", discoverySkillModel),
            new InterviewReply("Ready to draft.", true));
        var discoveryDraftSelection = discoverySkillSelection with { RequestId = Guid.NewGuid().ToString("N") };
        var discoveryDraftContext = discoverySkillStore.AdmitDiscovery(discoverySkillTask.Id,
            discoveryDraftSelection, "specification-draft", discoverySkillAnswer.LastSequence,
            null, discoverySkillModel);
        var discoverySkillDraft = await discoverySkillTeam.DraftWithSkillAsync(
            discoveryDraftContext, CancellationToken.None);
        discoverySkillStore.FinishDiscovery(discoveryDraftContext, true);
        Check(discoverySkillDraft.Length >= 180 && discoverySkillFactory.SkillContextsSeen == 2 &&
            discoverySkillFactory.AllowedToolsSeen == 0,
            "real MAF Product Owner receives reviewed text for both phases without tools");
        await discoverySkillTeam.DraftAsync(discoverySkillTask.Objective,
            discoverySkillAnswer.Messages, discoverySkillModel, CancellationToken.None);
        Check(discoverySkillFactory.SkillContextsSeen == 2, "unselected draft has no ambient skill");
        try
        {
            _ = await discoverySkillTeam.DraftWithSkillAsync(discoveryDraftContext, CancellationToken.None);
            Check(false, "discovery context must be single use");
        }
        catch (StoreFailure failure)
        {
            Check(failure.Code == "IDEMPOTENCY_CONFLICT", "discovery context rejects reuse");
        }
        var discoveryUsage = new SkillReviewStore(new TaskStore(root))
            .ReadDiscoveryUses(discoverySkillTask.Id);
        Check(discoveryUsage.Count == 2 && discoveryUsage.All(e => e.Status == "ResponseReturned" &&
            e.Role == "ProductOwner" && e.ContextSha256.Length == 64) &&
            discoveryUsage.Select(e => e.ContextSha256).Distinct().Count() == 2,
            "audit persists exact distinct host contexts without storing transcript text");
        using (var discoveryUsageResponse = await client.GetAsync(
            "/api/tasks/" + discoverySkillTask.Id + "/skill-uses"))
        using (var discoveryUsageBody = await Body(discoveryUsageResponse))
            Check(discoveryUsageResponse.StatusCode == HttpStatusCode.OK &&
                discoveryUsageBody.RootElement.GetProperty("discoveryEvents").GetArrayLength() == 2,
                "discovery usage is exposed through the authenticated project audit");
        discoverySkillStore.Review(discoverySkillTask.Id, discoverySkillReview.Skill.Name,
            discoverySkillApproval with { ExpectedRevision = 1, Decision = "Revoke",
                Confirmation = "REVOKE " + discoverySkillReview.ReviewSha256,
                LocalUsePermissionConfirmed = false });
        DenyDiscoverySkill(discoveryDraftSelection with { RequestId = Guid.NewGuid().ToString("N") },
            "specification-draft", discoverySkillAnswer.LastSequence, null, "STALE_SKILL_REVIEW");
        using (var deletedDiscoverySkill = await client.DeleteAsync("/api/tasks/" + discoverySkillTask.Id))
            Check(deletedDiscoverySkill.IsSuccessStatusCode,
                "project deletion clears Product Owner skill audit records");
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
    // The same real coordinator and store used by HTTP, with only the local
    // MAF IChatClient replaced. No generated code or Ollama is executed.
    {
        var qaSkillLedger = new SkillReviewStore(tasks);
        var qaSkillCandidate = qaSkillLedger.Read(taskBound.Id)
            .Single(item => item.Skill.Name == SkillReviewStore.QaSkill);
        var qaSkillSelection = new QaSkillRequest(qaSkillCandidate.Skill.Name, 1,
            qaSkillCandidate.ReviewSha256, Guid.NewGuid().ToString("N"));
        var qaSkillRequest = new StartQaReviewRequest(engineeringRunId, secondPlanHash,
            qaSkillSelection);
        var qaSkillFactory = new Phase5FakeMafFactory();
        var qaSkillCoordinator = new QaReviewCoordinator(tasks, interviews,
            new ImplementationPlanStore(tasks, interviews), engineeringStore, reviewStore,
            new MafQaEngineer(qaSkillFactory),
            app!.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>());
        void RejectQaSkill(StartQaReviewRequest input, string expected)
        {
            try
            {
                _ = qaSkillCoordinator.Start(taskBound.Id, input, "qwen3.5:4b");
                Check(false, "unexpected static QA skill admission");
            }
            catch (StoreFailure error)
            {
                Check(error.Code == expected, "QA skill rejects " + expected);
            }
        }
        RejectQaSkill(qaSkillRequest, "STALE_SKILL_REVIEW");
        var qaSkillApproval = new SkillReviewRequest(1, 0, qaSkillCandidate.ReviewSha256,
            "ApproveLocalUse", "APPROVE LOCAL USE " + qaSkillCandidate.ReviewSha256, true);
        qaSkillLedger.Review(taskBound.Id, qaSkillCandidate.Skill.Name, qaSkillApproval);
        RejectQaSkill(qaSkillRequest with { Skill =
            qaSkillSelection with { Name = "architecture-tradeoffs" } }, "INVALID_INPUT");
        using (var qaSkillOverride = await Post(client, qaPath, qaSkillRequest))
        using (var qaSkillOverrideBody = await Body(qaSkillOverride))
            Check(qaSkillOverride.StatusCode == HttpStatusCode.BadRequest &&
                qaSkillOverrideBody.RootElement.GetProperty("error").GetString() ==
                    "SKILL_ADAPTER_UNAVAILABLE" && qaSkillLedger.ReadQaUses(taskBound.Id).Count == 0,
                "explicit QA skill cannot fall back to another reviewer");
        var skillQaJob = qaSkillCoordinator.Start(taskBound.Id, qaSkillRequest, "qwen3.5:4b");
        for (var skillQaPoll = 0; skillQaPoll < 160 &&
            reviewStore.Get(taskBound.Id, skillQaJob.Id).Job.State == "Running"; skillQaPoll++)
            await Task.Delay(25);
        var skillQaResult = reviewStore.Get(taskBound.Id, skillQaJob.Id);
        var skillQaAudit = new SkillReviewStore(new TaskStore(root)).ReadQaUses(taskBound.Id).Single();
        Check(skillQaResult.Job.State == "Reviewed" && skillQaResult.Reports.Count == 2 &&
            qaSkillFactory.SkillContextsSeen == 2 && qaSkillFactory.ReferenceContextsSeen == 2 &&
            qaSkillFactory.NoTools && skillQaAudit.ContentSha256 == qaSkillCandidate.Skill.ContentSha256 &&
            skillQaAudit.QaReviewId == skillQaJob.Id && skillQaAudit.ReportsSaved == 2 &&
            skillQaAudit.SourceSha256 == QaReviewStore.Fingerprint(engineeringResult!.Files) &&
            skillQaAudit.PlanSha256 == secondPlanHash && skillQaAudit.Status == "Reviewed",
            "QA skill attaches to exact saved files via MAF with no callable tools and durable audit");
        RejectQaSkill(qaSkillRequest, "IDEMPOTENCY_CONFLICT");
        using (var qaSkillAuditResponse = await client.GetAsync("/api/tasks/" +
            taskBound.Id + "/skill-uses"))
        using (var qaSkillAuditBody = await Body(qaSkillAuditResponse))
            Check(qaSkillAuditResponse.StatusCode == HttpStatusCode.OK &&
                qaSkillAuditBody.RootElement.GetProperty("qaEvents").GetArrayLength() == 1,
                "owner-private skill usage exposes exact QA job evidence");
        qaSkillLedger.Review(taskBound.Id, qaSkillCandidate.Skill.Name,
            qaSkillApproval with { ExpectedRevision = 1, Decision = "Revoke",
                Confirmation = "REVOKE " + qaSkillCandidate.ReviewSha256,
                LocalUsePermissionConfirmed = false });
        RejectQaSkill(qaSkillRequest with { Skill =
            qaSkillSelection with { RequestId = Guid.NewGuid().ToString("N") } },
            "STALE_SKILL_REVIEW");
        Check(qaSkillLedger.ReadQaUses(taskBound.Id).Count == 1,
            "rejected and replayed QA admissions roll back their jobs and usage events");
    }
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

    using (var acceptanceResponse = await client.GetAsync(qaPath + "/" + qaReviewId))
    using (var acceptanceBody = await Body(acceptanceResponse))
    {
        var savedAcceptance = acceptanceBody.RootElement.GetProperty("acceptance");
        var savedPreflight = acceptanceBody.RootElement.GetProperty("staticPreflight");
        Check(savedPreflight.GetProperty("runId").GetString() == engineeringRunId &&
            savedPreflight.GetProperty("sourceSha256").GetString() == qaResult!.Job.SourceSha256 &&
            savedPreflight.GetProperty("verificationStatus").GetString() == "GENERATED_NOT_TESTED" &&
            qaResult.Events.Count(e => e.Kind == "QaStaticPreflightCaptured") == 1,
            "QA persists one host-owned preflight bound to exact saved source; no runtime claims");
        Check(acceptanceResponse.StatusCode == HttpStatusCode.OK &&
            savedAcceptance.GetProperty("reviewId").GetString() == qaReviewId &&
            savedAcceptance.GetProperty("planSha256").GetString() == secondPlanHash &&
            savedAcceptance.GetProperty("sourceSha256").GetString() == qaResult!.Job.SourceSha256 &&
            savedAcceptance.GetProperty("criteria").GetArrayLength() > 0 &&
            savedAcceptance.GetProperty("criteria").EnumerateArray().All(c =>
                c.GetProperty("verificationStatus").GetString() == "RequiresTesting") &&
            savedAcceptance.GetProperty("findings").GetArrayLength() == 1 &&
            savedAcceptance.GetProperty("criteria").EnumerateArray().Sum(c =>
                c.GetProperty("implementationIndicators").GetArrayLength()) == 1 &&
            qaResult!.Reports.Single(report => report.Path == "app.js")
                .Assessment.ImplementationEvidence?.Count == 1 &&
            qaResult.Reports.Single(report => report.Path == "index.html")
                .Assessment.CrossFileObservations?.Count == 1 &&
            savedAcceptance.GetProperty("crossFileEvidence").GetArrayLength() == 1 &&
            savedAcceptance.GetProperty("crossFileEvidence")[0]
                .GetProperty("causePath").GetString() == "app.js",
            "HTTP QA acceptance view is derived from persisted exact plan and reports, never a test pass");
    }

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
    // Emit fixed fixture status, not source, prompts, IDs, paths or raw
    // exceptions. Distinguish terminal failures from exhausted polling.
    Console.WriteLine("Repair fixture outcome: cycle=" +
        (repairOutcome?.Cycle.State ?? "POLL_EXHAUSTED") +
        "; cycleCode=" + (repairOutcome?.Cycle.ErrorCode ?? "NONE") +
        "; engineering=" + (repairOutcome?.Engineering.Job.State ?? "NONE") +
        "; engineeringCode=" + (repairOutcome?.Engineering.Job.ErrorCode ?? "NONE") +
        "; qa=" + (repairOutcome?.Review?.Job.State ?? "NONE") +
        "; qaCode=" + (repairOutcome?.Review?.Job.ErrorCode ?? "NONE") +
        "; focused=" + qaModel.RepairFocusPaths.SequenceEqual(["app.js"]));
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
              qaModel.ReviewedFiles.Count == 8,
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


// The HTTP negative skill test must exercise a genuinely non-MAF adapter.
// No live Ollama calls are permitted in this model-free suite.
sealed class Phase2RejectUnexpectedInterview : IInterviewModel
{
    public Task<InterviewReply> AskAsync(string objective,
        IReadOnlyList<InterviewMessage> messages, string model,
        CancellationToken cancellation) =>
        throw new InvalidOperationException("Unexpected interview model invocation");
    public Task<string> DraftAsync(string objective,
        IReadOnlyList<InterviewMessage> messages, string model,
        CancellationToken cancellation) =>
        throw new InvalidOperationException("Unexpected specification model invocation");
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
    public ChatOptions? LastNativeToolOptions { get; private set; }
    public int SkillContextsSeen { get; private set; }

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
            if (options?.Tools is { Count: > 0 }) parent.LastNativeToolOptions = options;
            if (history.Any(m => m.Role.Value == "system" &&
                m.Text?.Contains("OWNER-REVIEWED INSTRUCTION CONTEXT", StringComparison.Ordinal) == true) ||
                options?.Instructions?.Contains("OWNER-REVIEWED INSTRUCTION CONTEXT",
                    StringComparison.Ordinal) == true)
                parent.SkillContextsSeen++;
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
            var after = before.Replace("console.log('start')",
                "console.log('repaired start')", StringComparison.Ordinal);
            if (after == before)
                throw new Exception("Fixture repair did not find exact source");
            var edits = JsonSerializer.Serialize(new {
                summary = "Repaired the approved Start handler without modifying browser entry.",
                edits = new[] { new {
                    path = source.Path, oldText = before, newText = after
                } },
                verificationNotes = new[] { "Independently check Start button behavior" }
            });
            return Task.FromResult(MafSoftwareEngineer.ApplyRepairEdits(
                edits, repair, input.ExistingFiles));
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
        return Task.FromResult(input.File.Path == "app.js"
            ? new QaFileAssessment(
                "The timer code registers a button listener but lacks a persistent timer.",
                [new QaFinding("Major", "Start advances timer state",
                    "The handler logs start but does not update a visible timer.",
                    "Implement timer state and check it with a real browser")],
                ["Click Start and observe the timer after several seconds"],
                [new QaImplementationEvidence(
                    QaAcceptanceProjection.Identify(input.Plan).First().Id,
                    input.File.Content[..Math.Min(80, input.File.Content.Length)],
                    "A saved primary source construct provides an implementation indicator."),
                 new QaImplementationEvidence("unknown:1",
                    input.File.Content[..Math.Min(80, input.File.Content.Length)],
                    "Unknown criterion cannot become acceptance evidence."),
                 new QaImplementationEvidence(
                    QaAcceptanceProjection.Identify(input.Plan).First().Id,
                    "FABRICATED_SOURCE_EXCERPT_ABSENT_FROM_FIXTURE",
                    "Invented evidence must be discarded by the store.")])
            : new QaFileAssessment(
                "The HTML source references a script and a clickable button.",
                [],
                ["Load index.html offline and verify the script is linked"],
                CrossFileObservations: input.AllFiles.FirstOrDefault(f => f.Path == "app.js")
                    is { } companion ? [
                    new QaCrossFileObservation(QaAcceptanceProjection.Identify(input.Plan).First().Id,
                        companion.Path, companion.Content[..Math.Min(80, companion.Content.Length)],
                        "The document refers to the companion script."),
                    new QaCrossFileObservation(QaAcceptanceProjection.Identify(input.Plan).First().Id,
                        "../app.js", companion.Content[..Math.Min(80, companion.Content.Length)],
                        "Invalid path must never become admitted evidence.")]
                    : []));
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
    public string? LastCompanionContext { get; private set; }
    public int SkillContextsSeen { get; private set; }
    public int ReferenceContextsSeen { get; private set; }
    public bool StaticPreflightSeen { get; private set; }
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
            parent.LastCompanionContext = history.Select(m => m.Text).FirstOrDefault(value =>
                value?.StartsWith("PROJECT-WIDE STATIC WEB COMPANION EVIDENCE", StringComparison.Ordinal) == true);
            if (options?.Instructions?.Contains("OWNER-REVIEWED INSTRUCTION CONTEXT",
                    StringComparison.Ordinal) == true || history.Any(m =>
                    m.Role.Value == "system" && m.Text?.Contains("OWNER-REVIEWED INSTRUCTION CONTEXT",
                        StringComparison.Ordinal) == true))
                parent.SkillContextsSeen++;
            if (options?.Instructions?.Contains("OWNER-REVIEWED ADVISORY REFERENCE (references/review-checklist.md)", StringComparison.Ordinal) == true &&
                options?.Instructions?.Contains("Identify state persistence and restoration paths", StringComparison.Ordinal) == true ||
                history.Any(m => m.Role.Value == "system" &&
                    m.Text?.Contains("OWNER-REVIEWED ADVISORY REFERENCE (references/review-checklist.md)", StringComparison.Ordinal) == true &&
                    m.Text.Contains("Identify state persistence and restoration paths", StringComparison.Ordinal)))
                parent.ReferenceContextsSeen++;
            parent.StaticPreflightSeen |= history.Any(m =>
                m.Role.Value == "user" && m.Text?.StartsWith("HOST STATIC PREFLIGHT", StringComparison.Ordinal) == true &&
                m.Text.Contains("MISSING_LINKED_ASSET", StringComparison.Ordinal));
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
