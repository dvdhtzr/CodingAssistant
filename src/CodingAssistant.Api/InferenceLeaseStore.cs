using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record InferenceLeaseReceipt(string Id,string TaskId,string AgentId,string ReferenceId,
    string? SourceSha256,string Model,string State,string StartedUtc,string? FinishedUtc);

/// <summary>Durable receipt of scoped provider work. A receipt is never a
/// source write permission, nor does recovery replay uncertain inference.</summary>
public sealed class InferenceLeaseStore(TaskStore tasks)
{
    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS engineering_inference_leases(
            id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            agent_id TEXT NOT NULL,reference_id TEXT NOT NULL,source_sha256 TEXT,
            model TEXT NOT NULL,state TEXT NOT NULL CHECK(state IN
                ('Active','ResponseAccepted','Stopped','Cancelled','Interrupted')),
            started_utc TEXT NOT NULL,finished_utc TEXT
        );
        CREATE INDEX IF NOT EXISTS engineering_inference_lease_task
            ON engineering_inference_leases(task_id,started_utc,id);
        """);
    internal static void Start(SqliteConnection db,SqliteTransaction tx,string id,string taskId,
        string agentId,string referenceId,string? sourceSha,string model)
    {
        if(TaskStore.FindTask(db,tx,taskId) is null ||
            !EngineeringRoleCapabilities.Allows(agentId,EngineeringCapability.SendMessage) ||
            !LocalInterviewModel.AllowedModel(model))
            throw new StoreFailure("INVALID_STATE");
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_inference_leases(id,task_id,agent_id,reference_id,source_sha256,model,state,started_utc)
            VALUES(@id,@task,@agent,@ref,@source,@model,'Active',@utc)
            """,("@id",id),("@task",taskId),("@agent",agentId),("@ref",referenceId),
            ("@source",(object?)sourceSha??DBNull.Value),("@model",model),("@utc",DateTimeOffset.UtcNow.ToString("O")));
    }
    internal static bool Finish(SqliteConnection db,SqliteTransaction tx,string id,string taskId,string state)
    {
        if(state is not ("ResponseAccepted" or "Stopped" or "Cancelled" or "Interrupted"))
            throw new StoreFailure("INVALID_STATE");
        return TaskStore.Execute(db,tx,"""
            UPDATE engineering_inference_leases SET state=@state,finished_utc=@utc
            WHERE id=@id AND task_id=@task AND state='Active'
            """,("@id",id),("@task",taskId),("@state",state),("@utc",DateTimeOffset.UtcNow.ToString("O")))==1;
    }
    private const string Columns="""
        SELECT id,task_id,agent_id,reference_id,source_sha256,model,state,started_utc,finished_utc
        FROM engineering_inference_leases
        """;
    private static InferenceLeaseReceipt Read(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),
        r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.GetString(5),
        r.GetString(6),r.GetString(7),r.IsDBNull(8)?null:r.GetString(8));
    public IReadOnlyList<InferenceLeaseReceipt> List(string taskId,int limit=100)
    {
        if(limit is <1 or >200)throw new StoreFailure("INVALID_INPUT");
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        if(TaskStore.FindTask(db,tx,taskId) is null)throw new StoreFailure("NOT_FOUND");
        var result=new List<InferenceLeaseReceipt>();
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task ORDER BY started_utc DESC,id DESC LIMIT @limit",
            ("@task",taskId),("@limit",limit)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())result.Add(Read(reader));
        tx.Commit();return result.AsReadOnly();
    }
    public void Recover()
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var active=new List<InferenceLeaseReceipt>();
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE state='Active'"))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read())active.Add(Read(reader));
        foreach(var receipt in active)
            if(Finish(db,tx,receipt.Id,receipt.TaskId,"Interrupted"))
                EngineeringActivityStore.Record(db,tx,receipt.TaskId,"OllamaRequestInterrupted",receipt.AgentId,
                    "OllamaRequest",receipt.Id,sourceSha256:receipt.SourceSha256,model:receipt.Model);
        tx.Commit();
        foreach(var task in active.Select(x=>x.TaskId).Distinct())tasks.SignalEngineeringActivity(task);
    }
}
