using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// The proposal is intentionally NOT a grant. Until a separately qualified
// host adapter exists, no transition can authorize or consume execution.
public sealed record EvaluationExecutionProposal(string Id,string TaskId,string PlanReviewId,
    string AuthorizationSha256,string ProfileSha256,string State,bool Current,
    bool ExecutionEnabled=false,bool RuntimeQualified=false,string? PilotBindingSha256=null);

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
        CREATE TABLE IF NOT EXISTS evaluation_execution_pilot_bindings(
            proposal_id TEXT PRIMARY KEY REFERENCES evaluation_execution_proposals(id) ON DELETE CASCADE,
            binding_sha256 TEXT NOT NULL,binding_json TEXT NOT NULL
        );
        """);
    private static string Sha(string value)=>Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static (string Authorization,string Profile) Identity(SqliteConnection db,SqliteTransaction tx,
        string taskId,string planId,string? pilotBindingSha256=null)
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
        if(pilotBindingSha256 is not null)
            authorization=Sha(JsonSerializer.Serialize(new {
                schemaVersion=2,kind="pilot-evidence-bound-proposal-only",candidateAuthorizationSha256=authorization,
                pilotBindingSha256
            }));
        return (authorization,profileHash);
    }
    private const string Columns="""
        SELECT p.id,p.task_id,p.plan_review_id,p.authorization_sha256,p.profile_sha256,p.state,b.binding_sha256
        FROM evaluation_execution_proposals p
        LEFT JOIN evaluation_execution_pilot_bindings b ON b.proposal_id=p.id
        """;
    private static EvaluationExecutionProposal Decode(SqliteDataReader row)=>
        new(row.GetString(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),row.GetString(5),false,
            PilotBindingSha256:row.IsDBNull(6)?null:row.GetString(6));
    private static EvaluationExecutionProposal Find(SqliteConnection db,SqliteTransaction tx,string taskId,string id)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(id);
        using var cmd=TaskStore.Command(db,tx,Columns+" WHERE p.task_id=@task AND p.id=@id",
            ("@task",taskId),("@id",id));
        using var row=cmd.ExecuteReader();
        return row.Read()?Decode(row):throw new StoreFailure("NOT_FOUND");
    }
    private static bool Current(SqliteConnection db,SqliteTransaction tx,EvaluationExecutionProposal item)
    {
        if(item.State!="Proposed")return false;
        try
        {
            if(item.PilotBindingSha256 is not null)
            {
                var raw=TaskStore.Scalar(db,tx,"SELECT binding_json FROM evaluation_execution_pilot_bindings WHERE proposal_id=@id",("@id",item.Id));
                if(raw is null||Sha(raw)!=item.PilotBindingSha256)return false;
                using var json=JsonDocument.Parse(raw);
                var (_,descriptor,_)=EvaluationPlanReviewStore.RequireReviewedAdmission(db,tx,item.TaskId,item.PlanReviewId);
                PilotProposalBinding.ValidateAgainstAdmission(json.RootElement,descriptor);
            }
            var identity=Identity(db,tx,item.TaskId,item.PlanReviewId,item.PilotBindingSha256);
            return item.AuthorizationSha256==identity.Authorization&&item.ProfileSha256==identity.Profile;
        }
        catch(Exception e)when(e is StoreFailure or JsonException or InvalidOperationException or KeyNotFoundException){return false;}
    }
    public EvaluationExecutionProposal Propose(string taskId,string planReviewId)=>ProposeCore(taskId,planReviewId,null);

    // Host-only path: load owner-private configuration and obtain a fresh
    // read-only inspection. No arbitrary evidence JSON from a request/model.
    internal async Task<EvaluationExecutionProposal> ProposeBoundAsync(string taskId,string planReviewId,
        string ownerDataDirectory,IReadOnlyList<string> receipts,CancellationToken token)
    {
        var binding=await PilotProposalBinding.InspectAsync(ownerDataDirectory,receipts,token);
        token.ThrowIfCancellationRequested();
        return ProposeCore(taskId,planReviewId,binding);
    }
    internal EvaluationExecutionProposal ProposeBound(string taskId,string planReviewId,JsonElement binding)=>
        ProposeCore(taskId,planReviewId,binding);
    private EvaluationExecutionProposal ProposeCore(string taskId,string planReviewId,JsonElement? pilotBinding)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        string? raw=null,hash=null;
        if(pilotBinding is JsonElement evidence)
        {
            var (_,descriptor,_)=EvaluationPlanReviewStore.RequireReviewedAdmission(db,tx,taskId,planReviewId);
            PilotProposalBinding.ValidateAgainstAdmission(evidence,descriptor);
            raw=evidence.GetRawText();hash=Sha(raw);
        }
        var identity=Identity(db,tx,taskId,planReviewId,hash);
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE p.task_id=@task AND p.authorization_sha256=@hash",
            ("@task",taskId),("@hash",identity.Authorization)))
        using(var row=cmd.ExecuteReader())
            if(row.Read())
            {
                var existing=Decode(row);row.Close();
                if(existing.PilotBindingSha256!=hash)throw new StoreFailure("IDEMPOTENCY_CONFLICT");
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
        if(hash is not null)
            TaskStore.Execute(db,tx,"INSERT INTO evaluation_execution_pilot_bindings(proposal_id,binding_sha256,binding_json) VALUES(@id,@hash,@json)",
                ("@id",id),("@hash",hash),("@json",raw!));
        tx.Commit();return new(id,taskId,planReviewId,identity.Authorization,identity.Profile,"Proposed",true,PilotBindingSha256:hash);
    }
    // Explicit fresh check: Current on Get is database currentness only.
    // Reinspect outside the DB lock, then recheck the exact proposal and
    // source/contract in a transaction. Still not executable consent.
    internal async Task<EvaluationExecutionProposal> RevalidateBoundAsync(string taskId,string id,
        string ownerDataDirectory,IReadOnlyList<string> receipts,CancellationToken token)
    {
        var binding=await PilotProposalBinding.InspectAsync(ownerDataDirectory,receipts,token);
        token.ThrowIfCancellationRequested();
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var item=Find(db,tx,taskId,id);
        if(item.PilotBindingSha256 is null||item.PilotBindingSha256!=Sha(binding.GetRawText())||!Current(db,tx,item))
            throw new StoreFailure("PILOT_EXECUTION_BINDING_CHANGED");
        tx.Commit();return item with {Current=true};
    }
    // Reconstruct the exact candidate and linked historical evidence in
    // one read transaction. No caller supplies archive, scope or contract.
    internal (EvaluationExecutionProposal Proposal,EvaluationPlanningInput Input,JsonElement Descriptor,
        JsonElement Contract,JsonElement Binding) CaptureBoundTransport(string taskId,string id)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var item=Find(db,tx,taskId,id);
        if(item.PilotBindingSha256 is null||!Current(db,tx,item))
            throw new StoreFailure("PILOT_EXECUTION_BINDING_CHANGED");
        var (plan,descriptor,contractId)=EvaluationPlanReviewStore.RequireReviewedAdmission(db,tx,taskId,item.PlanReviewId);
        var input=EvaluationPlanReviewStore.Capture(db,tx,taskId,plan.RunId);
        var contract=BehavioralContractReviewStore.RequireReviewed(db,tx,input,contractId,descriptor.GetProperty("evaluatorSha256").GetString());
        var raw=TaskStore.Scalar(db,tx,"SELECT binding_json FROM evaluation_execution_pilot_bindings WHERE proposal_id=@id",("@id",item.Id))
            ??throw new StoreFailure("PILOT_EXECUTION_BINDING_CHANGED");
        if(Sha(raw)!=item.PilotBindingSha256)throw new StoreFailure("PILOT_EXECUTION_BINDING_CHANGED");
        using var binding=JsonDocument.Parse(raw);
        tx.Commit();return (item with {Current=true},input,descriptor,contract,binding.RootElement.Clone());
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
