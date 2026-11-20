namespace CodingAssistant.Api;

/// <summary>
/// The ONLY initial M2 → M1 coding scope, previously qualified on Dell.
/// Exact text avoids accidentally treating a freeform M2 planning task as
/// authorization to run M1's different, fixed coding assignment.
/// </summary>
public static class FrozenDemo
{
    public const string Title = "Qualified disposable C# repair demonstration";
    public const string Objective =
        "Repair only the frozen 02-build-repair C# compiler-error fixture in a disposable copy; no owner repositories.";
    public const string Summary =
        "Use the pinned qwen3.5:4b Gateway ACPX OpenCode workflow to repair only api/Program.cs of disposable 02-build-repair, then independently verify .NET offline.";

    public static bool Matches(TaskItem task, PlanItem plan, RunItem run)
    {
        return
            task.Id == run.TaskId &&
            task.CurrentVersion == run.PlanVersion &&
            task.Status == "Approved" &&
            task.Title == Title &&
            task.Objective == Objective &&
            plan.TaskId == task.Id &&
            plan.Version == run.PlanVersion &&
            plan.PlanHash == run.PlanHash &&
            plan.PlanHash == TaskStore.ComputePlanHash(
                task.Id, plan.Version, Summary) &&
            plan.Summary == Summary &&
            plan.Model == TaskStore.Model &&
            plan.Scope == TaskStore.Scope &&
            plan.Status == "Approved" &&
            !plan.ExecutionEnabled &&
            !run.ExecutionEnabled &&
            run.PlanCurrent &&
            run.State == "AwaitingExecutionAuthorization";
    }
}
