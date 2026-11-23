using System.Collections.Concurrent;

namespace CodingAssistant.Api;

/// <summary>
/// Host-managed, durable human-gated task-by-task MAF coordination.
/// An actual SoftwareEngineer MAF agent is invoked separately for each
/// approved Technical Lead task; the host owns ordering and persistence.
/// No generated code or model tools are ever executed.
/// </summary>
public sealed class EngineeringRunCoordinator(
    TaskStore tasks, InterviewStore interviews, ImplementationPlanStore plans,
    EngineeringRunStore store, IEngineeringTaskModel engineer,
    EngineeringAtlasNotifier atlasNotifier, IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public EngineeringRun Start(string taskId, StartEngineeringRunRequest request)
    {
        if (request.PlanRevision < 1 || string.IsNullOrEmpty(request.PlanSha256))
            throw new StoreFailure("INVALID_INPUT");
        var plan = plans.Read(taskId).Plan;
        if (plan is null || !plan.Current || plan.Status != "Approved")
            throw new StoreFailure("PLAN_NOT_APPROVED");
        if (plan.Revision != request.PlanRevision ||
            plan.PlanSha256 != request.PlanSha256)
            throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
        var spec = interviews.Read(taskId).Specification;
        if (spec is null || spec.Status != "Approved" ||
            spec.Version != plan.SpecificationVersion ||
            spec.Sha256 != plan.SpecificationSha256)
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        var arch = DraftArchitecturePolicy.Readiness(tasks, interviews, taskId);
        if (!arch.ReadyForSourceGeneration ||
            arch.ArchitectureSha256 != plan.ArchitectureSha256)
            throw new StoreFailure("ARCHITECTURE_CONFLICT");
        // Resuming is an explicit owner request. Store.Resume rechecks the
        // exact latest terminal run, plan/architecture/spec binding and
        // source hashes in one transaction before copying prior completions.
        var job = request.ResumeRunId is { } previousRunId
            ? store.Resume(taskId, plan, previousRunId)
            : store.Start(taskId, plan);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.ApplicationStopping);
        _active[job.Id] = cancellation;
        var objective = tasks.GetTask(taskId).Task.Objective;
        _ = Task.Run(() => RunAsync(objective, spec.Content, plan,
            arch, job, cancellation));
        return job;
    }

    private async Task RunAsync(
        string objective, string spec, ImplementationPlan plan,
        DraftArchitectureReadiness architecture, EngineeringRun job,
        CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token;
            foreach (var item in plan.Outline.Tasks)
            {
                token.ThrowIfCancellationRequested();
                // Never invoke the model again for a carried-forward completed
                // assignment; its source and status were copied atomically.
                var saved = store.Get(job.TaskId, job.Id);
                if (saved.Tasks.Any(t => t.Id == item.Id && t.State == "Completed"))
                    continue;
                store.StartTask(job.TaskId, job.Id, item.Id);
                var prior = store.Get(job.TaskId, job.Id).Files;
                var result = await engineer.ImplementAsync(
                    new EngineeringTaskInput(objective, spec, plan,
                        architecture, item, prior,
                        OnAtlasInspection: observation =>
                            store.RecordAtlasInspection(job.TaskId,
                                job.Id, item.Id, observation),
                        OnOutputRejected: rejection =>
                            store.RecordOutputRejection(job.TaskId,
                                job.Id, item.Id, rejection)), token);
                token.ThrowIfCancellationRequested();
                var committed = store.CompleteTask(job.TaskId, job.Id, item.Id, result);
                // Notification is best effort AFTER the durable transaction.
                // Missing SignalR messages are reconciled via atlas-events.
                await atlasNotifier.PublishAsync(committed);
            }
            token.ThrowIfCancellationRequested();
            store.Finish(job.TaskId, job.Id, true, null);
        }
        catch (OperationCanceledException)
        {
            store.Interrupt(job.TaskId, job.Id);
        }
        catch (StoreFailure error)
        {
            store.Finish(job.TaskId, job.Id, false, error.Code);
        }
        catch (Exception)
        {
            // User-visible failure categories never leak model contents,
            // private paths, credentials or environment details.
            store.Finish(job.TaskId, job.Id, false, "ENGINEERING_FAILED");
        }
        finally
        {
            _active.TryRemove(job.Id, out _);
            cancellation.Dispose();
        }
    }

    public EngineeringRunSnapshot Cancel(string taskId, string runId)
    {
        var snapshot = store.Get(taskId, runId);
        if (snapshot.Job.State == "Running")
        {
            if (_active.TryGetValue(runId, out var cancellation))
                cancellation.Cancel();
            else
                store.Interrupt(taskId, runId);
        }
        return store.Get(taskId, runId);
    }
}
