using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record RepairProgressAttempt(string CycleId,string OriginalRunId,string CandidateRunId,
    string OriginalSourceSha256,string CandidateSourceSha256,string State,
    IReadOnlyList<RepairFindingReference> Findings,string Origin,
    bool IndependentStaticResolution,bool RecurringExactFinding);
public sealed record RepairProgressReport(string RunId,IReadOnlyList<RepairProgressAttempt> Attempts,
    bool StrategyChangeRequired,string Reason,IReadOnlyList<string> StalledIssueIds);

/// <summary>Evidence-only recovery guard. No attempt-count ceiling. A repeated
/// source snapshot or consecutive unchanged, independently recurring defect
/// can require a change in strategy. Unknown evidence is not a success.</summary>
public sealed class RepairProgressStore(TaskStore tasks)
{
    public static RepairProgressReport Evaluate(string runId,IReadOnlyList<RepairProgressAttempt> attempts,
        IReadOnlyList<string> stalledIssueIds)
    {
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var repeated=false;
        foreach(var a in attempts)
        {
            seen.Add(a.OriginalSourceSha256);
            if(!seen.Add(a.CandidateSourceSha256))repeated=true;
        }
        // Require two actual consecutive recurring attempts, not an arbitrary
        // project-wide repair count or a model's assurance of improvement.
        var last=attempts.TakeLast(2).ToArray();
        var stalled=last.Length==2 && last.All(a=>a.RecurringExactFinding && !a.IndependentStaticResolution)
            && stalledIssueIds.Count>0;
        return new(runId,attempts,repeated||stalled,
            repeated?"REPEATED_SOURCE_SNAPSHOT":stalled?"RECURRING_UNRESOLVED_FINDING":
            attempts.Count==0?"NO_REPAIR_HISTORY":"NO_DETERMINISTIC_STALL_DETECTED",
            stalledIssueIds);
    }

    private static bool Matches(EngineeringIssue i,RepairFindingReference f,string origin)
        =>i.Origin==origin && i.Path.Equals(f.Path,StringComparison.OrdinalIgnoreCase) &&
          i.Requirement==f.Requirement && i.Evidence==f.Evidence && i.Recommendation==f.Recommendation;

    public RepairProgressReport Read(string taskId,string runId)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var result=Read(db,tx,taskId,runId);tx.Commit();return result;
    }

    internal static RepairProgressReport Read(SqliteConnection db,SqliteTransaction tx,string taskId,string runId)
    {
        var run=EngineeringRunStore.Find(db,tx,runId);
        if(run is null || run.TaskId!=taskId)throw new StoreFailure("NOT_FOUND");
        var current=EngineeringIssueStore.Read(db,tx,taskId,runId).Issues;
        var attempts=new List<RepairProgressAttempt>();
        var visited=new HashSet<string>(StringComparer.Ordinal);
        var cursor=runId;
        // A cycle links immutable source revisions. Follow actual lineage,
        // never compare unrelated projects or unrelated new engineering runs.
        while(visited.Add(cursor))
        {
            string id,original,source,state,origin,json;
            using(var cmd=TaskStore.Command(db,tx,"""
                SELECT c.id,c.base_run_id,c.base_source_sha256,c.state,c.findings_json,
                    CASE WHEN p.lead_review_id IS NULL THEN 'QaEngineer' ELSE 'LeadSoftwareEngineer' END
                FROM repair_cycles c JOIN repair_proposals p ON p.id=c.proposal_id
                WHERE c.task_id=@task AND c.engineering_run_id=@run AND p.plan_sha256=@plan
                """,("@task",taskId),("@run",cursor),("@plan",run.PlanSha256)))
            using(var reader=cmd.ExecuteReader())
            {
                if(!reader.Read())break;
                id=reader.GetString(0);original=reader.GetString(1);source=reader.GetString(2);
                state=reader.GetString(3);json=reader.GetString(4);origin=reader.GetString(5);
            }
            var findings=JsonSerializer.Deserialize<RepairFindingReference[]>(json)??throw new StoreFailure("INVALID_STATE");
            var candidate=EngineeringMessageStore.SourceFingerprint(db,tx,cursor);
            var candidateIssues=cursor==runId?current:EngineeringIssueStore.Read(db,tx,taskId,cursor).Issues;
            var prior=EngineeringIssueStore.Read(db,tx,taskId,original).Issues
                .Where(i=>i.RepairCycleId==id && findings.Any(f=>Matches(i,f,origin))).ToArray();
            // Only an actual independent resolution receipt counts as a static
            // disposition. This is deliberately not runtime verification.
            var resolved=prior.Length>0 && prior.All(i=>i.Verifications.Any(v=>
                v.CandidateRunId==cursor && v.CandidateSourceSha256==candidate &&
                v.Assessment.Outcome=="ResolvedByStaticEvidence"));
            attempts.Add(new(id,original,cursor,source,candidate,state,findings,origin,resolved,
                candidateIssues.Any(i=>findings.Any(f=>Matches(i,f,origin)))));
            cursor=original;
        }
        attempts.Reverse();
        var last=attempts.TakeLast(2).ToArray();
        var stalled=last.Length!=2?[]:current.Where(i=>last.All(a=>a.Findings.Any(f=>Matches(i,f,a.Origin))))
            .Select(i=>i.Id).ToArray();
        return Evaluate(runId,attempts,stalled);
    }
}
