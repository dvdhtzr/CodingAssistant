using System.Collections.Concurrent;

namespace CodingAssistant.Api;

/// <summary>
/// One owner-initiated, durable, draft-only project generator. Each event
/// records a real completed API/model/file operation; no invented percentages.
/// The only side effect of model output is private SQLite TEXT storage.
/// </summary>
public sealed class ProjectDraftCoordinator(
    TaskStore tasks,
    InterviewStore interviews,
    ProjectDraftStore store,
    IProjectDraftModel model,
    IHostApplicationLifetime lifetime)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    public ProjectDraftJob Start(string taskId, StartProjectDraftRequest request)
    {
        var interview = interviews.Read(taskId);
        var spec = interview.Specification;
        if (spec is null || spec.Status != "Approved" ||
            request.SpecificationVersion != spec.Version ||
            request.SpecificationSha256 != spec.Sha256)
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        var task = tasks.GetTask(taskId).Task;
        var job = store.Start(taskId, spec.Version, spec.Sha256, spec.Model,
            request.ArchitectureSha256);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(
            lifetime.ApplicationStopping);
        _active[job.Id] = stop;
        // The job does not outlive this process or resume automatically.
        // It can be cancelled using the authenticated run-scoped API.
        _ = Task.Run(() => GenerateAsync(task, spec, job, stop));
        return job;
    }

    private void RequireCurrentApproval(string taskId, ProjectDraftJob job)
    {
        var latest = interviews.Read(taskId).Specification;
        if (latest is null || latest.Status != "Approved" ||
            latest.Version != job.SpecificationVersion ||
            latest.Sha256 != job.SpecificationSha256)
            throw new StoreFailure("SPECIFICATION_CONFLICT");
        store.RequireCurrentArchitecture(taskId, job.Id);
    }

    private async Task GenerateAsync(
        TaskItem task, InterviewSpecification spec,
        ProjectDraftJob job, CancellationTokenSource stop)
    {
        try
        {
            var token = stop.Token;
            RequireCurrentApproval(task.Id, job);
            var bound = store.GetArchitecture(task.Id, job.Id);
            var sourceSpecification = bound is null
                ? spec.Content
                : spec.Content + "\n\n" +
                  DraftArchitecturePolicy.AsModelInstructions(bound);
            store.Note(task.Id, job.Id, "ModelPlanning",
                "Asking local " + spec.Model + " to choose project files");
            async Task<IReadOnlyList<PlannedProjectFile>> AskPlanAsync(string context)
            {
                var output = model is IStreamingProjectDraftModel streamingPlan
                    ? await streamingPlan.PlanWithProgressAsync(
                        task.Objective, context, spec.Model,
                        () => {
                            store.NoteModelProgress(task.Id, job.Id);
                            return Task.CompletedTask;
                        }, token)
                    : await model.PlanAsync(task.Objective, context, spec.Model, token);
                return ProjectDraftStore.ValidatePlan(output);
            }

            var plan = await AskPlanAsync(sourceSpecification);
            try
            {
                DraftArchitecturePolicy.ValidatePlannedFiles(bound, plan);
            }
            catch (StoreFailure error) when (bound is not null &&
                error.Code == "ARCHITECTURE_PLAN_CONFLICT")
            {
                // Small local models can ignore an exact selected stack. One
                // explicit corrective plan attempt is allowed, never an
                // unbounded retry or a silent relaxation of validation.
                token.ThrowIfCancellationRequested();
                RequireCurrentApproval(task.Id, job);
                store.Note(task.Id, job.Id, "PlanCorrectionRequested",
                    "First file plan conflicted with approved architecture; asking local model to correct it");
                plan = await AskPlanAsync(sourceSpecification + "\n\n" +
                    DraftArchitecturePolicy.PlanningCorrectionInstructions(bound));
                DraftArchitecturePolicy.ValidatePlannedFiles(bound, plan);
            }
            token.ThrowIfCancellationRequested();
            RequireCurrentApproval(task.Id, job);
            store.Note(task.Id, job.Id, "PlanAccepted",
                plan.Count + " distinct files selected from approved specification");
            foreach (var file in plan)
            {
                token.ThrowIfCancellationRequested();
                RequireCurrentApproval(task.Id, job);
                store.Note(task.Id, job.Id, "FileStarted",
                    "Generating " + file.Path + " using local model");
                var previous = store.Get(task.Id, job.Id).Files;
                var content = model is IStreamingProjectDraftModel streamingWrite
                    ? await streamingWrite.WriteWithProgressAsync(
                        task.Objective, sourceSpecification, spec.Model, file, previous,
                        () => {
                            store.NoteModelProgress(task.Id, job.Id);
                            return Task.CompletedTask;
                        }, token)
                    : await model.WriteAsync(
                        task.Objective, sourceSpecification, spec.Model, file, previous, token);
                token.ThrowIfCancellationRequested();
                RequireCurrentApproval(task.Id, job);
                store.SaveFile(task.Id, job.Id, file, content);
            }
            store.Note(task.Id, job.Id, "StaticChecking",
                "Bounded source and JSON syntax checks completed");
            token.ThrowIfCancellationRequested();
            RequireCurrentApproval(task.Id, job);
            store.Finish(task.Id, job.Id, true, null);
        }
        catch (OperationCanceledException)
        {
            store.Interrupt(task.Id, job.Id);
        }
        catch (StoreFailure error)
        {
            store.Finish(task.Id, job.Id, false, error.Code);
        }
        catch (Exception)
        {
            // Never leak model output, exception paths or environment details.
            store.Finish(task.Id, job.Id, false, "GENERATION_FAILED");
        }
        finally
        {
            _active.TryRemove(job.Id, out _);
            stop.Dispose();
        }
    }

    public ProjectDraftSnapshot Cancel(string taskId, string jobId)
    {
        var snapshot = store.Get(taskId, jobId);
        if (snapshot.Job.State == "Running")
        {
            if (_active.TryGetValue(jobId, out var cancellation))
                cancellation.Cancel();
            else
                store.Interrupt(taskId, jobId);
        }
        return store.Get(taskId, jobId);
    }
}
