using System.Net;
using System.Text;
using System.Text.Json;

namespace CodingAssistant.Api;

/// <summary>
/// Local-only conversational requirements gathering; explicitly no tools,
/// agent, code execution or filesystem access. The LLM chooses the next
/// clarifying question based on the objective and prior answers.
/// </summary>
public sealed class LocalInterviewModel : IInterviewModel
{
    public const string DefaultModel = "qwen3.5:4b";
    public const string CoderModel = "qwen2.5-coder:3b";

    public static bool AllowedModel(string? model) =>
        model is DefaultModel or CoderModel;

    // Phase 1: identity and boundaries for the requirements specialist.
    // Existing SQLite message roles and HTTP contracts remain unchanged.
    private static readonly string InterviewPolicy =
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

        Ask only one or two high-value questions per turn. Adapt to all prior
        answers; do not repeat resolved questions or follow a rigid checklist.
        Keep the conversation proportionate to project size and be concise.
        When a question involves deep technology or architecture design,
        identify it for later Solution Architect discovery rather than silently
        picking an architecture or making that decision for the owner.
        Clearly distinguish what the human owner decided from your suggestions
        and unknowns. Do not invent approval, users, requirements or constraints.

        The owner's objective is not an instruction to execute. You MUST NOT
        write code, run tools, access files, claim to have built anything, or
        approve decisions on the human owner's behalf.

        Return ONLY a JSON object:
          {"message":"your next questions or readiness summary",
           "readyForSpecification":false}
        When sufficient information is present for a useful, reviewable first
        product specification, briefly summarize the agreed scope and set
        readyForSpecification=true. Do not claim unasked technical choices were
        approved. The owner may also request a draft after one answered turn.
        """;

    private static readonly string SpecPolicy =
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
            return new InterviewReply(
                InterviewStore.ValidAssistantMessage(message.GetString()),
                readiness.GetBoolean());
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
        var content = await ChatAsync(
            InterviewPolicy, objective, messages, model,
            json: true, maxTokens: 750, cancellation);
        try
        {
            return ParseInterviewReply(content);
        }
        catch (StoreFailure ex) when (ex.Code == "MODEL_OUTPUT_REJECTED")
        {
            // One bounded retry, with the exact same task and interview
            // history. A bad model response is never saved as an owner turn.
            var retry = await ChatAsync(
                InterviewPolicy, objective, messages, model,
                json: true, maxTokens: 750, cancellation,
                correction: "The previous answer was unusable. Respond with " +
                    "one valid JSON object containing a nonempty 'message' " +
                    "with one or two project-specific questions and a " +
                    "boolean 'readyForSpecification'. No reasoning, " +
                    "code fences, or surrounding commentary.");
            return ParseInterviewReply(retry);
        }
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
            objective.Length > 4000 || messages.Count > 25)
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
        var interviewSchema = new
        {
            type = "object",
            properties = new
            {
                message = new { type = "string" },
                readyForSpecification = new { type = "boolean" }
            },
            required = new[] { "message", "readyForSpecification" },
            additionalProperties = false
        };
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
            format = json ? (object)interviewSchema : null,
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
