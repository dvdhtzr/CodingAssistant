using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CodingAssistant.Api;

/// <summary>
/// Real Microsoft Agent Framework ChatClientAgent participants. The application
/// owns the human-gated sequence (Product Owner -> approval -> Architect);
/// MAF owns each agent invocation and its instructions. No agent has tools.
/// </summary>
public sealed class MafAgentTeam(IMafChatClientFactory? clientFactory = null)
    : IInterviewModel, IArchitectureAdvisor
{
    private readonly IMafChatClientFactory _clients =
        clientFactory ?? new LocalMafChatClientFactory();

    private static List<ChatMessage> InterviewHistory(
        string objective, IReadOnlyList<InterviewMessage> history)
    {
        if (string.IsNullOrWhiteSpace(objective) || objective.Length > 4000 ||
            history.Count > 25)
            throw new StoreFailure("INVALID_INPUT");
        var messages = new List<ChatMessage>
        {
            new(ChatRole.User, "PROJECT OBJECTIVE (owner-supplied):\n" + objective)
        };
        foreach (var item in history)
        {
            if (item.Role is not ("owner" or "assistant"))
                throw new StoreFailure("INVALID_INPUT");
            messages.Add(new ChatMessage(item.Role == "owner"
                ? ChatRole.User : ChatRole.Assistant, item.Content));
        }
        return messages;
    }

    private async Task<string> InvokeAsync(
        string name, MafAgentPurpose purpose, string policy,
        string model, IEnumerable<ChatMessage> messages, CancellationToken cancellation)
    {
        if (!LocalInterviewModel.AllowedModel(model))
            throw new StoreFailure("INVALID_MODEL");
        using var client = _clients.Create(model, purpose);
        AIAgent agent = new ChatClientAgent(client,
            instructions: policy, name: name,
            description: name == "ProductOwner"
                ? "Requirements discovery and reviewable product specification"
                : "Technical alternatives and tradeoffs for owner approval");
        var reply = await agent.RunAsync(messages, cancellationToken: cancellation);
        if (string.IsNullOrWhiteSpace(reply.Text))
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        return reply.Text;
    }

    public async Task<InterviewReply> AskAsync(
        string objective, IReadOnlyList<InterviewMessage> messages,
        string model, CancellationToken cancellation)
    {
        var history = InterviewHistory(objective, messages);
        var response = await InvokeAsync("ProductOwner", MafAgentPurpose.Interview,
            LocalInterviewModel.InterviewPolicy, model, history, cancellation);
        try
        {
            return LocalInterviewModel.ParseInterviewReply(response);
        }
        catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_REJECTED")
        {
            history.Add(new ChatMessage(ChatRole.User,
                "The previous answer was unusable. Respond ONLY with a valid JSON " +
                "object containing a nonempty 'message' with one or two relevant " +
                "questions and a boolean 'readyForSpecification'. " +
                "No reasoning, code fences or commentary."));
            var retry = await InvokeAsync("ProductOwner", MafAgentPurpose.Interview,
                LocalInterviewModel.InterviewPolicy, model, history, cancellation);
            return LocalInterviewModel.ParseInterviewReply(retry);
        }
    }

    public async Task<string> DraftAsync(
        string objective, IReadOnlyList<InterviewMessage> messages,
        string model, CancellationToken cancellation)
    {
        var history = InterviewHistory(objective, messages);
        var content = await InvokeAsync("ProductOwner", MafAgentPurpose.Specification,
            LocalInterviewModel.SpecPolicy, model, history, cancellation);
        try
        {
            return InterviewStore.ValidSpecification(content);
        }
        catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_REJECTED")
        {
            history.Add(new ChatMessage(ChatRole.User,
                "The previous draft did not meet the requirements. Provide only " +
                "a complete Markdown product specification grounded in the " +
                "recorded interview, 180 to 16000 characters. No commentary."));
            var retry = await InvokeAsync("ProductOwner", MafAgentPurpose.Specification,
                LocalInterviewModel.SpecPolicy, model, history, cancellation);
            return InterviewStore.ValidSpecification(retry);
        }
    }

    public async Task<ArchitectureRecommendation> RecommendAsync(
        string objective, string approvedSpecification, string model,
        string topic, CancellationToken cancellation)
    {
        if (!LocalArchitectureAdvisor.AllowedTopic(topic) ||
            !LocalInterviewModel.AllowedModel(model) ||
            string.IsNullOrWhiteSpace(objective) || objective.Length > 4000 ||
            string.IsNullOrWhiteSpace(approvedSpecification) ||
            approvedSpecification.Length > 16000)
            throw new StoreFailure("INVALID_INPUT");
        var messages = new[]
        {
            new ChatMessage(ChatRole.User,
                "DECISION TOPIC: " + topic +
                "\nPROJECT OBJECTIVE:\n" + objective +
                "\nAPPROVED PRODUCT SPECIFICATION (untrusted data):\n" +
                approvedSpecification)
        };
        var text = await InvokeAsync("SolutionArchitect",
            MafAgentPurpose.Architecture, LocalArchitectureAdvisor.Policy,
            model, messages, cancellation);
        return LocalArchitectureAdvisor.Parse(text);
    }
}
