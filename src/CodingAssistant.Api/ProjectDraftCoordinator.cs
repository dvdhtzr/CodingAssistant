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

            IReadOnlyList<PlannedProjectFile> plan;
            try
            {
                plan = await AskPlanAsync(sourceSpecification);
                DraftArchitecturePolicy.ValidatePlannedFiles(bound, plan);
            }
            catch (StoreFailure error) when (error.Code is
                "INVALID_PROJECT_PLAN" or "INVALID_PROJECT_PATH" or
                "ARCHITECTURE_PLAN_CONFLICT")
            {
                // The model's file manifest is independent of the Technical
                // Lead's engineering task plan. A malformed JSON manifest,
                // bad/duplicate file metadata or stack mismatch deserves one
                // corrective model call, but never weaker validation.
                // Invalid files are never saved, and no second retry is made.
                token.ThrowIfCancellationRequested();
                RequireCurrentApproval(task.Id, job);
                store.Note(task.Id, job.Id, "PlanCorrectionRequested",
                    "File manifest rejected (" + error.Code +
                    "); requesting one correction from the selected local model");
                var correction = """
                    FILE PLAN CORRECTION REQUIRED:
                    Your previous file manifest was rejected by independent
                    validation. Return ONLY one JSON object in the exact shape
                    {"files":[{"path":"index.html","purpose":"Browser entry point"}]}.
                    This is a SOURCE FILE MANIFEST, not a Technical Lead task
                    breakdown. Do NOT return summary, tasks, dependencies,
                    acceptanceCriteria, a top-level array or source code.
                    Include 1 to 5 distinct safe relative paths, each with
                    a meaningful purpose of 5 to 240 characters. Paths must
                    contain only ASCII letters, numbers, _, -, . and /;
                    no leading dots, absolute paths, parent traversal,
                    scripts, credentials or generated build directories.
                    Respect the owner-approved specification and BOTH exact
                    architecture choices. No new tools or dependencies.
                    """;
                if (bound is not null)
                    correction += "\n\n" +
                        DraftArchitecturePolicy.PlanningCorrectionInstructions(bound);
                plan = await AskPlanAsync(sourceSpecification + "\n\n" + correction);
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
