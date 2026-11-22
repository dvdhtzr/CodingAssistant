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


    /// <summary>
    /// Return a bounded, non-content-bearing validation hint for a model retry.
    /// Do not propagate model-generated text or JSON parser messages into prompts.
    /// Parse remains the sole authority for accepting a plan.
    /// </summary>
    public static bool TryParse(string? raw, out ImplementationOutline? outline,
        out string reason)
    {
        try
        {
            outline = Parse(raw);
            reason = "";
            return true;
        }
        catch (StoreFailure error) when (error.Code == "MODEL_OUTPUT_REJECTED")
        {
            outline = null;
            reason = Diagnose(raw);
            return false;
        }
    }

    private static string Diagnose(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return "EMPTY_RESPONSE: Return the complete JSON object.";
        if (raw.Length > 16000)
            return "RESPONSE_TOO_LONG: Keep the entire response under 16000 characters.";
        var text = raw.Trim();
        if (text.StartsWith("```json\n", StringComparison.OrdinalIgnoreCase) &&
            text.EndsWith("\n```", StringComparison.Ordinal))
            text = text[8..^4].Trim();
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return "ROOT_SHAPE: Return one JSON object.";
            if (root.EnumerateObject().Any(p =>
                p.Name is not ("summary" or "tasks" or "testStrategy" or "risks")))
                return "EXTRA_FIELDS: Use only summary, tasks, testStrategy and risks.";
            if (!root.TryGetProperty("summary", out var summary) ||
                !TextInRange(summary, 20, 1800))
                return "SUMMARY: Provide a summary of 20-1800 characters.";
            if (!root.TryGetProperty("tasks", out var tasks) ||
                tasks.ValueKind != JsonValueKind.Array ||
                tasks.GetArrayLength() is < 1 or > 8)
                return "TASK_COUNT: Provide 1-8 tasks in a JSON array.";
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var task in tasks.EnumerateArray())
            {
                if (task.ValueKind != JsonValueKind.Object)
                    return "TASK_SHAPE: Each task must be a JSON object.";
                if (task.EnumerateObject().Any(p => p.Name is not
                    ("id" or "title" or "description" or "dependsOn" or "acceptanceCriteria")))
                    return "TASK_EXTRA_FIELDS: Use only documented task fields.";
                if (!task.TryGetProperty("id", out var idValue) ||
                    idValue.ValueKind != JsonValueKind.String ||
                    idValue.GetString() is not { } id ||
                    id.Length is < 2 or > 48 || !Identifier.IsMatch(id) ||
                    ids.Contains(id))
                    return "TASK_ID: Use unique, 2-48 character lowercase kebab-case IDs.";
                if (!task.TryGetProperty("title", out var title) ||
                    !TextInRange(title, 5, 120))
                    return "TASK_TITLE: Each title must be 5-120 characters.";
                if (!task.TryGetProperty("description", out var description) ||
                    !TextInRange(description, 12, 600))
                    return "TASK_DESCRIPTION: Each description must be 12-600 characters.";
                if (!task.TryGetProperty("dependsOn", out var deps) ||
                    deps.ValueKind != JsonValueKind.Array || deps.GetArrayLength() > 7)
                    return "DEPENDENCY_SHAPE: Use an array of at most 7 earlier task IDs.";
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var dep in deps.EnumerateArray())
                    if (dep.ValueKind != JsonValueKind.String ||
                        dep.GetString() is not { } value ||
                        !ids.Contains(value) || !seen.Add(value))
                        return "DEPENDENCY_ORDER: dependsOn must contain unique, earlier task IDs only.";
                if (!task.TryGetProperty("acceptanceCriteria", out var criteria) ||
                    !TextArrayInRange(criteria, 1, 6, 8, 260))
                    return "ACCEPTANCE_CRITERIA: Provide 1-6 criteria of 8-260 characters each.";
                ids.Add(id);
            }
            if (!root.TryGetProperty("testStrategy", out var strategy) ||
                !TextArrayInRange(strategy, 1, 8, 8, 300))
                return "TEST_STRATEGY: Provide 1-8 verification items of 8-300 characters each.";
            if (!root.TryGetProperty("risks", out var risks) ||
                !TextArrayInRange(risks, 0, 8, 8, 300))
                return "RISKS: Provide an array of 0-8 risks of 8-300 characters each.";
            return "CONTRACT_SHAPE: Match the exact documented JSON shape and casing.";
        }
        catch (JsonException)
        {
            return "JSON_SYNTAX: Return valid complete JSON with no prose or fences.";
        }
    }

    private static bool TextInRange(JsonElement value, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.String)
            return false;
        var text = value.GetString()?.Trim();
        return text is not null && text.Length >= min && text.Length <= max &&
            !text.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\t'));
    }

    private static bool TextArrayInRange(JsonElement value, int minItems,
        int maxItems, int minChars, int maxChars) =>
        value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() >= minItems && value.GetArrayLength() <= maxItems &&
        value.EnumerateArray().All(item => TextInRange(item, minChars, maxChars));

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
