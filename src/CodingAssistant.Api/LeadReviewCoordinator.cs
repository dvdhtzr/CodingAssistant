using System.Collections.Concurrent;

namespace CodingAssistant.Api;

public sealed class LeadReviewCoordinator(TaskStore tasks,InterviewStore interviews,
    ImplementationPlanStore plans,EngineeringRunStore engineering,LeadReviewStore reviews,
    ILeadSourceReviewer reviewer,IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string,CancellationTokenSource> _active=new();
    public LeadReviewRun Start(string taskId,StartQaReviewRequest request,string model)
    {
        if(string.IsNullOrWhiteSpace(request.EngineeringRunId)||string.IsNullOrWhiteSpace(request.PlanSha256))
            throw new StoreFailure("INVALID_INPUT");
        var source=engineering.Get(taskId,request.EngineeringRunId);
        var plan=plans.Read(taskId).Plan;
        if(plan is null || !plan.Current || plan.Status!="Approved" ||
            plan.PlanSha256!=request.PlanSha256 || plan.PlanSha256!=source.Job.PlanSha256)
            throw new StoreFailure("PLAN_NOT_APPROVED");
        var spec=interviews.Read(taskId).Specification;
        if(spec is null || spec.Status!="Approved" || spec.Version!=plan.SpecificationVersion ||
            spec.Sha256!=plan.SpecificationSha256) throw new StoreFailure("SPECIFICATION_CONFLICT");
        var arch=DraftArchitecturePolicy.Readiness(tasks,interviews,taskId);
        if(!arch.ReadyForSourceGeneration || arch.ArchitectureSha256!=plan.ArchitectureSha256)
            throw new StoreFailure("ARCHITECTURE_CONFLICT");
        var job=reviews.Start(taskId,source,plan,model);
        var cancel=CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
        _active[job.Id]=cancel;
        var objective=tasks.GetTask(taskId).Task.Objective;
        _=Task.Run(async()=>{
            try {
                foreach(var file in source.Files)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    using var scope=EngineeringProviderActivity.Enter(tasks,taskId,"LeadSoftwareEngineer",job.Id,job.SourceSha256);
                    var report=await reviewer.ReviewAsync(new QaReviewInput(objective,spec.Content,plan,
                        arch,source.Job,file,source.Files,source.Events,
                        observation=>reviews.RecordInspection(taskId,job.Id,observation),AgentModel:model),cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    reviews.Save(taskId,job.Id,file,report);
                }
                cancel.Token.ThrowIfCancellationRequested();
                reviews.Finish(taskId,job.Id,"Reviewed");
            } catch(OperationCanceledException) {reviews.Finish(taskId,job.Id,"Interrupted","OWNER_CANCELLED");}
            catch(StoreFailure e) {reviews.Finish(taskId,job.Id,"Failed",e.Code);}
            catch(Exception) {reviews.Finish(taskId,job.Id,"Failed","LEAD_REVIEW_FAILED");}
            finally {_active.TryRemove(job.Id,out _);cancel.Dispose();}
        });
        return job;
    }
    public LeadReviewSnapshot Cancel(string taskId,string reviewId)
    {
        var snapshot=reviews.Get(taskId,reviewId);
        if(snapshot.Job.State=="Running")
        {
            if(_active.TryGetValue(reviewId,out var token)) token.Cancel();
            else reviews.Finish(taskId,reviewId,"Interrupted","OWNER_CANCELLED");
        }
        return reviews.Get(taskId,reviewId);
    }
}
