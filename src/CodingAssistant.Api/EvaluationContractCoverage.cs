using System.Text.Json;

namespace CodingAssistant.Api;

// Structural coverage only. The exact mapping still needs independent
// semantic review. This catalog intentionally contains only the existing
// reviewed Calculation Journal evaluator; no fallback for other projects.
internal static class EvaluationContractCoverage
{
    internal const string CalculationJournal="calculation-journal-v1";
    private static readonly string[] RequiredCases=[
        "mouse_2_plus_3_equals_5","keyboard_12_5_divided_by_2",
        "decimal_0_1_plus_0_2","backspace_and_divide_by_zero",
        "back_and_history_recall","history_survives_page_reload",
        "history_survives_browser_restart"
    ];
    private sealed record Mapping(string CriterionId,string CriterionText,string[] Cases);
    private static bool Exact(JsonElement value,params string[] fields)
    {
        if(value.ValueKind!=JsonValueKind.Object)return false;
        var names=value.EnumerateObject().Select(p=>p.Name).ToArray();
        return names.Length==fields.Length&&names.ToHashSet(StringComparer.Ordinal).SetEquals(fields);
    }
    internal static string Validate(JsonElement mappings,IReadOnlyList<EvaluationCriterion> criteria)
    {
        try
        {
            if(mappings.ValueKind!=JsonValueKind.Array||mappings.GetArrayLength()!=criteria.Count||
                criteria.Count is <1 or >64)
                throw new StoreFailure("EVALUATION_CONTRACT_COVERAGE_INVALID");
            var expected=criteria.ToDictionary(c=>c.Id,c=>c.Text,StringComparer.Ordinal);
            var seen=new HashSet<string>(StringComparer.Ordinal);
            var covered=new HashSet<string>(StringComparer.Ordinal);
            var result=new List<Mapping>();
            foreach(var mapping in mappings.EnumerateArray())
            {
                if(!Exact(mapping,"criterionId","criterionText","cases"))
                    throw new StoreFailure("EVALUATION_CONTRACT_COVERAGE_INVALID");
                var id=mapping.GetProperty("criterionId").GetString();
                var text=mapping.GetProperty("criterionText").GetString();
                var cases=mapping.GetProperty("cases");
                if(id is null||text is null||!expected.TryGetValue(id,out var expectedText)||
                    text!=expectedText||!seen.Add(id)||cases.ValueKind!=JsonValueKind.Array||
                    cases.GetArrayLength() is <1 or >7)
                    throw new StoreFailure("EVALUATION_CONTRACT_COVERAGE_INVALID");
                var ids=cases.EnumerateArray().Select(c=>c.GetString()??"").ToArray();
                if(ids.Distinct(StringComparer.Ordinal).Count()!=ids.Length||
                    ids.Any(c=>!RequiredCases.Contains(c,StringComparer.Ordinal)))
                    throw new StoreFailure("EVALUATION_CONTRACT_COVERAGE_INVALID");
                covered.UnionWith(ids);
                result.Add(new(id,text,ids.OrderBy(c=>c,StringComparer.Ordinal).ToArray()));
            }
            if(!covered.SetEquals(RequiredCases))
                throw new StoreFailure("EVALUATION_MANDATORY_CASE_MISSING");
            return JsonSerializer.Serialize(result.OrderBy(m=>m.CriterionId,StringComparer.Ordinal));
        }
        catch(Exception e) when(e is InvalidOperationException or ArgumentException or KeyNotFoundException)
        {throw new StoreFailure("EVALUATION_CONTRACT_COVERAGE_INVALID");}
    }
    internal static string ValidateSelection(JsonElement contract,EvaluationPlanningInput input)
    {
        try
        {
            if(!Exact(contract,"contractId","planSha256","specificationSha256","architectureSha256","mappings")||
                contract.GetProperty("contractId").GetString()!=CalculationJournal||
                contract.GetProperty("planSha256").GetString()!=input.Scope.PlanSha256||
                contract.GetProperty("specificationSha256").GetString()!=input.Scope.SpecificationSha256||
                contract.GetProperty("architectureSha256").GetString()!=input.Scope.ArchitectureSha256)
                throw new StoreFailure("EVALUATION_CONTRACT_NOT_CURRENT");
            return Validate(contract.GetProperty("mappings"),input.Criteria);
        }
        catch(Exception e) when(e is InvalidOperationException or KeyNotFoundException)
        {throw new StoreFailure("EVALUATION_CONTRACT_NOT_CURRENT");}
    }
}
