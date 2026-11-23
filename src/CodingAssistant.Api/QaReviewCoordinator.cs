using System.Collections.Concurrent;
using System.Text.Json;

namespace CodingAssistant.Api;

/// <summary>
/// Human-initiated QA starts only after the independent MAF Software Engineer
/// has produced a terminal, plan-bound source run with at least one saved
/// source file. Even failed or interrupted submissions can be assessed as
/// incomplete source; running jobs are excluded. Reviews one saved file per
/// real QA MAF agent call, writes evidence transactionally and never executes
/// source code, builds, tests or external tools.
/// </summary>
public sealed class QaReviewCoordinator(
    TaskStore tasks, InterviewStore interviews,
    ImplementationPlanStore plans, EngineeringRunStore engineering,
    QaReviewStore reviews, IQaSourceReviewer reviewer,
    IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public QaReviewRun Start(string taskId, StartQaReviewRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.EngineeringRunId) ||
            string.IsNullOrWhiteSpace(request.PlanSha256))
            throw new StoreFailure("INVALID_INPUT");
        var source = engineering.Get(taskId, request.EngineeringRunId);
        if (!QaReviewStore.IsReviewableSource(
                source.Job.State, source.Files.Count))
            throw new StoreFailure("BUILD_NOT_COMPLETE");
        var plan = plans.Read(taskId).Plan;
        if (plan is null || !plan.Current || plan.Status != "Approved" ||
            plan.PlanSha256 != request.PlanSha256 ||
            plan.PlanSha256 != source.Job.PlanSha256 ||
            plan.Revision != source.Job.PlanRevision)
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
        var job = reviews.Start(taskId, source, plan);
        var token = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.ApplicationStopping);
        _active[job.Id] = token;
        var objective = tasks.GetTask(taskId).Task.Objective;
        _ = Task.Run(() => RunAsync(objective, specification.Content,
            plan, architecture, source, job, token));
        return job;
    }

    private async Task RunAsync(string objective, string specification,
        ImplementationPlan plan, DraftArchitectureReadiness architecture,
        EngineeringRunSnapshot source, QaReviewRun review,
        CancellationTokenSource cancellation)
    {
        try
        {
            var token = cancellation.Token;
            // Preserve the owner-confirmed issue across the engineer→QA
            // handoff. Only an exact bound repair run supplies this evidence.
            // QA must independently inspect the changed file and decide
            // whether the concern still appears in current source.
            IReadOnlyList<RepairFindingReference> previousFindings = [];
            using (var db = tasks.Open())
            {
                var recorded = TaskStore.Scalar(db, null, """
                    SELECT findings_json FROM repair_cycles
                    WHERE engineering_run_id=@run AND task_id=@task
                    """, ("@run", source.Job.Id), ("@task", source.Job.TaskId));
                if (recorded is not null)
                {
                    previousFindings = JsonSerializer.Deserialize<RepairFindingReference[]>(
                        recorded) ?? throw new StoreFailure("INVALID_STATE");
                    if (previousFindings.Count is < 1 or > 12 ||
                        previousFindings.Any(f => f.Classification != "ImplementationDefect" ||
                            !source.Files.Any(file => file.Path.Equals(f.Path,
                                StringComparison.OrdinalIgnoreCase))))
                        throw new StoreFailure("INVALID_STATE");
                }
            }
            foreach (var file in source.Files)
            {
                token.ThrowIfCancellationRequested();
                // QA reads source but does not write into the engineering run.
                var input = new QaReviewInput(objective, specification, plan,
                    architecture, source.Job, file, source.Files,
                    source.Events,
                    observation => reviews.RecordAtlasInspection(
                        review.TaskId, review.Id, file, observation),
                    previousFindings.Where(f => f.Path.Equals(file.Path,
                        StringComparison.OrdinalIgnoreCase)).ToArray());
                var report = await reviewer.ReviewAsync(input, token);
                token.ThrowIfCancellationRequested();
                reviews.SaveFileReport(review.TaskId, review.Id, file, report);
            }
            token.ThrowIfCancellationRequested();
            reviews.Finish(review.TaskId, review.Id, true, null);
        }
        catch (OperationCanceledException)
        {
            reviews.Interrupt(review.TaskId, review.Id);
        }
        catch (StoreFailure error)
        {
            reviews.Finish(review.TaskId, review.Id, false, error.Code);
        }
        catch (Exception)
        {
            // Never reveal raw generated source, proprietary prompts or paths.
            reviews.Finish(review.TaskId, review.Id, false, "QA_REVIEW_FAILED");
        }
        finally
        {
            _active.TryRemove(review.Id, out _);
            cancellation.Dispose();
        }
    }

    public QaReviewSnapshot Cancel(string taskId, string reviewId)
    {
        var current = reviews.Get(taskId, reviewId);
        if (current.Job.State == "Running")
        {
            if (_active.TryGetValue(reviewId, out var cancellation))
                cancellation.Cancel();
            else
                reviews.Interrupt(taskId, reviewId);
        }
        return reviews.Get(taskId, reviewId);
    }
}
