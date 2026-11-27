using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// The proposal is intentionally NOT a grant. Until a separately qualified
// host adapter exists, no transition can authorize or consume execution.
public sealed record EvaluationExecutionProposal(string Id,string TaskId,string PlanReviewId,
    string AuthorizationSha256,string ProfileSha256,string State,bool Current,
    bool ExecutionEnabled=false,bool RuntimeQualified=false);

public sealed class EvaluationExecutionAuthorizationStore(TaskStore tasks)
{
    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS evaluation_execution_proposals(
            id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            plan_review_id TEXT NOT NULL REFERENCES engineering_evaluation_plan_reviews(id) ON DELETE CASCADE,
            authorization_sha256 TEXT NOT NULL,profile_sha256 TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Proposed','Revoked','Interrupted')),
            created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,
            UNIQUE(task_id,authorization_sha256)
        );
        """);
    private static string Sha(string value)=>Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static (string Authorization,string Profile) Identity(SqliteConnection db,SqliteTransaction tx,
        string taskId,string planId)
    {
        var (plan,descriptor,contractId)=EvaluationPlanReviewStore.RequireReviewedAdmission(db,tx,taskId,planId);
        var profile=RuntimeProfileRegistry.Find("static-web")??throw new StoreFailure("EVALUATION_RUNTIME_UNQUALIFIED");
        var profileHash=Sha(JsonSerializer.Serialize(profile));
        // The descriptor digest pins the full immutable source, archive,
        // specification, architecture, cases and historical fixture receipt.
        // This is not current host qualification or a sandbox policy grant.
        var authorization=Sha(JsonSerializer.Serialize(new {
            schemaVersion=1,kind="evaluation-execution-proposal-only",
            plan.TaskId,plan.RunId,planReviewId=plan.Id,plan.AdmissionSha256,plan.DescriptorSha256,
            plan.ScopeSha256,plan.SourceSha256,plan.ArchiveSha256,contractReviewId=contractId,
            evaluatorSha256=descriptor.GetProperty("evaluatorSha256").GetString(),
            fixtureReceiptSha256=descriptor.GetProperty("qualification").GetProperty("receiptSha256").GetString(),
            profileSha256=profileHash
        }));
        return (authorization,profileHash);
    }
    private const string Columns="""
        SELECT id,task_id,plan_review_id,authorization_sha256,profile_sha256,state
        FROM evaluation_execution_proposals
        """;
    private static EvaluationExecutionProposal Decode(SqliteDataReader row)=>
        new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),row.GetString(5),false);
    private static EvaluationExecutionProposal Find(SqliteConnection db,SqliteTransaction tx,string taskId,string id)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(id);
        using var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND id=@id",
            ("@task",taskId),("@id",id));
        using var row=cmd.ExecuteReader();
        return row.Read()?Decode(row):throw new StoreFailure("NOT_FOUND");
    }
    private static bool Current(SqliteConnection db,SqliteTransaction tx,EvaluationExecutionProposal item)
    {
        if(item.State!="Proposed")return false;
        try
        {
            var identity=Identity(db,tx,item.TaskId,item.PlanReviewId);
            return item.AuthorizationSha256==identity.Authorization&&item.ProfileSha256==identity.Profile;
        }
        catch(StoreFailure){return false;}
    }
    public EvaluationExecutionProposal Propose(string taskId,string planReviewId)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var identity=Identity(db,tx,taskId,planReviewId);
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND authorization_sha256=@hash",
            ("@task",taskId),("@hash",identity.Authorization)))
        using(var row=cmd.ExecuteReader())
            if(row.Read())
            {
                var existing=Decode(row);row.Close();
                var current=Current(db,tx,existing);tx.Commit();
                return existing with {Current=current};
            }
        var id=Guid.NewGuid().ToString("N");
        var now=DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db,tx,"""
            INSERT INTO evaluation_execution_proposals(id,task_id,plan_review_id,authorization_sha256,profile_sha256,state,created_utc,updated_utc)
            VALUES(@id,@task,@plan,@hash,@profile,'Proposed',@now,@now)
            """,("@id",id),("@task",taskId),("@plan",planReviewId),("@hash",identity.Authorization),
            ("@profile",identity.Profile),("@now",now));
        tx.Commit();return new(id,taskId,planReviewId,identity.Authorization,identity.Profile,"Proposed",true);
    }
    public EvaluationExecutionProposal Get(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var item=Find(db,tx,taskId,id);
        var current=Current(db,tx,item);tx.Commit();return item with {Current=current};
    }
    public EvaluationExecutionProposal Revoke(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State=="Proposed")
        {
            TaskStore.Execute(db,tx,"UPDATE evaluation_execution_proposals SET state='Revoked',updated_utc=@now WHERE id=@id",
                ("@now",DateTimeOffset.UtcNow.ToString("O")),("@id",id));
            item=item with {State="Revoked"};
        }
        tx.Commit();return item with {Current=false};
    }
    // No owner prompt or worker dispatch calls this seam today. Even an exact
    // acknowledgement cannot override missing runtime/host qualification.
    public void Authorize(string taskId,string id,string confirmation)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(!Current(db,tx,item))throw new StoreFailure("EVALUATION_EXECUTION_PROPOSAL_NOT_CURRENT");
        if(confirmation!="AUTHORIZE EVALUATION "+item.AuthorizationSha256)
            throw new StoreFailure("EVALUATION_EXECUTION_CONFIRMATION_REQUIRED");
        var profile=RuntimeProfileRegistry.Find("static-web");
        if(profile is null||profile.QualificationStatus!="QUALIFIED"||!profile.ExecutableViaRegistry)
            throw new StoreFailure("EVALUATION_RUNTIME_UNQUALIFIED");
        // An eventual registry promotion alone is insufficient. Do not add
        // an executable state until current host/adapter qualification,
        // exact sandbox policy and single-use admission are implemented.
        throw new StoreFailure("EVALUATION_EXECUTION_ADAPTER_NOT_QUALIFIED");
    }
    public void Recover()
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        TaskStore.Execute(db,tx,"UPDATE evaluation_execution_proposals SET state='Interrupted',updated_utc=@now WHERE state='Proposed'",
            ("@now",DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
    }
}
