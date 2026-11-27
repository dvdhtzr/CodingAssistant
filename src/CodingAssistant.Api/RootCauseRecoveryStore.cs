using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// The original finding remains the repair identity. This separate receipt
// binds the one actual write target to a reviewed alternative diagnosis.
public sealed record RootCauseRepairTarget(string InvestigationId,string IssueId,string Origin,
    string ObservedPath,string ReviewId,int FindingIndex,string TargetPath,int Line,string Evidence,
    string Strategy,string Requirement,string HistorySha256);

internal static class RootCauseRecoveryStore
{
    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS engineering_root_cause_repairs(
            investigation_id TEXT PRIMARY KEY REFERENCES engineering_root_cause_investigations(id) ON DELETE CASCADE,
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            cycle_id TEXT NOT NULL UNIQUE,candidate_run_id TEXT NOT NULL UNIQUE,
            base_run_id TEXT NOT NULL,source_sha256 TEXT NOT NULL,plan_sha256 TEXT NOT NULL,
            policy_revision INTEGER NOT NULL,policy_sha256 TEXT NOT NULL,
            target_json TEXT NOT NULL
        );
        """);

    internal static RootCauseRepairTarget? ForCandidate(SqliteConnection db,SqliteTransaction? tx,
        string taskId,string candidateId)
    {
        var json=TaskStore.Scalar(db,tx,"""
            SELECT target_json FROM engineering_root_cause_repairs
            WHERE task_id=@task AND candidate_run_id=@candidate
            """,("@task",taskId),("@candidate",candidateId));
        return json is null?null:JsonSerializer.Deserialize<RootCauseRepairTarget>(json)
            ??throw new StoreFailure("INVALID_STATE");
    }

    internal static RootCauseRepairTarget? ForCandidate(TaskStore tasks,string taskId,string candidateId)
    {
        using var db=tasks.Open();
        return ForCandidate(db,null,taskId,candidateId);
    }

    // Select only actual supported alternative causes. Never treat an
    // unsupported, contradicted or merely differently worded old claim as
    // a defect. Keep the first increment to one different existing file.
    internal static IReadOnlyList<RootCauseFinding> SupportedAlternatives(RootCauseReceipt receipt,
        RootCauseInput input)
    {
        if(receipt.State!="Recorded" || receipt.HistorySha256!=input.HistorySha256 ||
            receipt.PolicyRevision!=input.Job.PolicyRevision || receipt.PolicySha256!=input.Job.PolicySha256 ||
            receipt.RunId!=input.Job.RunId || receipt.SourceSha256!=input.Job.SourceSha256 ||
            receipt.PlanSha256!=input.Job.PlanSha256 ||
            receipt.IndependentReview is not {State:"Recorded",Assessment:not null} ||
            RootCauseInvestigationStore.NeedsRefinement(receipt))return [];
        var proposal=receipt.Assessment;
        var review=receipt.IndependentReview.Assessment;
        if(receipt.Refinement is not null)
        {
            if(receipt.Refinement is not {State:"Recorded",Assessment:not null,IndependentAssessment:not null})
                return [];
            proposal=receipt.Refinement.Assessment;
            review=receipt.Refinement.IndependentAssessment;
        }
        if(proposal is null)return [];
        // Revalidate persisted output, including exact source evidence.
        proposal=MafRootCauseInvestigator.Parse(JsonSerializer.Serialize(proposal),input);
        review=MafRootCauseReviewer.Parse(JsonSerializer.Serialize(review),input,proposal);
        var attempted=input.Attempts.TakeLast(2).SelectMany(a=>a.Findings).Select(f=>f.Path)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return proposal.Findings.Where(f=>f.Outcome=="CandidateCause" &&
            f.CausePath is not null && f.Line is >0 && f.Evidence is {Length:>=8} &&
            f.AlternativeStrategy is not null &&
            input.ApprovedPaths.Contains(f.CausePath,StringComparer.Ordinal) &&
            !attempted.Contains(f.CausePath) &&
            input.Concerns.Any(c=>c.Id==f.IssueId && !c.Path.Equals(f.CausePath,StringComparison.OrdinalIgnoreCase)) &&
            review.Findings.Any(v=>v.IssueId==f.IssueId && v.Outcome=="Supported" && v.Path==f.CausePath))
            .OrderBy(f=>f.IssueId,StringComparer.Ordinal).ToArray();
    }

    internal static RootCauseRepairTarget? Select(SqliteConnection db,SqliteTransaction tx,
        CollaborationRun job,RepairProgressReport progress,RoutineRepairScope? scope)
    {
        if(scope is null || progress.Reason!="RECURRING_UNRESOLVED_FINDING")return null;
        var receipt=RootCauseInvestigationStore.Read(db,tx,job.TaskId,job.RunId,job.SourceSha256,job.PlanSha256);
        if(receipt is null || receipt.State!="Recorded" || RootCauseInvestigationStore.NeedsRefinement(receipt) ||
            receipt.PolicyRevision!=job.PolicyRevision || receipt.PolicySha256!=job.PolicySha256 ||
            TaskStore.Scalar(db,tx,"SELECT 1 FROM engineering_root_cause_repairs WHERE investigation_id=@id",
                ("@id",receipt.Id)) is not null)return null;
        // One alternative repair in this entire immutable ancestry. A later
        // recurring stall must not renew the strategy budget implicitly.
        foreach(var attempt in progress.Attempts)
            if(ForCandidate(db,tx,job.TaskId,attempt.CandidateRunId) is not null)return null;
        // Capture validates source/plan/policy and history even for a read-only
        // preview or the already checked dispatch checkpoint.
        var input=RootCauseInvestigationStore.Capture(db,tx,job with {State="Running"});
        var options=SupportedAlternatives(receipt,input);
        var issues=EngineeringIssueStore.Read(db,tx,job.TaskId,job.RunId).Issues;
        foreach(var option in options)
        {
            var issue=issues.SingleOrDefault(i=>i.Id==option.IssueId);
            if(issue is null || !issue.CurrentSource || !issue.LatestReview ||
                issue.RepairCycleId is not null || issue.State is ("WithdrawnByReviewer" or "StaticResolutionRecorded") ||
                issue.Origin!="QaEngineer" ||
                !input.Plan.Outline.Tasks.Any(w=>w.AcceptanceCriteria.Contains(issue.Requirement,StringComparer.Ordinal)))
                continue;
            // The original QA report must still be the actual current report.
            var latest=TaskStore.Scalar(db,tx,"""
                SELECT id FROM qa_reviews WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1
                """,("@task",job.TaskId));
            var reference=issue.References.LastOrDefault(x=>x.ReviewId==latest);
            if(reference is null)continue;
            RepairProposalStore.RequireCurrentReview(db,tx,job.TaskId,reference.ReviewId,false);
            return new(receipt.Id,issue.Id,issue.Origin,issue.Path,reference.ReviewId,reference.FindingIndex,
                option.CausePath!,option.Line!.Value,option.Evidence!,option.AlternativeStrategy!,issue.Requirement,input.HistorySha256);
        }
        return null;
    }

    internal static void Bind(SqliteConnection db,SqliteTransaction tx,CollaborationRun parent,
        RepairProposal proposal,IReadOnlyList<RepairFindingReference> selected,RootCauseRepairTarget target,
        string cycleId,string candidateRunId)
    {
        if(parent.State!="AwaitingOwner" || parent.Reason!="ROUTINE_REPAIR_DISPATCH_PENDING" ||
            parent.RunId!=proposal.EngineeringRunId || parent.SourceSha256!=proposal.SourceSha256 ||
            parent.PlanSha256!=proposal.PlanSha256 || selected.Count!=1 ||
            proposal.QaReviewId!=target.ReviewId || selected[0].Path!=target.ObservedPath ||
            selected[0].Index!=target.FindingIndex || selected[0].Requirement!=target.Requirement)
            throw new StoreFailure("REPAIR_NOT_AUTHORIZED");
        var scope=EngineeringDelegationStore.Latest(db,tx,parent.TaskId)?.RoutineRepair;
        var progress=RepairProgressStore.Read(db,tx,parent.TaskId,parent.RunId,scope);
        if(Select(db,tx,parent,progress,scope)!=target)
            throw new StoreFailure("REPAIR_NOT_AUTHORIZED");
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_root_cause_repairs(investigation_id,task_id,cycle_id,candidate_run_id,
                base_run_id,source_sha256,plan_sha256,policy_revision,policy_sha256,target_json)
            VALUES(@id,@task,@cycle,@candidate,@base,@source,@plan,@revision,@policy,@target)
            """,("@id",target.InvestigationId),("@task",parent.TaskId),("@cycle",cycleId),("@candidate",candidateRunId),
            ("@base",parent.RunId),("@source",parent.SourceSha256),("@plan",parent.PlanSha256),
            ("@revision",parent.PolicyRevision),("@policy",parent.PolicySha256),("@target",JsonSerializer.Serialize(target)));
    }
}
