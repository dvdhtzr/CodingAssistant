using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record SourceSmokeEvidence(string Id,string TaskId,string RunId,string SourceSha256,
    string ScopeSha256,string PlanSha256,string State,LocalSourceSmokeResult? Report,
    string StartedUtc,string? FinishedUtc,bool Current=false,bool Verified=false);

/// <summary>Durable evidence from the pre-existing explicitly admitted smoke
/// lane. No new execution permission, test qualification or delivery claim.
/// A historical pass cannot certify a later source or approval revision.</summary>
public sealed class SourceSmokeEvidenceStore(TaskStore tasks)
{
    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS engineering_source_smoke(
            id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            run_id TEXT NOT NULL,source_sha256 TEXT NOT NULL,scope_sha256 TEXT NOT NULL,plan_sha256 TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Running','Recorded','Interrupted')),
            report_json TEXT,started_utc TEXT NOT NULL,finished_utc TEXT
        );
        CREATE UNIQUE INDEX IF NOT EXISTS engineering_source_smoke_active
            ON engineering_source_smoke(task_id) WHERE state='Running';
        """);
    private const string Columns="""
        SELECT id,task_id,run_id,source_sha256,scope_sha256,plan_sha256,state,report_json,
            started_utc,finished_utc FROM engineering_source_smoke
        """;
    private static SourceSmokeEvidence Decode(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),
        r.GetString(2),r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),
        r.IsDBNull(7)?null:JsonSerializer.Deserialize<LocalSourceSmokeResult>(r.GetString(7)),
        r.GetString(8),r.IsDBNull(9)?null:r.GetString(9));
    private static void RequireCurrent(SqliteConnection db,SqliteTransaction tx,SourceSmokeEvidence item)
    {
        var run=EngineeringRunStore.Find(db,tx,item.RunId);
        if(run is null || run.TaskId!=item.TaskId || run.State!="Generated" || run.PlanSha256!=item.PlanSha256 ||
            TaskStore.Scalar(db,tx,"SELECT id FROM engineering_runs WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",("@task",item.TaskId))!=run.Id ||
            EngineeringMessageStore.SourceFingerprint(db,tx,run.Id)!=item.SourceSha256)
            throw new StoreFailure("SOURCE_CHANGED");
        EngineeringRunStore.RequireBinding(db,tx,run);
    }
    private static bool Current(SqliteConnection db,SqliteTransaction tx,SourceSmokeEvidence item)
    {
        try{RequireCurrent(db,tx,item);return true;}
        catch(StoreFailure){return false;}
    }
    private static SourceSmokeEvidence Find(SqliteConnection db,SqliteTransaction tx,string taskId,string id)
    {
        using var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND id=@id",("@task",taskId),("@id",id));
        using var reader=cmd.ExecuteReader();return reader.Read()?Decode(reader):throw new StoreFailure("NOT_FOUND");
    }
    public SourceSmokeEvidence Start(StaticWebVerificationScopePreview scope,StartSourceSmokeRequest consent)
    {
        SourceSmokeCoordinator.RequireOwnerConsent(consent,scope);
        var item=new SourceSmokeEvidence(Guid.NewGuid().ToString("N"),scope.TaskId,scope.EngineeringRunId,
            scope.SourceFingerprint??throw new StoreFailure("SOURCE_SCOPE_CHANGED"),scope.ScopeSha256!,
            scope.PlanSha256,"Running",null,DateTimeOffset.UtcNow.ToString("O"),null,true);
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        RequireCurrent(db,tx,item);
        try{
            TaskStore.Execute(db,tx,"""
                INSERT INTO engineering_source_smoke(id,task_id,run_id,source_sha256,scope_sha256,plan_sha256,state,started_utc)
                VALUES(@id,@task,@run,@source,@scope,@plan,'Running',@utc)
                """,("@id",item.Id),("@task",item.TaskId),("@run",item.RunId),("@source",item.SourceSha256),
                ("@scope",item.ScopeSha256),("@plan",item.PlanSha256),("@utc",item.StartedUtc));
        }catch(SqliteException e)when(e.SqliteErrorCode==19){throw new StoreFailure("SMOKE_BUSY");}
        EngineeringActivityStore.Record(db,tx,item.TaskId,"SourceSmokeAdmitted","HumanOwner","SourceSmoke",item.Id,sourceSha256:item.SourceSha256);
        tx.Commit();tasks.SignalEngineeringActivity(item.TaskId);return item;
    }
    public SourceSmokeEvidence Finish(string taskId,string id,LocalSourceSmokeResult report)
    {
        // Validate the same runner contract even if an injected runner
        // returns contradictory or unsupported values.
        var safe=WindowsDockerSourceSmokeRunner.ParseRunnerResult(JsonSerializer.Serialize(new {
            status=report.Status,passed=report.Passed,required=report.Required,containerRemoved=report.ContainerRemoved,
            browserSandbox=report.BrowserSandbox,reason=report.Reason,checks=report.Checks,
            generatedArtifactVerified=false,independentFunctionalVerification=false
        }));
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State!="Running")throw new StoreFailure("INVALID_STATE");
        var now=DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db,tx,"UPDATE engineering_source_smoke SET state='Recorded',report_json=@report,finished_utc=@utc WHERE id=@id",
            ("@report",JsonSerializer.Serialize(safe)),("@utc",now),("@id",id));
        var current=Current(db,tx,item);
        EngineeringActivityStore.Record(db,tx,taskId,"SourceSmokeRecorded","QaEngineer","SourceSmoke",id,sourceSha256:item.SourceSha256);
        tx.Commit();tasks.SignalEngineeringActivity(taskId);
        return item with {State="Recorded",Report=safe,FinishedUtc=now,Current=current};
    }
    public void Interrupt(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State=="Running")
        {
            TaskStore.Execute(db,tx,"UPDATE engineering_source_smoke SET state='Interrupted',finished_utc=@utc WHERE id=@id",
                ("@utc",DateTimeOffset.UtcNow.ToString("O")),("@id",id));
            EngineeringActivityStore.Record(db,tx,taskId,"SourceSmokeInterrupted","QaEngineer","SourceSmoke",id,sourceSha256:item.SourceSha256);
        }
        tx.Commit();tasks.SignalEngineeringActivity(taskId);
    }
    public void Recover()
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var running=new List<SourceSmokeEvidence>();
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE state='Running'"))
        using(var reader=cmd.ExecuteReader())while(reader.Read())running.Add(Decode(reader));
        foreach(var item in running)
        {
            TaskStore.Execute(db,tx,"UPDATE engineering_source_smoke SET state='Interrupted',finished_utc=@utc WHERE id=@id",
                ("@utc",DateTimeOffset.UtcNow.ToString("O")),("@id",item.Id));
            EngineeringActivityStore.Record(db,tx,item.TaskId,"SourceSmokeInterrupted","QaEngineer","SourceSmoke",item.Id,sourceSha256:item.SourceSha256);
        }
        tx.Commit();
    }
    public IReadOnlyList<SourceSmokeEvidence> List(string taskId,string runId)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var run=EngineeringRunStore.Find(db,tx,runId);
        if(run is null || run.TaskId!=taskId)throw new StoreFailure("NOT_FOUND");
        var list=new List<SourceSmokeEvidence>();
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND run_id=@run ORDER BY started_utc DESC,id DESC",
            ("@task",taskId),("@run",runId)))
        using(var reader=cmd.ExecuteReader())while(reader.Read())list.Add(Decode(reader));
        var result=list.Select(i=>i with {Current=Current(db,tx,i)}).ToArray();
        tx.Commit();return result;
    }
}
