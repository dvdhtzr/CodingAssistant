using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record DeliveryWork(string Id,string Title,string State,IReadOnlyList<string> AcceptanceCriteria);
public sealed record DeliveryReview(string Role,string? Id,string State,string? Model,int FilesReviewed,bool Complete);
public sealed record DeliveryRepair(string Id,string BaseRunId,string RunId,int Attempt,string State,string? ErrorCode);
public sealed record DeliveryModelUse(string AgentId,string Model,string State,int RecordedRequests);
public sealed record DeliverySmoke(string Id,string State,string? Status);
public sealed record DeliveryEvidence(string TaskId,string? RunId,string? SourceSha256,
    string? PlanSha256,string? SpecificationSha256,string? ArchitectureSha256,
    bool ImplementationComplete,bool EngineeringComplete,bool ProductAccepted,bool DeliveryAllowed,
    IReadOnlyList<string> Blockers,IReadOnlyList<DeliveryWork> Work,
    IReadOnlyList<DeliveryReview> Reviews,IReadOnlyList<EngineeringIssue> Findings,
    IReadOnlyList<DeliveryRepair> Repairs,IReadOnlyList<DeliverySmoke> Smoke,
    IReadOnlyList<DeliveryModelUse> ModelUse,IReadOnlyList<DraftArchitectureChoice> Decisions,
    IReadOnlyList<string> RequiredVerification,IReadOnlyList<string> PlannedRisks);

