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
        object? format = purpose switch
        {
            MafAgentPurpose.Interview => interviewSchema,
            MafAgentPurpose.Architecture => "json",
            MafAgentPurpose.TechnicalLead => "json",
            MafAgentPurpose.SoftwareEngineer => "json",
            _ => null
        };
        var maxTokens = purpose switch
        {
            MafAgentPurpose.Interview => 750,
            MafAgentPurpose.Specification => 3500,
            MafAgentPurpose.TechnicalLead => 3500,
            MafAgentPurpose.SoftwareEngineer => 6000,
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
                num_ctx = 8192
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
            using var decoded = JsonDocument.Parse(payload);
            if (decoded.RootElement.ValueKind != JsonValueKind.Object ||
                !decoded.RootElement.TryGetProperty("message", out var message) ||
                message.ValueKind != JsonValueKind.Object ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(content.GetString()))
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            return new ChatResponse(new ChatMessage(
                ChatRole.Assistant, content.GetString()));
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
    SoftwareEngineer
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
