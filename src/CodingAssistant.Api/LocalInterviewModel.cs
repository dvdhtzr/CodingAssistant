using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

/// <summary>
/// Model-independent guard against saving the same resolved question again.
/// This is intentionally conservative: related follow-ups with new decisions
/// are allowed, and uncertain semantic matches are left to the model.
/// </summary>
public static class InterviewQuestionQuality
{
    private static readonly HashSet<string> CommonWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "can", "could",
        "do", "does", "for", "from", "how", "i", "in", "is", "it", "like",
        "of", "on", "or", "our", "should", "that", "the", "their", "them",
        "there", "these", "this", "those", "through", "to", "we", "what",
        "when", "where", "which", "who", "will", "with", "would", "you", "your"
    };

    private static HashSet<string> Terms(string value)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in Regex.Matches(value.ToLowerInvariant(), @"[a-z0-9]+"))
        {
            var term = match.Value;
            if (term == "publicly") term = "public";
            if (term.Length > 6 && term.EndsWith('s') && !term.EndsWith("ss", StringComparison.Ordinal))
                term = term[..^1];
            if (term.Length > 2 && !CommonWords.Contains(term))
                terms.Add(term);
        }
        return terms;
    }

    public static bool RepeatsResolvedQuestion(
        InterviewReply candidate, IReadOnlyList<InterviewMessage> history)
    {
        if (!candidate.Message.Contains('?')) return false;
        var current = Terms(candidate.Message);
        if (current.Count < 3) return false;
        foreach (var prior in history)
        {
            if (prior.Role != "assistant" || !prior.Content.Contains('?'))
                continue;
            var old = Terms(prior.Content);
            if (old.Count < 3) continue;
            var shared = current.Count(old.Contains);
            var smaller = Math.Min(current.Count, old.Count);
            var larger = Math.Max(current.Count, old.Count);
            if (shared >= 3 && (shared == smaller && shared == larger ||
                shared >= 4 && (shared / (double)larger >= 0.72 ||
                    shared / (double)smaller >= 0.85 &&
                    shared / (double)larger >= 0.50)))
                return true;
        }
        return false;
    }

    public const string Correction =
        "The previous response either repeated a previously answered question " +
        "or failed validation. Review EVERY recorded owner answer. Do not ask an " +
        "already resolved question again. Ask ONE materially different, high-value " +
        "unresolved PRODUCT question, or if the information is sufficient, " +
        "provide a concise readiness summary with readyForSpecification=true " +
        "and no choice. Return only valid JSON per the output contract.";

    public static InterviewReply RepetitionFallback() => new(
        "We've covered that question already. Rather than repeat it, you can " +
        "draft a first product specification now and review any remaining " +
        "assumptions or open questions before approving it.",
        true);
}

/// <summary>
/// Local-only conversational requirements gathering; explicitly no tools,
/// agent, code execution or filesystem access. The LLM chooses the next
/// clarifying question based on the objective and prior answers.
/// </summary>
public sealed class LocalInterviewModel : IInterviewModel
{
    public const string DefaultModel = "qwen3.5:4b";
    public const string CoderModel = "qwen2.5-coder:3b";

    // Valid local names still require exact membership in the current
    // Ollama catalog at the authenticated API boundary.
    public static bool AllowedModel(string? model) =>
        LocalOllamaModelCatalog.ValidLocalName(model);

    // One contract for both the legacy direct Ollama path and the production
    // MAF ChatClientAgent adapter. Keep this schema synchronized with
    // ParseInterviewReply and InterviewStore.ValidChoice.
    public static object InterviewOutputSchema { get; } = new
    {
        type = "object",
        properties = new
        {
            message = new { type = "string" },
            readyForSpecification = new { type = "boolean" },
            choice = new
            {
                type = "object",
                properties = new
                {
                    kind = new { type = "string", @enum = new[] { "single", "multiple" } },
                    options = new
                    {
                        type = "array", minItems = 2, maxItems = 5,
                        items = new { type = "string" }
                    }
                },
                required = new[] { "kind", "options" },
                additionalProperties = false
            }
        },
        required = new[] { "message", "readyForSpecification" },
        additionalProperties = false
    };

