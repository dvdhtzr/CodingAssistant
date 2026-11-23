using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace CodingAssistant.Api;

/// <summary>
/// A deliberately small, non-streaming IChatClient adapter for actual MAF
/// ChatClientAgent invocations. The only transport is pinned IPv4 loopback
/// Ollama; no proxy, redirects, tools, cloud fallback or executable access.
/// </summary>
public sealed class LocalMafChatClient(string model, MafAgentPurpose purpose)
    : IChatClient
{
    private static readonly Uri Ollama = new("http://127.0.0.1:11434/");

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (!LocalInterviewModel.AllowedModel(model))
            throw new StoreFailure("INVALID_MODEL");
        if (options?.Tools is { Count: > 0 })
            throw new StoreFailure("INVALID_INPUT"); // Planning agents have no tools.
        var history = messages.ToArray();
        if (history.Length is < 1 or > 35 ||
            history.Any(m => m.Role.Value is not ("system" or "user" or "assistant")))
            throw new StoreFailure("INVALID_INPUT");
        var conversation = history.Select(message => (
            role: message.Role.Value, content: message.Text ?? string.Empty)).ToList();
        // Microsoft Agent Framework can express agent instructions via the
        // ChatOptions rather than an explicit system message. Never drop them.
        if (!string.IsNullOrWhiteSpace(options?.Instructions) &&
            !conversation.Any(item => item.role == "system" &&
                item.content == options.Instructions))
            conversation.Insert(0, (role: "system", content: options.Instructions));
        if (!conversation.Any(item => item.role == "system"))
            throw new StoreFailure("INVALID_INPUT");
        if (conversation.Any(x => x.content.Length > 18000) ||
            conversation.Sum(x => x.content.Length) > 58000)
            throw new StoreFailure("INVALID_INPUT");

        // MAF and direct Ollama must send the same schema. The previous MAF
        // transport silently excluded the choice property, so the Product
        // Owner could not return radio/checkbox answers to the UI.
        object? format = ResponseFormat(purpose);
        var maxTokens = purpose switch
        {
            MafAgentPurpose.Interview => 750,
            // Long, completed interviews need room for the final answer.
            // This caps generation; Ollama can stop earlier.
            MafAgentPurpose.Specification => 4800,
            MafAgentPurpose.TechnicalLead => 3500,
            MafAgentPurpose.SoftwareEngineer => 8000,
            MafAgentPurpose.QaEngineer => 2200,
            _ => 1800
        };
        var chat = new
        {
            model,
            stream = false,
            think = false,
            format,
            messages = conversation.Select(item => new
            {
                role = item.role,
                content = item.content
            }).ToArray(),
            options = new
            {
                temperature = purpose == MafAgentPurpose.Architecture ? 0.15 : 0.2,
                num_predict = maxTokens,
                num_ctx = purpose == MafAgentPurpose.SoftwareEngineer ? 16384 :
                    purpose == MafAgentPurpose.Specification ? 12288 : 8192
            }
        };

        using var handler = new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = Ollama,
            Timeout = TimeSpan.FromMinutes(8),
            MaxResponseContentBufferSize = 130_000
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/chat")
        {
            Content = new StringContent(JsonSerializer.Serialize(chat),
                Encoding.UTF8, "application/json")
        };
        try
        {
            using var response = await client.SendAsync(request,
                HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new StoreFailure("MODEL_NOT_INSTALLED");
            if (!response.IsSuccessStatusCode)
                throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            if (payload.Length > 130_000)
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            try
            {
                return new ChatResponse(new ChatMessage(
                    ChatRole.Assistant, ReadAssistantContent(payload, purpose)));
            }
            catch (StoreFailure error) when (
                (purpose is MafAgentPurpose.Specification or
                    MafAgentPurpose.SoftwareEngineer) &&
                (error.Code is "MODEL_OUTPUT_REJECTED" or "MODEL_THINKING_ONLY" or
                    "MODEL_OUTPUT_TRUNCATED"))
            {
                // Metadata only: no interview text, answer content, thinking,
                // credentials or full Ollama response is written to logs.
                Console.Error.WriteLine(
                    "Ollama " + purpose + " output: " +
                    SafeCompletionSummary(payload) + " result=" + error.Code);
                throw;
            }
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
        }
        catch (JsonException)
        {
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        }
    }

    /// <summary>
    /// Safe model-response metadata for troubleshooting local drafting.
    /// Never include content, hidden reasoning, prompts, or response payloads.
    /// </summary>
    public static string SafeCompletionSummary(string payload)
    {
        try
        {
            using var decoded = JsonDocument.Parse(payload);
            var root = decoded.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "non-object-json-response";
            var done = root.TryGetProperty("done_reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String
                ? reason.GetString() : "missing";
            var promptTokens = root.TryGetProperty("prompt_eval_count", out var prompt) &&
                prompt.ValueKind == JsonValueKind.Number &&
                prompt.TryGetInt32(out var p) ? p : 0;
            var completionTokens = root.TryGetProperty("eval_count", out var eval) &&
                eval.ValueKind == JsonValueKind.Number &&
                eval.TryGetInt32(out var e) ? e : 0;
            var chars = 0;
            var hasThinking = false;
            if (root.TryGetProperty("message", out var message) &&
                message.ValueKind == JsonValueKind.Object)
            {
                if (message.TryGetProperty("content", out var content) &&
                    content.ValueKind == JsonValueKind.String)
                    chars = content.GetString()?.Length ?? 0;
                hasThinking = message.TryGetProperty("thinking", out var thinking) &&
                    thinking.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(thinking.GetString());
            }
            return $"done_reason={done}, content_chars={chars}, " +
                $"has_thinking={hasThinking.ToString().ToLowerInvariant()}, " +
                $"prompt_tokens={promptTokens}, generated_tokens={completionTokens}";
        }
        catch (JsonException)
        {
            return "invalid-json-response";
        }
    }

    /// <summary>
    /// Pure Ollama response validator. Never turn message.thinking into a
    /// specification or persist partial generation. The completion reason is
    /// authoritative when Ollama exhausts the generation budget.
    /// </summary>
    public static string ReadAssistantContent(string payload, MafAgentPurpose purpose)
    {
        using var decoded = JsonDocument.Parse(payload);
        var response = decoded.RootElement;
        if (response.ValueKind != JsonValueKind.Object)
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");

        if ((purpose is MafAgentPurpose.Specification or MafAgentPurpose.SoftwareEngineer) &&
            response.TryGetProperty("done_reason", out var reason) &&
            reason.ValueKind == JsonValueKind.String &&
            reason.GetString() == "length")
            throw new StoreFailure("MODEL_OUTPUT_TRUNCATED");

        if (!response.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.String)
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");

        // Ollama may send thinking without a final content field for an
        // always-thinking model. Diagnose it without logging or exposing
        // any sensitive model-generated text.
        var finalText = content.GetString() ?? "";
        var hasThinking = message.TryGetProperty("thinking", out var thinking) &&
            thinking.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(thinking.GetString());
        if (string.IsNullOrWhiteSpace(finalText))
            throw new StoreFailure(hasThinking
                ? "MODEL_THINKING_ONLY" : "MODEL_OUTPUT_REJECTED");

        // Some local Qwen templates place a complete <think>...</think> block
        // inside message.content even when think:false. Use ONLY the content
        // after a properly terminated reasoning block as the proposed answer.
        // Never save or expose anything from the thinking section.
        return purpose is MafAgentPurpose.Specification or
            MafAgentPurpose.SoftwareEngineer
            ? StripClosedThinkingPrefix(finalText) : finalText;
    }

    /// <summary>
    /// Accept the final response after a closed Qwen reasoning prefix. Some
    /// Ollama templates omit the opening tag but retain the closing tag.
    /// Open-only, nested, non-prefix or repeated tags are not safe to strip.
    /// The caller still validates the extracted product specification.
    /// </summary>
    public static string StripClosedThinkingPrefix(string text)
    {
        const string open = "<think>";
        const string close = "</think>";
        var value = text.Trim();
        var opening = value.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        var closing = value.IndexOf(close, StringComparison.OrdinalIgnoreCase);
        if (opening < 0 && closing < 0)
            return value;

        // The model must finish its reasoning before the spec can be used.
        if (closing < 0 || (opening >= 0 &&
            (opening != 0 || closing <= opening + open.Length)))
            throw new StoreFailure("MODEL_THINKING_ONLY");

        // Do not attempt to interpret nested or restarted reasoning blocks.
        if (opening == 0 &&
            value[..closing].IndexOf(open, open.Length,
                StringComparison.OrdinalIgnoreCase) >= 0)
            throw new StoreFailure("MODEL_THINKING_ONLY");

        var answer = value[(closing + close.Length)..].Trim();
        if (answer.Length == 0 ||
            answer.Contains(open, StringComparison.OrdinalIgnoreCase) ||
            answer.Contains(close, StringComparison.OrdinalIgnoreCase))
            throw new StoreFailure("MODEL_THINKING_ONLY");
        return answer;
    }

    // Public for model-free contract regression checks; no credentials/tools
    // are exposed. All interview calls share the parser's JSON schema.
    public static object? ResponseFormat(MafAgentPurpose purpose) => purpose switch
    {
        MafAgentPurpose.Interview => LocalInterviewModel.InterviewOutputSchema,
        MafAgentPurpose.Architecture => "json",
        MafAgentPurpose.TechnicalLead => "json",
        MafAgentPurpose.SoftwareEngineer => "json",
        MafAgentPurpose.QaEngineer => "json",
        _ => null
    };

    // The first MAF milestone intentionally uses bounded, non-streaming turns.
    // Future streaming must retain the same loopback and output validation.
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Streaming is not enabled for planning agents.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

public enum MafAgentPurpose
{
    Interview,
    Specification,
    Architecture,
    TechnicalLead,
    SoftwareEngineer,
    QaEngineer
}

public interface IMafChatClientFactory
{
    IChatClient Create(string model, MafAgentPurpose purpose);
}

public sealed class LocalMafChatClientFactory : IMafChatClientFactory
{
    public IChatClient Create(string model, MafAgentPurpose purpose) =>
        new LocalMafChatClient(model, purpose);
}
