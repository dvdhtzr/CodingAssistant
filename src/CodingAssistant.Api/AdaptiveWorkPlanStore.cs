using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record WorkSubpackageSchedule(string Id,string Objective,
    IReadOnlyList<int> AcceptanceCriteriaIndices,IReadOnlyList<string> AllowedPaths);
public sealed record AdaptiveWorkSubpackage(string Id,int Ordinal,string Objective,
    IReadOnlyList<string> AcceptanceCriteria,IReadOnlyList<string> AllowedPaths);
public sealed record WorkPackageSchedule(string WorkItemId,IReadOnlyList<string> DependsOn,
    int Priority,bool Suspended,string Risk,IReadOnlyList<string>? AllowedPaths=null,
    IReadOnlyList<string>? AdditionalVerification=null,
    IReadOnlyList<WorkSubpackageSchedule>? Subpackages=null);
public sealed record AdaptiveWorkPackage(string Id,string Title,string Objective,
    IReadOnlyList<string> AcceptanceCriteria,IReadOnlyList<string> DependsOn,int Priority,
    bool Suspended,string Risk,IReadOnlyList<string>? AllowedPaths,
    string ArchitectureSha256,IReadOnlyList<string> VerificationRequirements,
    IReadOnlyList<AdaptiveWorkSubpackage>? Subpackages=null);
public sealed record AdaptiveWorkPlan(string TaskId,int BasePlanRevision,string BasePlanSha256,
    int Revision,string WorkPlanSha256,string? BasedOnRunId,string? BasedOnSourceSha256,
    int PolicyRevision,string PolicySha256,IReadOnlyList<AdaptiveWorkPackage> Packages,
    string Rationale,string CreatedUtc);
public sealed record AdaptivePlanContext(string TaskId,string FromAgent,int PolicyRevision,
    string PolicySha256,string BasePlanSha256,string? ExpectedRunId,string? ExpectedSourceSha256);
public sealed record ReviseAdaptivePlanDraft(int ExpectedRevision,string? ExpectedWorkPlanSha256,
    IReadOnlyList<WorkPackageSchedule> Packages,string Rationale);
public sealed record WorkPackageReadiness(string Id,string State,IReadOnlyList<string> WaitingFor);
public sealed record AdaptivePlanSelection(int Revision,string WorkPlanSha256);
public sealed record AdaptiveAssignment(string RunId,string WorkItemId,int Revision,string WorkPlanSha256,
    string SourceBeforeSha256,string? SourceAfterSha256,string StartedUtc,string? CompletedUtc);
public sealed record AdaptiveSubpackageReceipt(string WorkItemId,string SubpackageId,int Ordinal,
    int Revision,string WorkPlanSha256,string SourceBeforeSha256,string? SourceAfterSha256,
    string StartedUtc,string? CompletedUtc,string? CarriedFromRunId);
internal sealed record PartialWorkCheckpoint(string WorkItemId,IReadOnlyList<string> AllowedPaths,
    IReadOnlyList<AdaptiveSubpackageReceipt> Receipts);