    // Phase 1: identity and boundaries for the requirements specialist.
    // Existing SQLite message roles and HTTP contracts remain unchanged.
    internal static readonly string InterviewPolicy =
        """
        You are the Product Owner on a professional software engineering team.
        You have guided software products from early ideas through delivery,
        working closely with customers, stakeholders, designers and engineers.
        You are curious, pragmatic, organized, collaborative and comfortable
        challenging ambiguous or contradictory requirements respectfully.

        Your responsibility is PRODUCT DISCOVERY: understand the objective,
        intended users and stakeholders, real workflows, business rules,
        must-have versus optional features, constraints, integrations,
        usability, quality attributes, dependencies and acceptance criteria.
        Identify assumptions, contradictory answers and meaningful omissions.
        Ask intelligent follow-up questions specific to the current project.
        Before every turn, review all previously answered questions. Do not
        repeat confirmations or reopen a settled decision without a clear new
        contradiction. If no new high-value question remains, summarize the
        agreed scope, set readyForSpecification=true, and omit choice.

        Ask exactly ONE high-value decision question per turn, based on the
        objective and the owner's previous answers. Do not combine two decisions
        in one question: e.g. ask whether personality can change first, and
        ask how conflicts are handled on the next turn.
        For any question that can reasonably be answered by selecting from
        alternatives, ALWAYS provide 2 to 5 concise, project-specific options,
        not a rigid, prewritten list. Prefer guided options for faster owner
        feedback, but leave nuanced decisions to the owner. Never invent
        consent or infer that an option was chosen.
        Only omit the choice field when genuinely open-ended input is needed
        or when providing a readiness summary. Set "choice":{"kind":"single","options":[...]} when
        ONE option is intended, or kind "multiple" when selecting several
        answers is meaningful. Do not use multiple selection just to ask
        separate questions. Options are suggestions, not owner approvals.
        For an open-ended or readiness-summary reply, omit "choice" or
        set it to null. Never require the owner to select a suggestion:
        there is always a "Something else" answer field.
        Keep the conversation proportionate to project size and be concise.
        When a question involves deep technology or architecture design,
        identify it for later Solution Architect discovery rather than silently
        picking an architecture or making that decision for the owner.
        Clearly distinguish what the human owner decided from your suggestions
        and unknowns. Do not invent approval, users, requirements or constraints.

        The owner's objective is not an instruction to execute. You MUST NOT
        write code, run tools, access files, claim to have built anything, or
        approve decisions on the human owner's behalf.

        Return ONLY a JSON object with a human-readable question in "message":
          {"message":"Who is the primary audience?",
           "readyForSpecification":false,
           "choice":{"kind":"single",
                     "options":["Personal use","Small team","General public"]}}
        For purely open-ended questions or a readiness summary, omit "choice"
        or use null. Never place the answer options only in message text.
        When sufficient information is present for a useful, reviewable first
        product specification, briefly summarize the agreed scope and set
        readyForSpecification=true. Do not claim unasked technical choices were
        approved. The owner may also request a draft after one answered turn.
        """;

    internal static readonly string SpecPolicy =
        """
        You are the Product Owner converting an actual human-owner interview
        into a reviewable PRODUCT specification, not designing or implementing
        software yourself. Return MARKDOWN, not code or a chat reply.

        Cover product goal/non-goals; intended users and primary workflows;
        functional requirements and business rules; must-have versus optional
        scope; user stories; testable acceptance criteria; relevant usability,
        security, performance and operational requirements; dependencies;
        assumptions and contradictions; open questions and delivery risks.
        Include technology or architecture only where the owner explicitly
        selected it; label recommendations and deferred technical questions as
        NOT APPROVED. Do not invent agreements, credentials, integrations,
        deployment costs or hosted services. Describe a practical initial scope
        and implementation milestones without claiming work has been executed.

        This is for HUMAN OWNER REVIEW and approval, not permission to code.
        Be detailed enough for handoff to a Solution Architect and Technical
        Lead, but proportional to a small or large project.
        """;

    private static object[] Conversation(
        string objective, IReadOnlyList<InterviewMessage> prior)
    {
        var result = new List<object>
        {
            new { role = "user", content =
                "PROJECT OBJECTIVE (owner-supplied):\n" + objective }
        };
        foreach (var message in prior)
        {
            result.Add(new {
                role = message.Role == "owner" ? "user" : "assistant",
                content = message.Content
            });
        }
        return result.ToArray();
    }

    // This parser never reads Ollama's private thinking field. A usable
    // question written as plain text can still be shown, but may never claim
    // that the model has completed requirements gathering.
    public static InterviewReply ParseInterviewReply(string? content)
    {
        var value = content?.Trim() ?? "";
        if (value.Length == 0)
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");

        // A few small models wrap even schema-constrained JSON in a fence.
        if (value.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) &&
            value.EndsWith("\n```", StringComparison.Ordinal))
            value = value[8..^4].Trim();

