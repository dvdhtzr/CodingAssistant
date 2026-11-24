using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record WorkPackageSchedule(string WorkItemId,IReadOnlyList<string> DependsOn,
    int Priority,bool Suspended,string Risk,IReadOnlyList<string>? AllowedPaths=null,
    IReadOnlyList<string>? AdditionalVerification=null);
public sealed record AdaptiveWorkPackage(string Id,string Title,string Objective,
    IReadOnlyList<string> AcceptanceCriteria,IReadOnlyList<string> DependsOn,int Priority,
    bool Suspended,string Risk,IReadOnlyList<string>? AllowedPaths,
    string ArchitectureSha256,IReadOnlyList<string> VerificationRequirements);
public sealed record AdaptiveWorkPlan(string TaskId,int BasePlanRevision,string BasePlanSha256,
    int Revision,string WorkPlanSha256,string? BasedOnRunId,string? BasedOnSourceSha256,
    int PolicyRevision,string PolicySha256,IReadOnlyList<AdaptiveWorkPackage> Packages,
    string Rationale,string CreatedUtc);
public sealed record AdaptivePlanContext(string TaskId,string FromAgent,int PolicyRevision,
    string PolicySha256,string BasePlanSha256,string? ExpectedRunId,string? ExpectedSourceSha256);
public sealed record ReviseAdaptivePlanDraft(int ExpectedRevision,string? ExpectedWorkPlanSha256,
    IReadOnlyList<WorkPackageSchedule> Packages,string Rationale);
public sealed record WorkPackageReadiness(string Id,string State,IReadOnlyList<string> WaitingFor);
public sealed record AdaptivePlanView(AdaptiveWorkPlan? Plan,
    IReadOnlyList<AdaptiveWorkPlan> History,IReadOnlyList<WorkPackageReadiness> Readiness);

