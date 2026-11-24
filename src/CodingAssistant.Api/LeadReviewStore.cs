using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record LeadReviewRun(string Id,string TaskId,string EngineeringRunId,
    string SourceSha256,string PlanSha256,string Model,string State,string? ErrorCode,
    string CreatedUtc,string UpdatedUtc);
public sealed record LeadFileReport(string Path,string SourceSha256,LeadFileAssessment Assessment);
public sealed record LeadReviewSnapshot(LeadReviewRun Job,IReadOnlyList<LeadFileReport> Reports,
    bool Current,string Disposition);

/// <summary>Independent static review ledger. No source-writing methods.
/// Every accepted report is verified against the exact saved file and plan.</summary>
public sealed class LeadReviewStore(TaskStore tasks)
{
    private static readonly Regex Id = new("^[0-9a-f]{32}\\z",RegexOptions.CultureInvariant);
    internal static void Initialize(SqliteConnection db) => TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS lead_reviews(
            id TEXT PRIMARY KEY NOT NULL,task_id TEXT NOT NULL REFERENCES tasks(id),
            engineering_run_id TEXT NOT NULL REFERENCES engineering_runs(id),
            source_sha256 TEXT NOT NULL,plan_sha256 TEXT NOT NULL,model TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Running','Reviewed','Failed','Interrupted')),
            error_code TEXT,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS lead_review_active ON lead_reviews(task_id) WHERE state='Running';
        CREATE TABLE IF NOT EXISTS lead_review_files(
            review_id TEXT NOT NULL REFERENCES lead_reviews(id),
            path TEXT NOT NULL COLLATE NOCASE,source_sha256 TEXT NOT NULL,assessment_json TEXT NOT NULL,
            PRIMARY KEY(review_id,path)
        );
        """);
    public void MarkInterruptedOnStartup()
    {
        using var db=tasks.Open();
        TaskStore.Execute(db,null,"UPDATE lead_reviews SET state='Interrupted',error_code='PROCESS_INTERRUPTED',updated_utc=@now WHERE state='Running'",
            ("@now",DateTimeOffset.UtcNow.ToString("O")));
    }
    private static void CheckId(string id)
    {
        if(id is null || !Id.IsMatch(id)) throw new StoreFailure("INVALID_ID");
    }
    private static LeadReviewRun Find(SqliteConnection db,SqliteTransaction? tx,string taskId,string reviewId)
    {
        using var cmd=TaskStore.Command(db,tx,"""
            SELECT id,task_id,engineering_run_id,source_sha256,plan_sha256,model,state,error_code,created_utc,updated_utc
            FROM lead_reviews WHERE id=@id AND task_id=@task
            """,("@id",reviewId),("@task",taskId));
        using var reader=cmd.ExecuteReader();
        if(!reader.Read()) throw new StoreFailure("NOT_FOUND");
        return new(reader.GetString(0),reader.GetString(1),reader.GetString(2),reader.GetString(3),
            reader.GetString(4),reader.GetString(5),reader.GetString(6),
            reader.IsDBNull(7)?null:reader.GetString(7),reader.GetString(8),reader.GetString(9));
    }
    private static void RequireSource(SqliteConnection db,SqliteTransaction tx,LeadReviewRun review)
    {
        var run=EngineeringRunStore.Find(db,tx,review.EngineeringRunId);
        if(run is null || run.TaskId!=review.TaskId) throw new StoreFailure("NOT_FOUND");
        if(run.State!="Generated") throw new StoreFailure("BUILD_NOT_COMPLETE");
        if(run.PlanSha256!=review.PlanSha256) throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
        if(TaskStore.Scalar(db,tx,"SELECT id FROM engineering_runs WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",
            ("@task",review.TaskId))!=run.Id ||
            EngineeringMessageStore.SourceFingerprint(db,tx,run.Id)!=review.SourceSha256)
            throw new StoreFailure("SOURCE_CHANGED");
        EngineeringRunStore.RequireBinding(db,tx,run);
    }
    private static void Activity(SqliteConnection db,SqliteTransaction tx,LeadReviewRun job,string kind)
        => EngineeringActivityStore.Record(db,tx,job.TaskId,kind,"LeadSoftwareEngineer","LeadReview",
            job.Id,sourceSha256:job.SourceSha256,model:job.Model);
    public LeadReviewRun Start(string taskId,EngineeringRunSnapshot source,ImplementationPlan plan,string model,CollaborationAdmission? admission=null)
    {
        CheckId(taskId);
        EngineeringRoleCapabilities.Require("LeadSoftwareEngineer",EngineeringCapability.ReviewCode);
        if(source.Job.TaskId!=taskId || source.Job.State!="Generated" || source.Files.Count==0)
            throw new StoreFailure("BUILD_NOT_COMPLETE");
        if(!plan.Current || plan.Status!="Approved" || plan.TaskId!=taskId ||
            plan.PlanSha256!=source.Job.PlanSha256 || !LocalInterviewModel.AllowedModel(model))
            throw new StoreFailure("PLAN_NOT_APPROVED");
        var now=DateTimeOffset.UtcNow.ToString("O");
        var job=new LeadReviewRun(Guid.NewGuid().ToString("N"),taskId,source.Job.Id,
            QaReviewStore.Fingerprint(source.Files),plan.PlanSha256,model,"Running",null,now,now);
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        RequireSource(db,tx,job);
        CollaborationStore.AdmitReview(db,tx,taskId,job.EngineeringRunId,job.SourceSha256,job.Id,"LeadSoftwareEngineer",admission);
        try {
            TaskStore.Execute(db,tx,"""
                INSERT INTO lead_reviews(id,task_id,engineering_run_id,source_sha256,plan_sha256,model,state,created_utc,updated_utc)
                VALUES(@id,@task,@run,@sha,@plan,@model,'Running',@now,@now)
                """,("@id",job.Id),("@task",taskId),("@run",job.EngineeringRunId),("@sha",job.SourceSha256),
                ("@plan",job.PlanSha256),("@model",model),("@now",now));
        } catch(SqliteException e) when(e.SqliteErrorCode==19) {throw new StoreFailure("BUILD_BUSY");}
        Activity(db,tx,job,"LeadReviewAdmitted");tx.Commit();tasks.SignalEngineeringActivity(taskId);return job;
    }
    public void Save(string taskId,string reviewId,EngineeringSourceFile file,LeadFileAssessment report)
    {
        CheckId(taskId);CheckId(reviewId);
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var job=Find(db,tx,taskId,reviewId);
        if(job.State!="Running") throw new StoreFailure("INVALID_STATE");
        RequireSource(db,tx,job);
        CollaborationStore.RequireReviewAuthority(db,tx,taskId,job.Id,"LeadSoftwareEngineer");
        // Re-read persisted source, not the caller's evidence text.
        var content=TaskStore.Scalar(db,tx,"SELECT content FROM engineering_run_files WHERE run_id=@run AND path=@path AND sha256=@sha",
            ("@run",job.EngineeringRunId),("@path",file.Path),("@sha",file.Sha256));
        if(content is null || content!=file.Content) throw new StoreFailure("SOURCE_CHANGED");
        var valid=MafLeadSoftwareEngineer.Parse(JsonSerializer.Serialize(report),file);
        TaskStore.Execute(db,tx,"INSERT INTO lead_review_files VALUES(@id,@path,@sha,@report)",
            ("@id",reviewId),("@path",file.Path),("@sha",file.Sha256),("@report",JsonSerializer.Serialize(valid)));
        Activity(db,tx,job,"LeadFileReviewed");tx.Commit();tasks.SignalEngineeringActivity(taskId);
    }
    internal void RecordInspection(string taskId,string reviewId,AtlasInspectionObservation observation)
    {
        if(observation.Name is not ("atlas.find_symbols" or "atlas.get_relationships" or "atlas.source_excerpt") ||
            observation.ResultCount is <0 or >16) throw new StoreFailure("INVALID_INPUT");
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var job=Find(db,tx,taskId,reviewId);
        if(job.State!="Running") throw new StoreFailure("INVALID_STATE");
        RequireSource(db,tx,job);
        CollaborationStore.RequireReviewAuthority(db,tx,taskId,job.Id,"LeadSoftwareEngineer");
        Activity(db,tx,job,"LeadAtlasInspected");tx.Commit();tasks.SignalEngineeringActivity(taskId);
    }
    public void Finish(string taskId,string reviewId,string state,string? error=null)
    {
        if(state is not ("Reviewed" or "Failed" or "Interrupted")) throw new StoreFailure("INVALID_STATE");
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var job=Find(db,tx,taskId,reviewId);
        if(job.State!="Running") return;
        if(state=="Reviewed")
        {
            RequireSource(db,tx,job);
            CollaborationStore.RequireReviewAuthority(db,tx,taskId,job.Id,"LeadSoftwareEngineer");
            var count=TaskStore.Scalar(db,tx,"SELECT COUNT(*) FROM lead_review_files WHERE review_id=@id",("@id",reviewId));
            var expected=TaskStore.Scalar(db,tx,"SELECT COUNT(*) FROM engineering_run_files WHERE run_id=@id",("@id",job.EngineeringRunId));
            if(count!=expected || count=="0") throw new StoreFailure("BUILD_NOT_COMPLETE");
        }
        TaskStore.Execute(db,tx,"UPDATE lead_reviews SET state=@state,error_code=@error,updated_utc=@now WHERE id=@id",
            ("@state",state),("@error",(object?)error??DBNull.Value),("@now",DateTimeOffset.UtcNow.ToString("O")),("@id",reviewId));
        Activity(db,tx,job,state=="Reviewed"?"LeadReviewCompleted":state=="Interrupted"?"LeadReviewInterrupted":"LeadReviewFailed");
        tx.Commit();tasks.SignalEngineeringActivity(taskId);
    }
    public LeadReviewSnapshot? Latest(string taskId)
    {
        CheckId(taskId);_=tasks.GetTask(taskId);
        using var db=tasks.Open();
        var id=TaskStore.Scalar(db,null,"SELECT id FROM lead_reviews WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1",("@task",taskId));
        return id is null?null:Get(taskId,id);
    }
    public LeadReviewSnapshot Get(string taskId,string reviewId)
    {
        CheckId(taskId);CheckId(reviewId);
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var job=Find(db,tx,taskId,reviewId);
        var reports=new List<LeadFileReport>();
        using(var cmd=TaskStore.Command(db,tx,"SELECT path,source_sha256,assessment_json FROM lead_review_files WHERE review_id=@id ORDER BY path",("@id",reviewId)))
        using(var reader=cmd.ExecuteReader())
            while(reader.Read()) reports.Add(new(reader.GetString(0),reader.GetString(1),
                JsonSerializer.Deserialize<LeadFileAssessment>(reader.GetString(2))??throw new InvalidOperationException("Invalid saved review")));
        var current=true;
        try {RequireSource(db,tx,job);} catch(StoreFailure) {current=false;}
        var disposition=!current?"Stale":job.State!="Reviewed"?"Unverified":
            reports.Any(x=>x.Assessment.Disposition=="ChangesRequested")?"ChangesRequested":
            reports.Any(x=>x.Assessment.Disposition=="InsufficientEvidence")?"InsufficientEvidence":
            reports.Any(x=>x.Assessment.Disposition=="ClarificationRequested")?"ClarificationRequested":
            reports.Any(x=>x.Assessment.Disposition=="ApprovedWithRecommendations")?"ApprovedWithRecommendations":"Approved";
        tx.Commit();return new(job,reports,current,disposition);
    }
}
