using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

public sealed record ImplementationWorkItem(
    string Id, string Title, string Description,
    IReadOnlyList<string> DependsOn, IReadOnlyList<string> AcceptanceCriteria);

public sealed record ImplementationOutline(
    string Summary, IReadOnlyList<ImplementationWorkItem> Tasks,
    IReadOnlyList<string> TestStrategy, IReadOnlyList<string> Risks);

public sealed record ImplementationPlan(
    string TaskId, int Revision, int SpecificationVersion,
    string SpecificationSha256, string ArchitectureSha256,
    string Model, ImplementationOutline Outline, string PlanSha256,
    string Status, string CreatedUtc, string? ApprovedUtc,
    bool Current, string EffectiveStatus);

public sealed record ImplementationPlanSnapshot(
    int SchemaVersion, ImplementationPlan? Plan,
    IReadOnlyList<ImplementationPlan> History);

public sealed record GenerateImplementationPlanRequest(
    int SchemaVersion, int SpecificationVersion,
    string? SpecificationSha256, string? ArchitectureSha256, int ExpectedRevision);

public sealed record ApproveImplementationPlanRequest(
    int SchemaVersion, int ExpectedRevision, string? PlanSha256,
    string? Confirmation);

public interface IImplementationPlanner
{
    Task<ImplementationOutline> PlanAsync(
        string objective, string specification, DraftArchitectureReadiness architecture,
        string model, CancellationToken cancellation);
}

/// <summary>
/// Accept only bounded, structured engineering plans. A plan is not permission
/// to write or run files; file paths and shell commands are never accepted.
/// </summary>
public static class ImplementationPlanContract
{
    private static readonly Regex Identifier = new(
        "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static string ValidText(string? value, int min, int max)
    {
        var text = value?.Trim();
        if (text is null || text.Length < min || text.Length > max ||
            text.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\t')))
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        return text;
    }

    public static ImplementationOutline Validate(ImplementationOutline? outline)
    {
        if (outline is null || outline.Tasks is null ||
            outline.Tasks.Count is < 1 or > 8 ||
            outline.TestStrategy is null || outline.TestStrategy.Count is < 1 or > 8 ||
            outline.Risks is null || outline.Risks.Count > 8)
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var tasks = new List<ImplementationWorkItem>();
        foreach (var item in outline.Tasks)
        {
            if (item is null || item.Id is null ||
                item.Id.Length is < 2 or > 48 || !Identifier.IsMatch(item.Id) ||
                !ids.Add(item.Id) || item.DependsOn is null ||
                item.DependsOn.Count > 7 || item.AcceptanceCriteria is null ||
                item.AcceptanceCriteria.Count is < 1 or > 6)
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            // Dependencies must refer to *previously* planned tasks; this
            // guarantees a topological order without cycles or hidden work.
            foreach (var dependency in item.DependsOn)
                if (dependency == item.Id || !tasks.Any(t => t.Id == dependency))
                    throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            if (item.DependsOn.Distinct(StringComparer.Ordinal).Count() !=
                item.DependsOn.Count)
                throw new StoreFailure("MODEL_OUTPUT_REJECTED");
            tasks.Add(new ImplementationWorkItem(item.Id,
                ValidText(item.Title, 5, 120),
                ValidText(item.Description, 12, 600),
                item.DependsOn.ToArray(),
                item.AcceptanceCriteria.Select(x => ValidText(x, 8, 260)).ToArray()));
        }

        return new ImplementationOutline(
            ValidText(outline.Summary, 20, 1800), tasks.AsReadOnly(),
            outline.TestStrategy.Select(x => ValidText(x, 8, 300)).ToArray(),
            outline.Risks.Select(x => ValidText(x, 8, 300)).ToArray());
    }

    public static ImplementationOutline Parse(string? raw)
    {
        if (raw is null || raw.Length > 16000)
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        var text = raw.Trim();
        if (text.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) &&
            text.EndsWith("\n```", StringComparison.Ordinal))
            text = text[8..^4].Trim();
        try
        {
            var json = JsonSerializer.Deserialize<ImplementationOutline>(text,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true,
                    UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
            return Validate(json);
        }
        catch (JsonException)
        {
            throw new StoreFailure("MODEL_OUTPUT_REJECTED");
        }
    }
}
