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
    EngineeringAtlasNotifier atlasNotifier, IHostApplicationLifetime lifetime,
    IServiceProvider services)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public EngineeringRun Start(string taskId, StartEngineeringRunRequest request,
        string? agentModel = null)
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
        if(request.ResumeRunId is not null && (request.WorkPlan is not null||request.UseApprovedTeamAuthorization) ||
            request.UseApprovedTeamAuthorization && request.WorkPlan is not null)
            throw new StoreFailure("INVALID_INPUT");
        var job = request.ResumeRunId is { } previousRunId
            ? store.Resume(taskId, plan, previousRunId, agentModel)
            : store.Start(taskId, plan, agentModel,request.WorkPlan,request.UseApprovedTeamAuthorization);
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
            async Task Implement(ImplementationWorkItem item,AdaptiveWorkPackage? package,
                AdaptiveWorkSubpackage? subpackage=null)
            {
                var prior = store.Get(job.TaskId, job.Id).Files;
                using var providerScope = EngineeringProviderActivity.Enter(tasks,job.TaskId,
                    "SoftwareEngineer",job.Id);
                var result = await engineer.ImplementAsync(
                    new EngineeringTaskInput(objective, spec, plan,
                        architecture, item, prior,
                        OnAtlasInspection: observation =>
                            store.RecordAtlasInspection(job.TaskId,job.Id,item.Id,observation),
                        OnOutputRejected: rejection =>
                            store.RecordOutputRejection(job.TaskId,job.Id,item.Id,rejection),
                        AgentModel: job.Model,AdaptivePackage:package,AdaptiveSubpackage:subpackage),token);
                token.ThrowIfCancellationRequested();
                var committed=store.CompleteTask(job.TaskId,job.Id,item.Id,result,subpackage?.Id);
                await atlasNotifier.PublishAsync(committed);
            }
            if(store.HasAdaptiveBinding(job.TaskId,job.Id))
            {
                while(true)
                {
                    token.ThrowIfCancellationRequested();
                    var saved=store.Get(job.TaskId,job.Id);
                    if(saved.Tasks.All(t=>t.State=="Completed"))break;
                    var package=store.StartNextAdaptiveTask(job.TaskId,job.Id);
                    if(package is null)
                    {
                        // Suspended or dependency-blocked work remains pending.
                        // No speculative model calls or unbounded busy loop.
                        await Task.Delay(750,token);
                        continue;
                    }
                    var item=plan.Outline.Tasks.Single(t=>t.Id==package.Id);
                    if(package.Subpackages is {Count:>0})
                    {
                        while(store.Get(job.TaskId,job.Id).Tasks.Single(t=>t.Id==item.Id).State=="Running")
                        {
                            token.ThrowIfCancellationRequested();
                            var step=store.StartNextSubpackage(job.TaskId,job.Id,item.Id)
                                ??throw new StoreFailure("WORK_NOT_READY");
                            await Implement(item,package,step);
                        }
                    }
                    else await Implement(item,package);
                }
            }
            else
                foreach(var item in plan.Outline.Tasks)
                {
                    token.ThrowIfCancellationRequested();
                    if(store.Get(job.TaskId,job.Id).Tasks.Any(t=>t.Id==item.Id && t.State=="Completed"))continue;
                    store.StartTask(job.TaskId,job.Id,item.Id);
                    await Implement(item,null);
                }
            token.ThrowIfCancellationRequested();
            store.Finish(job.TaskId, job.Id, true, null);
            // Only the one admitted run can materialize the future-file rule.
            // A changed plan, missing path or revoked scope stops here.
            if(new UpfrontTeamAuthorizationStore(tasks).Read(job.TaskId) is {State:"Claimed",RunId:var claimed}
                && claimed==job.Id)
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var next=new EngineeringDelegationStore(tasks).BindUpfront(job.TaskId,job.Id);
                    if(next is not null)
                        await services.GetRequiredService<CollaborationCoordinator>().StartAsync(job.TaskId,next,token);
                }
                catch(StoreFailure e)
                {new UpfrontTeamAuthorizationStore(tasks).MarkStopped(job.TaskId,job.Id,e.Code);}
                catch(OperationCanceledException)
                {store.Interrupt(job.TaskId,job.Id);}
                catch(Exception)
                {new UpfrontTeamAuthorizationStore(tasks).MarkStopped(job.TaskId,job.Id,"COLLABORATION_FAILED");}
            }
        }
        catch (OperationCanceledException)
        {
            store.Interrupt(job.TaskId, job.Id);
        }
        catch (StoreFailure error)
        {
            store.Finish(job.TaskId, job.Id, false, error.Code);
            new UpfrontTeamAuthorizationStore(tasks).MarkStopped(job.TaskId,job.Id,error.Code);
        }
        catch (Exception)
        {
            // User-visible failure categories never leak model contents,
            // private paths, credentials or environment details.
            store.Finish(job.TaskId, job.Id, false, "ENGINEERING_FAILED");
            new UpfrontTeamAuthorizationStore(tasks).MarkStopped(job.TaskId,job.Id,"ENGINEERING_FAILED");
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
        // Persist stop before signalling a model; a completed generation
        // may still be in its automatic coordination handoff.
        if(snapshot.Job.State=="Running" ||
            new UpfrontTeamAuthorizationStore(tasks).Read(taskId)?.RunId==runId)
        {
            store.Interrupt(taskId,runId);
            if(_active.TryGetValue(runId,out var cancellation))
                try{cancellation.Cancel();}catch(ObjectDisposedException){}
        }
        return store.Get(taskId, runId);
    }
}
