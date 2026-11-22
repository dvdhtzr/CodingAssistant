using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace CodingAssistant.Api;

/// <summary>
/// Bounded, explicitly authorized Software Engineer repair followed by
/// independent static QA. Source verification is evidence only: neither
/// source generation nor QA can claim functional test success.
/// </summary>
public sealed class RepairCycleCoordinator(
    TaskStore tasks, InterviewStore interviews,
    ImplementationPlanStore plans, EngineeringRunStore engineering,
    RepairCycleStore cycles, IEngineeringTaskModel engineer,
    QaReviewCoordinator qa, QaReviewStore reviews,
    EngineeringAtlasNotifier atlasNotifier,
    IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public RepairCycle Start(string taskId, StartRepairCycleRequest request)
    {
        var plan = plans.Read(taskId).Plan;
        if (plan is null || !plan.Current || plan.Status != "Approved")
            throw new StoreFailure("PLAN_NOT_APPROVED");
        var specification = interviews.Read(taskId).Specification;
        if (specification is null || specification.Status != "Approved" ||
            specification.Version != plan.SpecificationVersion ||
            specification.Sha256 != plan.SpecificationSha256)
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        var architecture = DraftArchitecturePolicy.Readiness(tasks, interviews, taskId);
        if (!architecture.ReadyForSourceGeneration ||
            architecture.ArchitectureSha256 != plan.ArchitectureSha256)
            throw new StoreFailure("ARCHITECTURE_CONFLICT");

        var admitted = cycles.Start(taskId, request, plan);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.ApplicationStopping);
        _active[admitted.Id] = cancellation;
        var objective = tasks.GetTask(taskId).Task.Objective;
        _ = Task.Run(() => RunAsync(objective, specification.Content,
            plan, architecture, admitted, cancellation));
        return admitted;
    }

    private async Task RunAsync(string objective, string specification,
        ImplementationPlan plan, DraftArchitectureReadiness architecture,
        RepairCycle cycle, CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token;
            var taskId = cycle.TaskId;
            var runId = cycle.EngineeringRunId;
            var workItemId = cycles.WorkItemId(taskId, cycle.Id);
            var item = plan.Outline.Tasks.Single(x => x.Id == workItemId);
            var files = engineering.Get(taskId, runId).Files;
            var allowed = cycle.Findings.Select(f => f.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            token.ThrowIfCancellationRequested();
            engineering.StartTask(taskId, runId, workItemId);
            var changes = await engineer.ImplementAsync(
                new EngineeringTaskInput(objective, specification, plan,
                    architecture, item, files,
                    new RepairTaskContext(cycle.ProposalId, cycle.BaseSourceSha256,
                        cycle.Findings, allowed)), token);
            token.ThrowIfCancellationRequested();
            // Recheck model output independently of its claims and prompt.
            var safe = MafSoftwareEngineer.Parse(
                System.Text.Json.JsonSerializer.Serialize(changes));
            if (safe.Files.Count < 1 || safe.Files.Any(f =>
                !allowed.Contains(f.Path, StringComparer.OrdinalIgnoreCase)))
                throw new StoreFailure("REPAIR_SCOPE_VIOLATION");
            if (!safe.Files.Any(f => files.Any(original =>
                original.Path.Equals(f.Path, StringComparison.OrdinalIgnoreCase)
                && original.Sha256 != Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(f.Content)))
                    .ToLowerInvariant())))
                throw new StoreFailure("REPAIR_NO_CHANGE");
            var atlasChange = engineering.CompleteTask(
                taskId, runId, workItemId, safe);
            // Repair changes are source-committed just like ordinary Software
            // Engineer tasks. Publish only the durable post-commit hint.
            await atlasNotifier.PublishAsync(atlasChange);
            token.ThrowIfCancellationRequested();
            engineering.Finish(taskId, runId, true, null);
            cycles.SetState(taskId, cycle.Id, "Generated");

            // Only static source inspection follows automatically. No browser,
            // Docker, shell or arbitrary generated code is ever executed here.
            var nextQa = qa.Start(taskId, new StartQaReviewRequest(
                runId, plan.PlanSha256));
            cycles.SetState(taskId, cycle.Id, "QaRunning", qaId: nextQa.Id);
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var state = reviews.Get(taskId, nextQa.Id).Job.State;
                if (state != "Running")
                {
                    if (state == "Reviewed")
                        cycles.SetState(taskId, cycle.Id, "QaReviewed");
                    else
                        cycles.SetState(taskId, cycle.Id, "Failed",
                            "QA_" + state.ToUpperInvariant());
                    break;
                }
                await Task.Delay(750, token);
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                var current = cycles.Get(cycle.TaskId, cycle.Id);
                if (current.Review?.Job.State == "Running")
                    qa.Cancel(cycle.TaskId, current.Review.Job.Id);
            }
            catch (StoreFailure) { }
            try { engineering.Interrupt(cycle.TaskId, cycle.EngineeringRunId); }
            catch (StoreFailure) { }
            try { cycles.SetState(cycle.TaskId, cycle.Id, "Interrupted",
                "OWNER_CANCELLED"); }
            catch (StoreFailure) { }
        }
        catch (StoreFailure error)
        {
            TryFail(cycle, error.Code);
        }
        catch (Exception)
        {
            TryFail(cycle, "REPAIR_FAILED");
        }
        finally
        {
            _active.TryRemove(cycle.Id, out _);
            cancellation.Dispose();
        }
    }

    private void TryFail(RepairCycle cycle, string code)
    {
        try { engineering.Finish(cycle.TaskId, cycle.EngineeringRunId, false, code); }
        catch (StoreFailure) { }
        try { cycles.SetState(cycle.TaskId, cycle.Id, "Failed", code); }
        catch (StoreFailure) { }
    }

    public RepairCycleSnapshot Cancel(string taskId, string cycleId)
    {
        var current = cycles.Get(taskId, cycleId);
        if (current.Cycle.State is "Running" or "QaRunning")
        {
            if (_active.TryGetValue(cycleId, out var cancellation))
                cancellation.Cancel();
            else
            {
                if (current.Review?.Job.State == "Running")
                    qa.Cancel(taskId, current.Review.Job.Id);
                if (current.Engineering.Job.State == "Running")
                    engineering.Interrupt(taskId, current.Engineering.Job.Id);
                cycles.SetState(taskId, cycleId, "Interrupted", "OWNER_CANCELLED");
            }
        }
        return cycles.Get(taskId, cycleId);
    }
}