/// <summary>Revision-bound, owner-private delivery readiness and retrospective evidence.
/// Deliberately fail closed: no currently supported runtime profile supplies qualified
/// independent generated-project verification. Static or smoke evidence cannot accept
/// a product. This projection grants neither source writes nor execution nor export.
/// </summary>
public sealed class DeliveryEvidenceStore(TaskStore tasks)
{
    public DeliveryEvidence Read(string taskId)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        if(TaskStore.FindTask(db,tx,taskId) is null)throw new StoreFailure("NOT_FOUND");
        var blockers=new List<string>();
        var work=new List<DeliveryWork>();
        var reviews=new List<DeliveryReview>();
        var findings=new List<EngineeringIssue>();
        var repairs=new List<DeliveryRepair>();
        var smoke=new List<DeliverySmoke>();
        var models=new List<DeliveryModelUse>();
        IReadOnlyList<DraftArchitectureChoice> decisions=[];
        IReadOnlyList<string> required=[];IReadOnlyList<string> risks=[];
        var runId=TaskStore.Scalar(db,tx,
            "SELECT id FROM engineering_runs WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",("@task",taskId));
        var run=runId is null?null:EngineeringRunStore.Find(db,tx,runId);
        ImplementationPlan? plan=null;
        try{plan=ImplementationPlanStore.RequireCurrentApproved(db,tx,taskId);}
        catch(StoreFailure e){blockers.Add(e.Code);}
        if(plan is not null)
        {
            decisions=DraftArchitecturePolicy.Capture(db,tx,taskId,plan.SpecificationVersion,plan.SpecificationSha256)!.Choices;
            required=plan.Outline.TestStrategy;
            risks=plan.Outline.Risks;
        }
        string? source=null;
        var implementation=false;
        if(run is null)blockers.Add("NO_SOURCE_RUN");
        else
        {
            source=EngineeringMessageStore.SourceFingerprint(db,tx,run.Id);
            var bindingCurrent=false;
            try{EngineeringRunStore.RequireBinding(db,tx,run);bindingCurrent=true;}
            catch(StoreFailure e){blockers.Add(e.Code);}
            var lineage=new List<EngineeringRun>();
            var seen=new HashSet<string>(StringComparer.Ordinal);
            for(var ancestor=run;ancestor is not null && seen.Add(ancestor.Id);)
            {
                if(ancestor.TaskId!=taskId || ancestor.PlanSha256!=run.PlanSha256)break;
                lineage.Add(ancestor);
                string? baseId=null;
                using(var cmd=TaskStore.Command(db,tx,"""
                    SELECT id,base_run_id,attempt,state,error_code FROM repair_cycles
                    WHERE task_id=@task AND engineering_run_id=@run
                    """,("@task",taskId),("@run",ancestor.Id)))
                using(var reader=cmd.ExecuteReader())
                    if(reader.Read())
                    {
                        baseId=reader.GetString(1);
                        repairs.Add(new(reader.GetString(0),baseId,ancestor.Id,reader.GetInt32(2),
                            reader.GetString(3),reader.IsDBNull(4)?null:reader.GetString(4)));
                    }
                if(baseId is null)break;
                ancestor=EngineeringRunStore.Find(db,tx,baseId);
            }
            if(plan is not null && lineage.Count>0)
            {
                var states=AdaptiveWorkPlanStore.States(db,tx,lineage[^1].Id);
                work.AddRange(plan.Outline.Tasks.Select(w=>new DeliveryWork(w.Id,w.Title,
                    states.GetValueOrDefault(w.Id,"Pending"),w.AcceptanceCriteria)));
                var fileCount=Count(db,tx,"SELECT COUNT(*) FROM engineering_run_files WHERE run_id=@run",("@run",run.Id));
                implementation=bindingCurrent && run.PlanSha256==plan.PlanSha256 && fileCount>0 &&
                    lineage.All(x=>x.State=="Generated") && work.Count>0 && work.All(x=>x.State=="Completed");
            }
            if(!implementation)blockers.Add("IMPLEMENTATION_INCOMPLETE");
            reviews.Add(Review(db,tx,run,source,"LeadSoftwareEngineer"));
            reviews.Add(Review(db,tx,run,source,"QaEngineer"));
            if(reviews.Any(x=>!x.Complete))blockers.Add("INDEPENDENT_REVIEWS_REQUIRED");
            foreach(var ancestor in lineage)
                findings.AddRange(EngineeringIssueStore.Read(db,tx,taskId,ancestor.Id).Issues);
            // Historical findings are not erased by a new source or omitted in a new review.
            if(findings.Any(i=>i.State is not ("StaticResolutionRecorded" or "WithdrawnByReviewer")))
                blockers.Add("UNRESOLVED_FINDINGS");
            using(var cmd=TaskStore.Command(db,tx,"""
                SELECT id,state,report_json FROM engineering_source_smoke
                WHERE task_id=@task AND run_id=@run AND source_sha256=@source AND plan_sha256=@plan
                ORDER BY started_utc,id
                """,("@task",taskId),("@run",run.Id),("@source",source),("@plan",run.PlanSha256)))
            using(var reader=cmd.ExecuteReader())while(reader.Read())
                smoke.Add(new(reader.GetString(0),reader.GetString(1),
                    reader.IsDBNull(2)?null:JsonSerializer.Deserialize<LocalSourceSmokeResult>(reader.GetString(2))?.Status));
        }
        if(plan is not null && run is null)
            work.AddRange(plan.Outline.Tasks.Select(w=>new DeliveryWork(w.Id,w.Title,"Pending",w.AcceptanceCriteria)));
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT agent_id,model,state,COUNT(*) FROM engineering_inference_leases
            WHERE task_id=@task GROUP BY agent_id,model,state ORDER BY agent_id,model,state
            """,("@task",taskId)))
        using(var reader=cmd.ExecuteReader())while(reader.Read())
            models.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetInt32(3)));
        // Registry entries are planning-only. No smoke report, static reviewer,
        // user confirmation, or model assertion can satisfy this mandatory gate.
        blockers.Add("QUALIFIED_VERIFICATION_REQUIRED");
        blockers.Add("PRODUCT_ACCEPTANCE_REQUIRED");
        tx.Commit();
        return new(taskId,runId,source,run?.PlanSha256??plan?.PlanSha256,
            run?.SpecificationSha256??plan?.SpecificationSha256,run?.ArchitectureSha256??plan?.ArchitectureSha256,
            implementation,false,false,false,blockers.Distinct().ToArray(),work,reviews,findings,repairs,
            smoke,models,decisions,required,risks);
    }

    private static int Count(SqliteConnection db,SqliteTransaction tx,string sql,params (string Key,object Value)[] args)
        =>int.Parse(TaskStore.Scalar(db,tx,sql,args)??"0",System.Globalization.CultureInfo.InvariantCulture);

    private static DeliveryReview Review(SqliteConnection db,SqliteTransaction tx,EngineeringRun run,string source,string role)
    {
        // These identifiers come from a fixed host branch, never request data.
        var lead=role=="LeadSoftwareEngineer";
        var table=lead?"lead_reviews":"qa_reviews";
        var files=lead?"lead_review_files":"qa_review_files";
        var fk=lead?"review_id":"qa_id";
        string? id=null,model=null;var state="Missing";var matches=false;
        using(var cmd=TaskStore.Command(db,tx,$"""
            SELECT id,state,model,source_sha256,plan_sha256 FROM {table}
            WHERE task_id=@task AND engineering_run_id=@run ORDER BY created_utc DESC,id DESC LIMIT 1
            """,("@task",run.TaskId),("@run",run.Id)))
        using(var reader=cmd.ExecuteReader())
            if(reader.Read())
            {
                id=reader.GetString(0);state=reader.GetString(1);model=reader.GetString(2);
                matches=reader.GetString(3)==source && reader.GetString(4)==run.PlanSha256;
            }
        if(id is null)return new(role,null,state,null,0,false);
        var count=Count(db,tx,$"""
            SELECT COUNT(*) FROM {files} f JOIN engineering_run_files s
              ON s.run_id=@run AND s.path=f.path AND s.sha256=f.source_sha256
            WHERE f.{fk}=@id
            """,("@run",run.Id),("@id",id));
        var total=Count(db,tx,"SELECT COUNT(*) FROM engineering_run_files WHERE run_id=@run",("@run",run.Id));
        return new(role,id,state,model,count,matches && state=="Reviewed" && total>0 && count==total);
    }
}