public sealed record AdaptivePlanView(AdaptiveWorkPlan? Plan,
    IReadOnlyList<AdaptiveWorkPlan> History,IReadOnlyList<WorkPackageReadiness> Readiness,
    string? BoundRunId=null,IReadOnlyList<AdaptiveAssignment>? Assignments=null,
    IReadOnlyList<AdaptiveSubpackageReceipt>? SubpackageReceipts=null);

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
        CREATE TABLE IF NOT EXISTS engineering_work_plan_bindings(
            run_id TEXT PRIMARY KEY REFERENCES engineering_runs(id) ON DELETE CASCADE,
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            base_plan_sha256 TEXT NOT NULL,revision INTEGER NOT NULL,work_plan_sha256 TEXT NOT NULL,
            FOREIGN KEY(task_id,base_plan_sha256,revision)
                REFERENCES engineering_work_plans(task_id,base_plan_sha256,revision)
        );
        CREATE TABLE IF NOT EXISTS engineering_adaptive_assignments(
            run_id TEXT NOT NULL REFERENCES engineering_runs(id) ON DELETE CASCADE,
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            work_item_id TEXT NOT NULL,revision INTEGER NOT NULL,work_plan_sha256 TEXT NOT NULL,
            source_before_sha256 TEXT NOT NULL,source_after_sha256 TEXT,started_utc TEXT NOT NULL,
            completed_utc TEXT,PRIMARY KEY(run_id,work_item_id)
        );
        CREATE TABLE IF NOT EXISTS engineering_adaptive_subpackages(
            run_id TEXT NOT NULL REFERENCES engineering_runs(id) ON DELETE CASCADE,
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            work_item_id TEXT NOT NULL,subpackage_id TEXT NOT NULL,ordinal INTEGER NOT NULL,
            revision INTEGER NOT NULL,work_plan_sha256 TEXT NOT NULL,source_before_sha256 TEXT NOT NULL,
            source_after_sha256 TEXT,started_utc TEXT NOT NULL,completed_utc TEXT,carried_from_run_id TEXT,
            PRIMARY KEY(run_id,work_item_id,subpackage_id),UNIQUE(run_id,work_item_id,ordinal)
        );
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
                if(approved.Id==plan.Outline.Tasks[0].Id &&
                    architecture.Choices.Any(c=>c.Topic=="technology-stack" && c.RuntimeProfileId=="static-web") &&
                    !scope.Contains("index.html",StringComparer.OrdinalIgnoreCase))
                    throw new StoreFailure("INVALID_WORK_PLAN");
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
            var steps=ValidateSubpackages(approved,architecture,change.Subpackages,scope,prior,
                approved.Id==plan.Outline.Tasks[0].Id);
            packages.Add(new(approved.Id,approved.Title,approved.Description,approved.AcceptanceCriteria,
                change.DependsOn.OrderBy(x=>x,StringComparer.Ordinal).ToArray(),change.Priority,
                change.Suspended,change.Risk,scope,architecture.Sha256,checks,steps));
        }
        // A deterministic topological sort detects cycles even when tasks are
        // suspended. No priority can make a blocked dependency runnable.
        _=Topological(packages);
        return packages.OrderBy(x=>Array.FindIndex(plan.Outline.Tasks.ToArray(),t=>t.Id==x.Id)).ToArray();
    }

    private static IReadOnlyList<AdaptiveWorkSubpackage>? ValidateSubpackages(ImplementationWorkItem approved,
        DraftArchitectureContract architecture,IReadOnlyList<WorkSubpackageSchedule>? drafts,
        IReadOnlyList<string>? parentScope,AdaptiveWorkPackage? prior,bool firstParent)
    {
        if(drafts is null || drafts.Count==0)
        {
            if(prior?.Subpackages is {Count:>0})throw new StoreFailure("WORK_SCOPE_EXPANSION");
            return null;
        }
        if(drafts.Count is <2 or >4)throw new StoreFailure("INVALID_WORK_PLAN");
        var ids=new HashSet<string>(StringComparer.Ordinal);
        var covered=new HashSet<int>();
        var result=new List<AdaptiveWorkSubpackage>();
        var priorSteps=prior?.Subpackages;
        var priorUnion=priorSteps?.SelectMany(x=>x.AllowedPaths).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var draft in drafts)
        {
            if(draft is null || draft.Id is null || draft.Id.Length is <2 or >40 ||
                !Regex.IsMatch(draft.Id,"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$",RegexOptions.CultureInvariant) ||
                !ids.Add(draft.Id) || draft.AcceptanceCriteriaIndices is null ||
                draft.AcceptanceCriteriaIndices.Count<1 ||
                draft.AcceptanceCriteriaIndices.Distinct().Count()!=draft.AcceptanceCriteriaIndices.Count ||
                draft.AcceptanceCriteriaIndices.Any(i=>i<0 || i>=approved.AcceptanceCriteria.Count) ||
                draft.AllowedPaths is null || draft.AllowedPaths.Count is <1 or >EngineeringRunStore.MaxDistinctFiles ||
                draft.AllowedPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=draft.AllowedPaths.Count)
                throw new StoreFailure("INVALID_WORK_PLAN");
            var scope=draft.AllowedPaths.Select(ProjectDraftStore.SafePath).OrderBy(x=>x,StringComparer.Ordinal).ToArray();
            var oldStep=priorSteps?.SingleOrDefault(x=>x.Id==draft.Id);
            if(scope.Any(path=>(parentScope is not null && !parentScope.Contains(path,StringComparer.OrdinalIgnoreCase)) ||
                (priorUnion is not null && !priorUnion.Contains(path)) ||
                (oldStep is not null && !oldStep.AllowedPaths.Contains(path,StringComparer.OrdinalIgnoreCase))))
                throw new StoreFailure("WORK_SCOPE_EXPANSION");
            foreach(var path in scope)DraftArchitecturePolicy.ValidateSourcePath(architecture,path);
            if(firstParent && result.Count==0 &&
                architecture.Choices.Any(c=>c.Topic=="technology-stack" && c.RuntimeProfileId=="static-web") &&
                !scope.Contains("index.html",StringComparer.OrdinalIgnoreCase))
                throw new StoreFailure("INVALID_WORK_PLAN");
            foreach(var index in draft.AcceptanceCriteriaIndices)covered.Add(index);
            result.Add(new(draft.Id,result.Count,Text(draft.Objective,12,600),
                draft.AcceptanceCriteriaIndices.Order().Select(i=>approved.AcceptanceCriteria[i]).ToArray(),scope));
        }
        if(covered.Count!=approved.AcceptanceCriteria.Count)throw new StoreFailure("INVALID_WORK_PLAN");
        return result.AsReadOnly();
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
        var bound=run is null?null:Bound(db,tx,run.Id);
        var boundRun=bound?.WorkPlanSha256==latest?.WorkPlanSha256 && bound is not null?runId:null;
        var states=States(db,tx,boundRun);
        IReadOnlyList<WorkPackageReadiness> readiness=latest is null?[]:Evaluate(latest.Packages,states);
        IReadOnlyList<AdaptiveAssignment> assignments=boundRun is null?[]:Assignments(db,tx,boundRun);
        IReadOnlyList<AdaptiveSubpackageReceipt> steps=boundRun is null?[]:SubpackageReceipts(db,tx,boundRun);
        tx.Commit();return new(latest,history.AsReadOnly(),readiness,boundRun,assignments,steps);
    }

    // Application-only scheduling authority. HTTP callers cannot impersonate
    // Technical Lead. Active revisions are accepted only between assignments.
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
        var bound=run is null?null:Bound(db,tx,run.Id);
        var active=run?.State=="Running";
        var states=States(db,tx,runId);
        if(active && (bound is null || states.Values.Any(s=>s is not ("Pending" or "Completed"))))
            throw new StoreFailure("BUILD_BUSY");
        var previous=Latest(db,tx,context.TaskId,plan.PlanSha256);
        if(draft.ExpectedRevision!=(previous?.Revision??0) ||
            draft.ExpectedWorkPlanSha256!=previous?.WorkPlanSha256 ||
            draft.ExpectedRevision==int.MaxValue)
            throw new StoreFailure("STALE_WORK_PLAN");
        var architecture=DraftArchitecturePolicy.Capture(db,tx,context.TaskId,
            plan.SpecificationVersion,plan.SpecificationSha256)??throw new StoreFailure("ARCHITECTURE_CONFLICT");
        if(active && previous?.WorkPlanSha256!=bound?.WorkPlanSha256)
            throw new StoreFailure("STALE_WORK_PLAN");
        var packages=Validate(plan,architecture,draft.Packages,previous?.Packages);
        if(active && bound is not null)
            foreach(var item in bound.Packages.Where(x=>states.GetValueOrDefault(x.Id)=="Completed"))
                if(JsonSerializer.Serialize(item)!=JsonSerializer.Serialize(packages.Single(x=>x.Id==item.Id)))
                    throw new StoreFailure("WORK_ALREADY_COMPLETED");
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
        if(active)
            TaskStore.Execute(db,tx,"""
                UPDATE engineering_work_plan_bindings SET revision=@rev,work_plan_sha256=@sha
                WHERE task_id=@task AND run_id=@run
                """,("@rev",revision),("@sha",sha),("@task",context.TaskId),("@run",runId!));
        EngineeringActivityStore.Record(db,tx,context.TaskId,"WorkPlanRevised","TechnicalLead",
            "AdaptiveWorkPlan",sha,revision,sourceSha256:sourceSha);
        tx.Commit();tasks.SignalEngineeringActivity(context.TaskId);return result;
    }

    internal static AdaptiveWorkPlan? Bound(SqliteConnection db,SqliteTransaction tx,string runId)
    {
        using var cmd=TaskStore.Command(db,tx,Columns+"""
             WHERE (task_id,base_plan_sha256,revision,work_plan_sha256) IN
                (SELECT task_id,base_plan_sha256,revision,work_plan_sha256
                 FROM engineering_work_plan_bindings WHERE run_id=@run)
            """,("@run",runId));
        using var reader=cmd.ExecuteReader();return reader.Read()?Decode(reader):null;
    }

    internal static AdaptiveWorkPlan RequireSelected(SqliteConnection db,SqliteTransaction tx,
        string taskId,string planSha,AdaptivePlanSelection selection)
    {
        var latest=Latest(db,tx,taskId,planSha);
        if(latest is null || latest.Revision!=selection.Revision || latest.WorkPlanSha256!=selection.WorkPlanSha256)
            throw new StoreFailure("STALE_WORK_PLAN");
        EngineeringDelegationStore.Require(db,tx,taskId,latest.PolicyRevision,
            latest.PolicySha256,"TechnicalLead","ScheduleWork");
        var priorRun=TaskStore.Scalar(db,tx,"SELECT id FROM engineering_runs WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",("@task",taskId));
        if(priorRun!=latest.BasedOnRunId ||
            (priorRun is null?null:EngineeringMessageStore.SourceFingerprint(db,tx,priorRun))!=latest.BasedOnSourceSha256)
            throw new StoreFailure("SOURCE_CHANGED");
        return latest;
    }

    internal static void Bind(SqliteConnection db,SqliteTransaction tx,EngineeringRun run,AdaptiveWorkPlan plan)
    {
        if(plan.TaskId!=run.TaskId || plan.BasePlanSha256!=run.PlanSha256 ||
            plan.BasePlanRevision!=run.PlanRevision)throw new StoreFailure("STALE_WORK_PLAN");
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_work_plan_bindings(run_id,task_id,base_plan_sha256,revision,work_plan_sha256)
            VALUES(@run,@task,@base,@rev,@sha)
            """,("@run",run.Id),("@task",run.TaskId),("@base",plan.BasePlanSha256),
            ("@rev",plan.Revision),("@sha",plan.WorkPlanSha256));
    }

    internal static AdaptiveWorkPlan? RequireBound(SqliteConnection db,SqliteTransaction tx,EngineeringRun run)
    {
        var bound=Bound(db,tx,run.Id);
        if(bound is null)return null;
        if(bound.TaskId!=run.TaskId || bound.BasePlanSha256!=run.PlanSha256 ||
            bound.WorkPlanSha256!=Latest(db,tx,run.TaskId,run.PlanSha256)?.WorkPlanSha256)
            throw new StoreFailure("STALE_WORK_PLAN");
        EngineeringDelegationStore.Require(db,tx,run.TaskId,bound.PolicyRevision,
            bound.PolicySha256,"TechnicalLead","ScheduleWork");
        return bound;
    }

    internal static AdaptiveWorkPackage? Admit(SqliteConnection db,SqliteTransaction tx,
        EngineeringRun run,string item)
    {
        var bound=RequireBound(db,tx,run);
        if(bound is null)return null;
        if(Next(bound.Packages,States(db,tx,run.Id))!=item)
            throw new StoreFailure("WORK_NOT_READY");
        var package=bound.Packages.Single(x=>x.Id==item);
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_adaptive_assignments(run_id,task_id,work_item_id,revision,
                work_plan_sha256,source_before_sha256,started_utc)
            VALUES(@run,@task,@item,@rev,@sha,@source,@utc)
            """,("@run",run.Id),("@task",run.TaskId),("@item",item),("@rev",bound.Revision),
            ("@sha",bound.WorkPlanSha256),("@source",EngineeringMessageStore.SourceFingerprint(db,tx,run.Id)),
            ("@utc",DateTimeOffset.UtcNow.ToString("O")));
        return package;
    }

    internal static AdaptiveWorkSubpackage? AdmitNextSubpackage(SqliteConnection db,SqliteTransaction tx,
        EngineeringRun run,string item)
    {
        var bound=RequireBound(db,tx,run)??throw new StoreFailure("STALE_WORK_PLAN");
        var package=bound.Packages.SingleOrDefault(x=>x.Id==item)??throw new StoreFailure("WORK_NOT_READY");
        if(package.Subpackages is not {Count:>0} steps ||
            States(db,tx,run.Id).GetValueOrDefault(item)!="Running")
            throw new StoreFailure("WORK_NOT_READY");
        var receipts=SubpackageReceipts(db,tx,run.Id).Where(x=>x.WorkItemId==item).ToArray();
        if(receipts.Any(x=>x.CompletedUtc is null))throw new StoreFailure("WORK_NOT_READY");
        if(receipts.Length==steps.Count)return null;
        if(receipts.Length>steps.Count || receipts.Where((x,i)=>x.Ordinal!=i || x.SubpackageId!=steps[i].Id).Any())
            throw new StoreFailure("STALE_WORK_PLAN");
        var next=steps[receipts.Length];
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_adaptive_subpackages(run_id,task_id,work_item_id,subpackage_id,ordinal,
                revision,work_plan_sha256,source_before_sha256,started_utc)
            VALUES(@run,@task,@item,@step,@ordinal,@rev,@sha,@source,@utc)
            """,("@run",run.Id),("@task",run.TaskId),("@item",item),("@step",next.Id),
            ("@ordinal",next.Ordinal),("@rev",bound.Revision),("@sha",bound.WorkPlanSha256),
            ("@source",EngineeringMessageStore.SourceFingerprint(db,tx,run.Id)),("@utc",DateTimeOffset.UtcNow.ToString("O")));
        return next;
    }

    internal static void RequireWrite(SqliteConnection db,SqliteTransaction tx,EngineeringRun run,
        string item,IReadOnlyList<EngineeringFileChange> files,string? subpackageId=null)
    {
        var bound=RequireBound(db,tx,run);
        if(bound is null)
        {
            if(subpackageId is not null)throw new StoreFailure("WORK_NOT_READY");
            return;
        }
        EngineeringRoleCapabilities.Require("SoftwareEngineer",EngineeringCapability.WriteSource);
        var package=bound.Packages.SingleOrDefault(p=>p.Id==item)??throw new StoreFailure("WORK_NOT_READY");
        var before=TaskStore.Scalar(db,tx,"""
            SELECT source_before_sha256 FROM engineering_adaptive_assignments
            WHERE run_id=@run AND work_item_id=@item AND revision=@rev AND work_plan_sha256=@sha AND completed_utc IS NULL
            """,("@run",run.Id),("@item",item),("@rev",bound.Revision),("@sha",bound.WorkPlanSha256));
        if(package.Suspended || before is null)throw new StoreFailure("STALE_WORK_PLAN");
        IReadOnlyList<string>? scope=package.AllowedPaths;
        if(package.Subpackages is {Count:>0} steps)
        {
            var step=steps.SingleOrDefault(x=>x.Id==subpackageId)??throw new StoreFailure("WORK_NOT_READY");
            before=TaskStore.Scalar(db,tx,"""
                SELECT source_before_sha256 FROM engineering_adaptive_subpackages
                WHERE run_id=@run AND work_item_id=@item AND subpackage_id=@step AND revision=@rev
                    AND work_plan_sha256=@sha AND completed_utc IS NULL
                """,("@run",run.Id),("@item",item),("@step",step.Id),("@rev",bound.Revision),("@sha",bound.WorkPlanSha256));
            scope=step.AllowedPaths;
        }
        else if(subpackageId is not null)throw new StoreFailure("WORK_NOT_READY");
        if(before!=EngineeringMessageStore.SourceFingerprint(db,tx,run.Id))
            throw new StoreFailure("STALE_WORK_PLAN");
        if(scope is not null && files.Any(f=>!scope.Contains(f.Path,StringComparer.OrdinalIgnoreCase)))
            throw new StoreFailure("WORK_SCOPE_VIOLATION");
    }

    // Returns whether all child packages have committed. A parent remains
    // Running until every child has saved its actual source transaction.
    internal static bool CompleteSubpackage(SqliteConnection db,SqliteTransaction tx,EngineeringRun run,
        string item,string subpackageId)
    {
        var bound=RequireBound(db,tx,run)??throw new StoreFailure("STALE_WORK_PLAN");
        var steps=bound.Packages.Single(x=>x.Id==item).Subpackages??throw new StoreFailure("WORK_NOT_READY");
        if(TaskStore.Execute(db,tx,"""
            UPDATE engineering_adaptive_subpackages SET source_after_sha256=@source,completed_utc=@utc
            WHERE run_id=@run AND work_item_id=@item AND subpackage_id=@step AND work_plan_sha256=@sha
                AND completed_utc IS NULL
            """,("@source",EngineeringMessageStore.SourceFingerprint(db,tx,run.Id)),
            ("@utc",DateTimeOffset.UtcNow.ToString("O")),("@run",run.Id),("@item",item),
            ("@step",subpackageId),("@sha",bound.WorkPlanSha256))!=1)
            throw new StoreFailure("STALE_WORK_PLAN");
        return SubpackageReceipts(db,tx,run.Id).Count(x=>x.WorkItemId==item && x.CompletedUtc is not null)==steps.Count;
    }

    internal static IReadOnlyList<AdaptiveSubpackageReceipt> SubpackageReceipts(SqliteConnection db,SqliteTransaction tx,string runId)
    {
        var result=new List<AdaptiveSubpackageReceipt>();
        using var cmd=TaskStore.Command(db,tx,"""
            SELECT work_item_id,subpackage_id,ordinal,revision,work_plan_sha256,source_before_sha256,
                source_after_sha256,started_utc,completed_utc,carried_from_run_id
            FROM engineering_adaptive_subpackages WHERE run_id=@run ORDER BY work_item_id,ordinal
            """,("@run",runId));
        using var reader=cmd.ExecuteReader();
        while(reader.Read())result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt32(2),
            reader.GetInt32(3),reader.GetString(4),reader.GetString(5),reader.IsDBNull(6)?null:reader.GetString(6),
            reader.GetString(7),reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetString(9)));
        return result.AsReadOnly();
    }

    internal static PartialWorkCheckpoint? PartialCheckpoint(SqliteConnection db,SqliteTransaction tx,
        EngineeringRun run,AdaptiveWorkPlan plan,IReadOnlyList<EngineeringAssignedTask> carried)
    {
        var groups=SubpackageReceipts(db,tx,run.Id).Where(x=>x.CompletedUtc is not null &&
            !carried.Any(c=>c.Id==x.WorkItemId)).GroupBy(x=>x.WorkItemId).ToArray();
        if(groups.Length==0)return null;
        if(groups.Length!=1)throw new StoreFailure("RESUME_NOT_AVAILABLE");
        var group=groups[0];var receipts=group.OrderBy(x=>x.Ordinal).ToArray();
        var package=plan.Packages.SingleOrDefault(x=>x.Id==group.Key)??throw new StoreFailure("RESUME_NOT_AVAILABLE");
        var steps=package.Subpackages??throw new StoreFailure("RESUME_NOT_AVAILABLE");
        if(package.DependsOn.Any(d=>!carried.Any(c=>c.Id==d)) || receipts.Length>=steps.Count ||
            receipts.Where((x,i)=>x.Ordinal!=i || x.SubpackageId!=steps[i].Id ||
                x.WorkPlanSha256!=plan.WorkPlanSha256 || x.SourceAfterSha256 is null ||
                (i>0 && x.SourceBeforeSha256!=receipts[i-1].SourceAfterSha256)).Any() ||
            receipts[^1].SourceAfterSha256!=EngineeringMessageStore.SourceFingerprint(db,tx,run.Id))
            throw new StoreFailure("RESUME_NOT_AVAILABLE");
        return new(group.Key,steps.Take(receipts.Length).SelectMany(x=>x.AllowedPaths)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),receipts);
    }

    internal static void CarrySubpackages(SqliteConnection db,SqliteTransaction tx,
        EngineeringRun run,string originalRunId,PartialWorkCheckpoint checkpoint)
    {
        foreach(var receipt in checkpoint.Receipts)
            TaskStore.Execute(db,tx,"""
                INSERT INTO engineering_adaptive_subpackages(run_id,task_id,work_item_id,subpackage_id,ordinal,
                    revision,work_plan_sha256,source_before_sha256,source_after_sha256,started_utc,completed_utc,carried_from_run_id)
                VALUES(@run,@task,@item,@step,@ordinal,@rev,@sha,@before,@after,@started,@completed,@original)
                """,("@run",run.Id),("@task",run.TaskId),("@item",receipt.WorkItemId),("@step",receipt.SubpackageId),
                ("@ordinal",receipt.Ordinal),("@rev",receipt.Revision),("@sha",receipt.WorkPlanSha256),
                ("@before",receipt.SourceBeforeSha256),("@after",receipt.SourceAfterSha256!),
                ("@started",receipt.StartedUtc),("@completed",receipt.CompletedUtc!),("@original",originalRunId));
    }

    internal static void Complete(SqliteConnection db,SqliteTransaction tx,EngineeringRun run,string item)
    {
        var bound=Bound(db,tx,run.Id);
        if(bound is null)return;
        if(TaskStore.Execute(db,tx,"""
            UPDATE engineering_adaptive_assignments SET source_after_sha256=@source,completed_utc=@utc
            WHERE run_id=@run AND work_item_id=@item AND work_plan_sha256=@sha AND completed_utc IS NULL
            """,("@source",EngineeringMessageStore.SourceFingerprint(db,tx,run.Id)),
            ("@utc",DateTimeOffset.UtcNow.ToString("O")),("@run",run.Id),("@item",item),
            ("@sha",bound.WorkPlanSha256))!=1)throw new StoreFailure("STALE_WORK_PLAN");
    }

    internal static IReadOnlyList<AdaptiveAssignment> Assignments(SqliteConnection db,SqliteTransaction tx,string runId)
    {
        var items=new List<AdaptiveAssignment>();
        using var cmd=TaskStore.Command(db,tx,"""
            SELECT run_id,work_item_id,revision,work_plan_sha256,source_before_sha256,
                source_after_sha256,started_utc,completed_utc FROM engineering_adaptive_assignments
            WHERE run_id=@run ORDER BY started_utc,work_item_id
            """,("@run",runId));
        using var reader=cmd.ExecuteReader();
        while(reader.Read())items.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt32(2),
            reader.GetString(3),reader.GetString(4),reader.IsDBNull(5)?null:reader.GetString(5),
            reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7)));
        return items.AsReadOnly();
    }
}
