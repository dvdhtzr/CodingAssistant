using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record BehavioralContractReview(string Id,string TaskId,string ContractSha256,
    string EvaluatorSha256,string State,bool ExecutionEnabled=false);

// Host-internal semantic review receipt; reusable across source-only repairs
// under identical approved requirements. No executable grant or public route.
public sealed class BehavioralContractReviewStore(TaskStore tasks)
{
    private static readonly Regex Digest=new("^[a-f0-9]{64}\\z",RegexOptions.CultureInvariant);
    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS behavioral_contract_reviews(
            id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            contract_sha256 TEXT NOT NULL,evaluator_sha256 TEXT NOT NULL,contract_json TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Proposed','Reviewed','Revoked','Interrupted')),
            UNIQUE(task_id,contract_sha256)
        );
        CREATE TABLE IF NOT EXISTS evaluation_plan_contract_links(
            plan_review_id TEXT PRIMARY KEY REFERENCES engineering_evaluation_plan_reviews(id) ON DELETE CASCADE,
            contract_review_id TEXT NOT NULL REFERENCES behavioral_contract_reviews(id) ON DELETE CASCADE
        );
        """);
    private static string Identity(EvaluationPlanningInput input,JsonElement contract,string evaluator)
    {
        if(!Digest.IsMatch(evaluator))throw new StoreFailure("EVALUATOR_IDENTITY_CHANGED");
        var coverage=EvaluationContractCoverage.ValidateSelection(contract,input);
        var exact=JsonSerializer.Serialize(new {input.Scope.TaskId,input.Scope.PlanSha256,
            input.Scope.SpecificationSha256,input.Scope.ArchitectureSha256,evaluator,
            contractId=EvaluationContractCoverage.CalculationJournal,coverage});
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(exact)));
    }
    private static BehavioralContractReview Read(SqliteDataReader row)=>
        new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4));
    private const string Columns="SELECT id,task_id,contract_sha256,evaluator_sha256,state FROM behavioral_contract_reviews";
    private static BehavioralContractReview Find(SqliteConnection db,SqliteTransaction tx,string taskId,string id)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(id);
        using var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND id=@id",("@task",taskId),("@id",id));
        using var row=cmd.ExecuteReader();
        return row.Read()?Read(row):throw new StoreFailure("NOT_FOUND");
    }
    private static JsonElement Contract(SqliteConnection db,SqliteTransaction tx,string id)
    {
        var raw=TaskStore.Scalar(db,tx,"SELECT contract_json FROM behavioral_contract_reviews WHERE id=@id",("@id",id))
            ??throw new StoreFailure("NOT_FOUND");
        using var document=JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
    public BehavioralContractReview Propose(string taskId,string runId,JsonElement contract,string evaluatorSha256)
    {
        if(contract.GetRawText().Length>160000)throw new StoreFailure("EVALUATION_CONTRACT_NOT_CURRENT");
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var input=EvaluationPlanReviewStore.Capture(db,tx,taskId,runId);
        var hash=Identity(input,contract,evaluatorSha256);
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND contract_sha256=@hash",("@task",taskId),("@hash",hash)))
        using(var row=cmd.ExecuteReader())
            if(row.Read())
            {
                var existing=Read(row);row.Close();tx.Commit();return existing;
            }
        var id=Guid.NewGuid().ToString("N");
        TaskStore.Execute(db,tx,"""
            INSERT INTO behavioral_contract_reviews(id,task_id,contract_sha256,evaluator_sha256,contract_json,state)
            VALUES(@id,@task,@hash,@evaluator,@json,'Proposed')
            """,("@id",id),("@task",taskId),("@hash",hash),("@evaluator",evaluatorSha256),("@json",contract.GetRawText()));
        tx.Commit();return new(id,taskId,hash,evaluatorSha256,"Proposed");
    }
    public BehavioralContractReview Review(string taskId,string runId,string id,string confirmation)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State!="Proposed"||confirmation!="REVIEW BEHAVIOR "+item.ContractSha256)
            throw new StoreFailure("BEHAVIORAL_CONTRACT_CONFIRMATION_REQUIRED");
        var input=EvaluationPlanReviewStore.Capture(db,tx,taskId,runId);
        if(Identity(input,Contract(db,tx,id),item.EvaluatorSha256)!=item.ContractSha256)
            throw new StoreFailure("EVALUATION_CONTRACT_NOT_CURRENT");
        if(TaskStore.Execute(db,tx,"UPDATE behavioral_contract_reviews SET state='Reviewed' WHERE id=@id AND state='Proposed'",("@id",id))!=1)
            throw new StoreFailure("INVALID_STATE");
        tx.Commit();return item with {State="Reviewed"};
    }
    public BehavioralContractReview Revoke(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State is "Reviewed" or "Proposed")
        {
            TaskStore.Execute(db,tx,"UPDATE behavioral_contract_reviews SET state='Revoked' WHERE id=@id",("@id",id));
            item=item with {State="Revoked"};
        }
        tx.Commit();return item;
    }
    internal static JsonElement RequireReviewed(SqliteConnection db,SqliteTransaction tx,
        EvaluationPlanningInput input,string id,string? evaluatorSha256=null)
    {
        var item=Find(db,tx,input.Scope.TaskId,id);
        if(item.State!="Reviewed")throw new StoreFailure("BEHAVIORAL_CONTRACT_REVIEW_REQUIRED");
        if(evaluatorSha256 is not null&&item.EvaluatorSha256!=evaluatorSha256)
            throw new StoreFailure("EVALUATOR_IDENTITY_CHANGED");
        var contract=Contract(db,tx,id);
        if(Identity(input,contract,item.EvaluatorSha256)!=item.ContractSha256)
            throw new StoreFailure("EVALUATION_CONTRACT_NOT_CURRENT");
        return contract;
    }
    internal JsonElement ReadReviewed(string taskId,string runId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var contract=RequireReviewed(db,tx,EvaluationPlanReviewStore.Capture(db,tx,taskId,runId),id);
        tx.Commit();return contract;
    }
    internal static void RequireDescriptor(SqliteConnection db,SqliteTransaction tx,
        EvaluationPlanningInput input,string id,JsonElement descriptor)
    {
        var reviewed=RequireReviewed(db,tx,input,id,descriptor.GetProperty("evaluatorSha256").GetString());
        if(EvaluationContractCoverage.ValidateSelection(reviewed,input)!=
            EvaluationContractCoverage.Validate(descriptor.GetProperty("mappings"),input.Criteria))
            throw new StoreFailure("EVALUATION_CONTRACT_MAPPING_CHANGED");
    }
    internal void Recover()
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        TaskStore.Execute(db,tx,"UPDATE behavioral_contract_reviews SET state='Interrupted' WHERE state='Proposed'");
        tx.Commit();
    }
}