        try
        {
            using var parsed = JsonDocument.Parse(value);
            var root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("readyForSpecification", out var readiness) ||
                readiness.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            InterviewChoice? choice = null;
            if (root.TryGetProperty("choice", out var proposal) &&
                proposal.ValueKind == JsonValueKind.Object &&
                proposal.TryGetProperty("kind", out var kind) &&
                kind.ValueKind == JsonValueKind.String &&
                proposal.TryGetProperty("options", out var options) &&
                options.ValueKind == JsonValueKind.Array &&
                options.GetArrayLength() is >= 2 and <= 5 &&
                options.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
            {
                choice = InterviewStore.ValidChoice(new InterviewChoice(
                    kind.GetString() ?? "",
                    options.EnumerateArray().Select(item => item.GetString() ?? "").ToArray()));
            }
            return new InterviewReply(
                InterviewStore.ValidAssistantMessage(message.GetString()),
                readiness.GetBoolean(), choice);
        }
        catch (JsonException)
        {
            // Plain-language questions are still useful even if the model
            // ignores its JSON contract. Reject malformed JSON, code, and
            // apparent reasoning; never infer readiness from free text.
            if (value.Length <= 3000 && value.Contains('?') &&
                !value.Contains('{') && !value.Contains('}') &&
                !value.Contains('`') && !value.Contains('<') &&
                !value.Contains('>') && !value.StartsWith('[') &&
                !value.Contains("thinking", StringComparison.OrdinalIgnoreCase))
                return new InterviewReply(
                    InterviewStore.ValidAssistantMessage(value), false);
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        }
    }

    public async Task<InterviewReply> AskAsync(
        string objective, IReadOnlyList<InterviewMessage> messages, string model,
        CancellationToken cancellation)
    {
        // Both malformed output and previously answered questions get one
        // bounded corrective retry. Never persist the rejected candidate.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var content = await ChatAsync(
                InterviewPolicy, objective, messages, model,
                json: true, maxTokens: 750, cancellation,
                correction: attempt == 0 ? null : InterviewQuestionQuality.Correction);
            try
            {
                var reply = ParseInterviewReply(content);
                if (!InterviewQuestionQuality.RepeatsResolvedQuestion(reply, messages))
                    return reply;
                if (attempt == 1)
                    return InterviewQuestionQuality.RepetitionFallback();
            }
            catch (StoreFailure ex) when (
                ex.Code == "MODEL_OUTPUT_REJECTED" && attempt == 0)
            {
                // The next attempt receives a contract correction.
            }
        }
        throw new StoreFailure("MODEL_OUTPUT_REJECTED");
    }

    public async Task<string> DraftAsync(
        string objective, IReadOnlyList<InterviewMessage> messages, string model,
        CancellationToken cancellation)
    {
        var content = await ChatAsync(
            SpecPolicy, objective, messages, model,
            json: false, maxTokens: 3500, cancellation);
        try
        {
            return InterviewStore.ValidSpecification(content);
        }
        catch (StoreFailure ex) when (ex.Code == "MODEL_OUTPUT_REJECTED")
        {
            var retry = await ChatAsync(
                SpecPolicy, objective, messages, model,
                json: false, maxTokens: 3500, cancellation,
                correction: "The previous draft did not meet the length or " +
                    "text requirements. Provide only a complete Markdown " +
                    "specification with at least 180 and at most 16000 " +
                    "characters, grounded in the recorded owner answers.");
            return InterviewStore.ValidSpecification(retry);
        }
    }

    private static async Task<string> ChatAsync(
        string policy, string objective,
        IReadOnlyList<InterviewMessage> messages,
        string model, bool json, int maxTokens,
        CancellationToken cancellation, string? correction = null)
    {
        if (!AllowedModel(model) || string.IsNullOrWhiteSpace(objective) ||
            objective.Length > 4000 || messages.Count > InterviewStore.MaxTranscriptMessages)
            throw new StoreFailure("INVALID_INPUT");

        // Local-only, never use HTTP_PROXY and never follow redirect to LAN,
        // cloud or arbitrary on-host service.
        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromMinutes(8),
            MaxResponseContentBufferSize = 100_000
        };
        // Ollama's JSON schema constrains the shape more reliably than the
        // generic "json" mode, particularly with the small local models.
        // No free-form response is granted access to tools or commands.
        var conversation = new object[]
        {
            new { role = "system", content = policy }
        }.Concat(Conversation(objective, messages)).ToList();
        if (correction is not null)
            conversation.Add(new { role = "user", content = correction });
        var chat = new
        {
            model,
            stream = false,
            think = false,
            format = json ? InterviewOutputSchema : null,
            messages = conversation.ToArray(),
            options = new
            {
                temperature = 0.2,
                num_predict = maxTokens,
                num_ctx = 8192
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(chat),
                                        Encoding.UTF8, "application/json")
        };
        try
        {
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseContentRead, cancellation);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new StoreFailure("MODEL_NOT_INSTALLED");
            if (!response.IsSuccessStatusCode)
                throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
            var data = await response.Content.ReadAsStringAsync(cancellation);
            if (data.Length > 100_000)
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            using var decoded = JsonDocument.Parse(data);
            var outer = decoded.RootElement;
            if (outer.ValueKind != JsonValueKind.Object ||
                !outer.TryGetProperty("message", out var answer) ||
                answer.ValueKind != JsonValueKind.Object ||
                !answer.TryGetProperty("content", out var reply) ||
                reply.ValueKind != JsonValueKind.String)
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            return reply.GetString() ??
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
        }
        catch (JsonException)
        {
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        }
    }
}