/// <summary>
/// Host-owned operational overlay on the immutable, owner-approved plan.
/// It cannot alter a mandatory objective, acceptance criterion, architecture
/// or required dependency. The graph can add safe dependencies, prioritize,
/// suspend or narrow source scope. No execution or source-write grant.
/// </summary>
public sealed class AdaptiveWorkPlanStore(TaskStore tasks)
{
    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS engineering_work_plans(
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            base_plan_sha256 TEXT NOT NULL,base_plan_revision INTEGER NOT NULL,
            revision INTEGER NOT NULL CHECK(revision>0),work_plan_sha256 TEXT NOT NULL,
            based_on_run_id TEXT,based_on_source_sha256 TEXT,policy_revision INTEGER NOT NULL,
            policy_sha256 TEXT NOT NULL,packages_json TEXT NOT NULL,rationale TEXT NOT NULL,created_utc TEXT NOT NULL,
            PRIMARY KEY(task_id,base_plan_sha256,revision)
        );
        CREATE INDEX IF NOT EXISTS engineering_work_plan_history
            ON engineering_work_plans(task_id,base_plan_sha256,revision);
        """);

    private static string Text(string? value,int min,int max)
    {
        if(value is null || value.Trim().Length<min || value.Trim().Length>max ||
            value.Any(char.IsControl))throw new StoreFailure("INVALID_WORK_PLAN");
        return value.Trim();
    }

    // Pure validation: models propose schedules, the host retains the approved
    // semantics, validates the entire graph and rejects scope widening.
    public static IReadOnlyList<AdaptiveWorkPackage> Validate(ImplementationPlan plan,
        DraftArchitectureContract architecture,IReadOnlyList<WorkPackageSchedule> changes,
        IReadOnlyList<AdaptiveWorkPackage>? previous=null)
    {
        if(plan.Status!="Approved" || architecture.Sha256!=plan.ArchitectureSha256 ||
            changes is null || changes.Count!=plan.Outline.Tasks.Count)
            throw new StoreFailure("INVALID_WORK_PLAN");
        var ids=plan.Outline.Tasks.Select(x=>x.Id).ToHashSet(StringComparer.Ordinal);
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var packages=new List<AdaptiveWorkPackage>();
        foreach(var change in changes)
        {
            if(change is null || !ids.Contains(change.WorkItemId) || !seen.Add(change.WorkItemId) ||
                change.DependsOn is null || change.DependsOn.Count>ids.Count-1 ||
                change.DependsOn.Distinct(StringComparer.Ordinal).Count()!=change.DependsOn.Count ||
                change.DependsOn.Any(d=>d==change.WorkItemId || !ids.Contains(d)) ||
                change.Priority is <0 or >100 || change.Risk is not ("Low" or "Medium" or "High"))
                throw new StoreFailure("INVALID_WORK_PLAN");
            var approved=plan.Outline.Tasks.Single(x=>x.Id==change.WorkItemId);
            if(approved.DependsOn.Any(d=>!change.DependsOn.Contains(d,StringComparer.Ordinal)))
                throw new StoreFailure("INVALID_WORK_PLAN");
            string[]? scope=null;
            if(change.AllowedPaths is not null)
            {
                if(change.AllowedPaths.Count is <1 or >EngineeringRunStore.MaxDistinctFiles ||
                    change.AllowedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=change.AllowedPaths.Count)
                    throw new StoreFailure("INVALID_WORK_PLAN");
                scope=change.AllowedPaths.Select(ProjectDraftStore.SafePath)
                    .OrderBy(x=>x,StringComparer.Ordinal).ToArray();
                foreach(var file in scope)DraftArchitecturePolicy.ValidateSourcePath(architecture,file);
            }
            var prior=previous?.SingleOrDefault(x=>x.Id==approved.Id);
            if(prior?.AllowedPaths is not null &&
                (scope is null || scope.Any(p=>!prior.AllowedPaths.Contains(p,StringComparer.OrdinalIgnoreCase))))
                throw new StoreFailure("WORK_SCOPE_EXPANSION");
            var extra=change.AdditionalVerification??[];
            if(extra.Count>6)throw new StoreFailure("INVALID_WORK_PLAN");
            var checks=plan.Outline.TestStrategy.Concat(extra.Select(x=>Text(x,8,300)))
                .Concat(prior?.VerificationRequirements??[]).Distinct(StringComparer.Ordinal).ToArray();
            if(checks.Length>32)throw new StoreFailure("INVALID_WORK_PLAN");
            packages.Add(new(approved.Id,approved.Title,approved.Description,approved.AcceptanceCriteria,
                change.DependsOn.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),change.Priority,
                change.Suspended,change.Risk,scope,architecture.Sha256,checks));
        }
        // A deterministic topological sort detects cycles even when tasks are
        // suspended. No priority can make a blocked dependency runnable.
        _=Topological(packages);
        return packages.OrderBy(x=>Array.FindIndex(plan.Outline.Tasks.ToArray(),t=>t.Id==x.Id)).ToArray();
    }

    public static IReadOnlyList<string> Topological(IReadOnlyList<AdaptiveWorkPackage> packages)
    {
        var result=new List<string>();var completed=new HashSet<string>(StringComparer.Ordinal);
        while(result.Count<packages.Count)
        {
            var next=packages.Where(p=>!completed.Contains(p.Id) && p.DependsOn.All(completed.Contains))
                .OrderByDescending(p=>p.Priority).ThenBy(p=>p.Id,StringComparer.Ordinal).FirstOrDefault();
            if(next is null)throw new StoreFailure("WORK_DEPENDENCY_CYCLE");
            completed.Add(next.Id);result.Add(next.Id);
        }
        return result.AsReadOnly();
    }

    public static IReadOnlyList<WorkPackageReadiness> Evaluate(IReadOnlyList<AdaptiveWorkPackage> packages,
        IReadOnlyDictionary<string,string> states)
    {
        return packages.Select(p=>{
            var state=states.GetValueOrDefault(p.Id)??"Pending";
            var waits=p.DependsOn.Where(d=>states.GetValueOrDefault(d)!="Completed").ToArray();
            return new WorkPackageReadiness(p.Id,state!="Pending"?state:
                p.Suspended?"Suspended":waits.Length>0?"WaitingForDependency":
                states.Values.Any(s=>s=="Running")?"WaitingForWriter":"Ready",waits);
        }).ToArray();
    }
    public static string? Next(IReadOnlyList<AdaptiveWorkPackage> packages,
        IReadOnlyDictionary<string,string> states)
    {
        var ready=Evaluate(packages,states).Where(x=>x.State=="Ready").Select(x=>x.Id).ToHashSet();
        return packages.Where(p=>ready.Contains(p.Id)).OrderByDescending(p=>p.Priority)
            .ThenBy(p=>p.Id,StringComparer.Ordinal).FirstOrDefault()?.Id;
    }

    private const string Columns="""
        SELECT task_id,base_plan_revision,base_plan_sha256,revision,work_plan_sha256,
            based_on_run_id,based_on_source_sha256,policy_revision,policy_sha256,packages_json,rationale,created_utc
        FROM engineering_work_plans
        """;
    private static AdaptiveWorkPlan Decode(SqliteDataReader reader)=>new(reader.GetString(0),reader.GetInt32(1),
        reader.GetString(2),reader.GetInt32(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),
        reader.IsDBNull(6)?null:reader.GetString(6),reader.GetInt32(7),reader.GetString(8),
        JsonSerializer.Deserialize<AdaptiveWorkPackage[]>(reader.GetString(9))??throw new StoreFailure("INVALID_STATE"),
        reader.GetString(10),reader.GetString(11));

    internal static AdaptiveWorkPlan? Latest(SqliteConnection db,SqliteTransaction tx,string taskId,string planSha)
    {
        using var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND base_plan_sha256=@plan ORDER BY revision DESC LIMIT 1",
            ("@task",taskId),("@plan",planSha));
        using var reader=cmd.ExecuteReader();return reader.Read()?Decode(reader):null;
    }
    internal static Dictionary<string,string> States(SqliteConnection db,SqliteTransaction tx,string? runId)
    {
        var states=new Dictionary<string,string>(StringComparer.Ordinal);
        if(runId is null)return states;
        using var cmd=TaskStore.Command(db,tx,"SELECT work_item_id,state FROM engineering_run_tasks WHERE run_id=@run",("@run",runId));
        using var reader=cmd.ExecuteReader();
        while(reader.Read())states.Add(reader.GetString(0),reader.GetString(1));
        return states;
    }

    public AdaptivePlanView Read(string taskId)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        if(TaskStore.FindTask(db,tx,taskId) is null)throw new StoreFailure("NOT_FOUND");
        var history=new List<AdaptiveWorkPlan>();
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task ORDER BY created_utc,revision",("@task",taskId)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())history.Add(Decode(reader));
        ImplementationPlan? approved;
        try{approved=ImplementationPlanStore.RequireCurrentApproved(db,tx,taskId);}
        catch(StoreFailure){approved=null;}
        var latest=approved is null?null:history.LastOrDefault(x=>x.BasePlanSha256==approved.PlanSha256);
        var runId=TaskStore.Scalar(db,tx,"SELECT id FROM engineering_runs WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",("@task",taskId));
        var run=runId is null?null:EngineeringRunStore.Find(db,tx,runId);
        var states=States(db,tx,run?.PlanSha256==approved?.PlanSha256?runId:null);
        IReadOnlyList<WorkPackageReadiness> readiness=latest is null?[]:Evaluate(latest.Packages,states);
        tx.Commit();return new(latest,history.AsReadOnly(),readiness);
    }

    // Application-only scheduling authority. HTTP callers cannot impersonate
    // Technical Lead. Active-run binding is introduced with the scheduler.
    public AdaptiveWorkPlan Revise(AdaptivePlanContext context,ReviseAdaptivePlanDraft draft)
    {
        EngineeringRoleCapabilities.Require(context.FromAgent,EngineeringCapability.ScheduleWork);
        var rationale=Text(draft.Rationale,12,1000);
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        EngineeringDelegationStore.Require(db,tx,context.TaskId,context.PolicyRevision,
            context.PolicySha256,context.FromAgent,"ScheduleWork");
        var plan=ImplementationPlanStore.RequireCurrentApproved(db,tx,context.TaskId);
        if(plan.PlanSha256!=context.BasePlanSha256)throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
        var runId=TaskStore.Scalar(db,tx,"SELECT id FROM engineering_runs WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",("@task",context.TaskId));
        var sourceSha=runId is null?null:EngineeringMessageStore.SourceFingerprint(db,tx,runId);
        if(runId!=context.ExpectedRunId || sourceSha!=context.ExpectedSourceSha256)
            throw new StoreFailure("SOURCE_CHANGED");
        var run=runId is null?null:EngineeringRunStore.Find(db,tx,runId);
        if(run?.State=="Running")throw new StoreFailure("BUILD_BUSY");
        var previous=Latest(db,tx,context.TaskId,plan.PlanSha256);
        if(draft.ExpectedRevision!=(previous?.Revision??0) ||
            draft.ExpectedWorkPlanSha256!=previous?.WorkPlanSha256 ||
            draft.ExpectedRevision==int.MaxValue)
            throw new StoreFailure("STALE_WORK_PLAN");
        var architecture=DraftArchitecturePolicy.Capture(db,tx,context.TaskId,
            plan.SpecificationVersion,plan.SpecificationSha256)??throw new StoreFailure("ARCHITECTURE_CONFLICT");
        var packages=Validate(plan,architecture,draft.Packages,previous?.Packages);
        var revision=draft.ExpectedRevision+1;
        var sha=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new{
            schema=1,context.TaskId,plan.PlanSha256,planRevision=plan.Revision,revision,
            runId,sourceSha,context.PolicyRevision,context.PolicySha256,packages,rationale
        }))).ToLowerInvariant();
        var created=DateTimeOffset.UtcNow.ToString("O");
        var result=new AdaptiveWorkPlan(context.TaskId,plan.Revision,plan.PlanSha256,revision,sha,
            runId,sourceSha,context.PolicyRevision,context.PolicySha256,packages,rationale,created);
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_work_plans(task_id,base_plan_sha256,base_plan_revision,revision,work_plan_sha256,
                based_on_run_id,based_on_source_sha256,policy_revision,policy_sha256,packages_json,rationale,created_utc)
            VALUES(@task,@base,@baseRev,@rev,@sha,@run,@source,@policy,@policySha,@packages,@rationale,@created)
            """,("@task",context.TaskId),("@base",plan.PlanSha256),("@baseRev",plan.Revision),("@rev",revision),
            ("@sha",sha),("@run",(object?)runId??DBNull.Value),("@source",(object?)sourceSha??DBNull.Value),
            ("@policy",context.PolicyRevision),("@policySha",context.PolicySha256),
            ("@packages",JsonSerializer.Serialize(packages)),("@rationale",rationale),("@created",created));
        EngineeringActivityStore.Record(db,tx,context.TaskId,"WorkPlanRevised","TechnicalLead",
            "AdaptiveWorkPlan",sha,revision,sourceSha256:sourceSha);
        tx.Commit();tasks.SignalEngineeringActivity(context.TaskId);return result;
    }
}
