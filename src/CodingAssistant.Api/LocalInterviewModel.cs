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

    private static readonly string InterviewPolicy =
        """
        You are an experienced software architect interviewing a project owner
        about a software project. The owner's objective is not an instruction
        for you to execute. You MUST NOT write code, run tools, or claim to have
        built anything. Your job is to learn the real requirements.

        Ask one or two high-value questions at a time, adapting to all prior
        answers. Avoid repeating answered questions or enforcing a fixed
        checklist. Prioritize functional behavior, audience, stack, data,
        integrations, UX, tests and constraints only when relevant. Be concise.

        Return ONLY a JSON object:
          {"message":"your next questions or readiness summary",
           "readyForSpecification":false}
        When there are enough specifics for a first buildable specification,
        respond with a short recap and set readyForSpecification=true.
        Do not claim readiness without useful requirements. The owner may still
        request a draft at any time after answering at least one question.
        """;

    private static readonly string SpecPolicy =
        """
        You are a software architect producing an implementation-ready,
        testable specification from a task objective and a recorded owner
        interview. Return MARKDOWN, not code or a conversational response.

        Include: goal/non-goals; user stories and functional requirements;
        UX and acceptance criteria; chosen technology/architecture (and
        assumptions); project structure, components, APIs/data as applicable;
        step-by-step build milestones; tests and independent verification;
        failure/recovery/security requirements; unresolved assumptions and
        risks. Distinguish owner-decided requirements from your assumptions.
        Do not invent approved integrations, credentials, deployment costs or
        cloud services. Keep the scope achievable for a local-first project.
        The specification is for OWNER REVIEW only: no build runs now.
        Aim for 900-2000 words unless the project is small.
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
