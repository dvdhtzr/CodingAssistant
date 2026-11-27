using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// Host-internal planning input. Never return archive bytes to a model or
// accept a scope, criteria, archive, or qualification descriptor from HTTP.
public sealed record EvaluationCriterion(string Id,string Text);
public sealed record EvaluationPlanningInput(StaticWebVerificationScopePreview Scope,
    IReadOnlyList<EvaluationCriterion> Criteria,byte[] Archive,string ArchiveSha256);
public sealed record EvaluationPlanReview(string Id,string TaskId,string RunId,string SourceSha256,
    string ScopeSha256,string ArchiveSha256,string AdmissionSha256,string DescriptorSha256,
    string State,string CreatedUtc,string UpdatedUtc,bool Current,
    bool ExecutionEnabled=false,bool Verified=false);

// Non-executable review ledger. Only a trusted control-plane integration
// may supply the output of the existing Node admission binder. This store
// rechecks current database bindings and records review, not qualification,
// execution consent, a reusable grant, or permission to start a process.
public sealed class EvaluationPlanReviewStore(TaskStore tasks)
{
    private static readonly Regex Digest=new("^[a-f0-9]{64}\\z",RegexOptions.CultureInvariant);
    private static string Sha(byte[] data)=>Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    private static string Sha(string data)=>Sha(Encoding.UTF8.GetBytes(data));

    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS engineering_evaluation_plan_reviews(
            id TEXT PRIMARY KEY,task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            run_id TEXT NOT NULL,source_sha256 TEXT NOT NULL,scope_sha256 TEXT NOT NULL,
            archive_sha256 TEXT NOT NULL,admission_sha256 TEXT NOT NULL,
            descriptor_sha256 TEXT NOT NULL,descriptor_json TEXT NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Previewed','Reviewed','Cancelled','Interrupted')),
            created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL,
            UNIQUE(task_id,run_id,admission_sha256)
        );
        """);

    // Stable bytes for the same exact file set, independent of wall clock.
    // This is not a new general ZIP export route or execution workspace.
    private static byte[] Archive(IReadOnlyList<EngineeringSourceFile> files)
    {
        using var buffer=new MemoryStream();
        using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true))
            foreach(var file in files.OrderBy(f=>f.Path,StringComparer.Ordinal))
            {
                var entry=zip.CreateEntry(ProjectDraftStore.SafePath(file.Path),CompressionLevel.NoCompression);
                entry.LastWriteTime=new DateTimeOffset(2000,1,1,0,0,0,TimeSpan.Zero);
                using var stream=entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(file.Content));
            }
        return buffer.ToArray();
    }

    private static EvaluationPlanningInput Capture(SqliteConnection db,SqliteTransaction tx,string taskId,string runId)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(runId);
        var run=EngineeringRunStore.Find(db,tx,runId);
        if(run is null || run.TaskId!=taskId)throw new StoreFailure("NOT_FOUND");
        EngineeringRunStore.RequireBinding(db,tx,run);
        var plan=ImplementationPlanStore.RequireCurrentApproved(db,tx,taskId) with {Current=true};
        var latest=TaskStore.Scalar(db,tx,"""
            SELECT id FROM engineering_runs WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1
            """,("@task",taskId));
        var arch=DraftArchitecturePolicy.Capture(db,tx,taskId,run.SpecificationVersion,run.SpecificationSha256)
            ??throw new StoreFailure("ARCHITECTURE_CONFLICT");
        InterviewSpecification spec;
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT version,model,content,sha256,status,created_utc,approved_utc FROM interview_specs
            WHERE task_id=@task AND version=@version AND sha256=@sha AND status='Approved'
            """,("@task",taskId),("@version",run.SpecificationVersion),("@sha",run.SpecificationSha256)))
        using(var row=cmd.ExecuteReader())
        {
            if(!row.Read())throw new StoreFailure("SPECIFICATION_CONFLICT");
            spec=new(row.GetInt32(0),row.GetString(1),row.GetString(2),row.GetString(3),row.GetString(4),
                row.GetString(5),row.IsDBNull(6)?null:row.GetString(6));
        }
        var files=new List<EngineeringSourceFile>();
        using(var cmd=TaskStore.Command(db,tx,"""
            SELECT path,purpose,content,sha256,work_item_id,updated_utc
            FROM engineering_run_files WHERE run_id=@run ORDER BY path
            """,("@run",runId)))
        using(var rows=cmd.ExecuteReader())
            while(rows.Read())files.Add(new(rows.GetString(0),rows.GetString(1),rows.GetString(2),
                rows.GetString(3),rows.GetString(4),rows.GetString(5)));
        var scope=StaticWebVerificationScope.Preview(new EngineeringRunSnapshot(run,[],[],files),plan,spec,
            new DraftArchitectureReadiness("READY",true,arch.Sha256,[],arch.Choices),latest==runId);
        if(scope.Status!="SCOPE_CANDIDATE" || scope.ScopeSha256 is null || scope.SourceFingerprint is null)
            throw new StoreFailure("SOURCE_SCOPE_CHANGED");
        var criteria=plan.Outline.Tasks.SelectMany(w=>w.AcceptanceCriteria.Select((c,i)=>
            new EvaluationCriterion(w.Id+":"+(i+1).ToString(System.Globalization.CultureInfo.InvariantCulture),c)))
            .OrderBy(c=>c.Id,StringComparer.Ordinal).ToArray();
        if(criteria.Length is <1 or >64 || criteria.Select(c=>c.Id).Distinct(StringComparer.Ordinal).Count()!=criteria.Length)
            throw new StoreFailure("EVALUATION_CRITERIA_INVALID");
        var archive=Archive(files);
        return new(scope,criteria,archive,Sha(archive));
    }

    public EvaluationPlanningInput Capture(string taskId,string runId)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var input=Capture(db,tx,taskId,runId);tx.Commit();return input;
    }

    // Only called with a host-produced binder preview. No public route
    // accepts this JSON. Reconstruct the current task binding in the same
    // transaction before recording or acknowledging anything.
    private static string ValidateDescriptor(JsonElement d,EvaluationPlanningInput input)
    {
        try
        {
            string Str(string key)=>d.GetProperty(key).GetString()??throw new JsonException();
            bool Off(string key)=>d.GetProperty(key).ValueKind==JsonValueKind.False;
            var scope=input.Scope;
            var raw=d.GetRawText();
            if(raw.Length>90000 || d.GetProperty("schemaVersion").GetInt32()!=1 ||
                Str("status")!="ADMISSION_PREVIEW_ONLY" || !Off("executionEnabled") ||
                !Off("runtimeQualified") || !Off("generatedArtifactVerified") ||
                Str("taskId")!=scope.TaskId || Str("runId")!=scope.EngineeringRunId ||
                Str("planSha256")!=scope.PlanSha256 || Str("specificationSha256")!=scope.SpecificationSha256 ||
                Str("architectureSha256")!=scope.ArchitectureSha256 ||
                Str("sourceFingerprint")!=scope.SourceFingerprint || Str("scopeSha256")!=scope.ScopeSha256 ||
                Str("archiveSha256")!=input.ArchiveSha256 || Str("model")!=scope.Model ||
                Str("contractId")!=EvaluationContractCoverage.CalculationJournal ||
                !Digest.IsMatch(Str("admissionSha256")) || !Digest.IsMatch(Str("candidateSha256")) ||
                !Digest.IsMatch(Str("evaluatorSha256")))
                throw new StoreFailure("EVALUATION_DESCRIPTOR_NOT_CURRENT");
            var qualification=d.GetProperty("qualification");
            if(qualification.GetProperty("hostProvenanceEstablished").ValueKind!=JsonValueKind.False ||
                qualification.GetProperty("runtimeQualified").ValueKind!=JsonValueKind.False ||
                qualification.GetProperty("executionEnabled").ValueKind!=JsonValueKind.False ||
                !Digest.IsMatch(qualification.GetProperty("receiptSha256").GetString()??"") ||
                qualification.GetProperty("evaluatorSha256").GetString()!=Str("evaluatorSha256"))
                throw new StoreFailure("EVALUATION_DESCRIPTOR_NOT_CURRENT");
            _=EvaluationContractCoverage.Validate(d.GetProperty("mappings"),input.Criteria);
            return Str("admissionSha256");
        }
        catch(Exception e) when(e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {throw new StoreFailure("EVALUATION_DESCRIPTOR_INVALID");}
    }

    private const string Columns="""
        SELECT id,task_id,run_id,source_sha256,scope_sha256,archive_sha256,admission_sha256,
            descriptor_sha256,state,created_utc,updated_utc FROM engineering_evaluation_plan_reviews
        """;
    private static EvaluationPlanReview Decode(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.GetString(2),
        r.GetString(3),r.GetString(4),r.GetString(5),r.GetString(6),r.GetString(7),r.GetString(8),r.GetString(9),r.GetString(10),false);
    private static EvaluationPlanReview Find(SqliteConnection db,SqliteTransaction tx,string taskId,string id)
    {
        using var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND id=@id",("@task",taskId),("@id",id));
        using var row=cmd.ExecuteReader();return row.Read()?Decode(row):throw new StoreFailure("NOT_FOUND");
    }

    private static bool Current(SqliteConnection db,SqliteTransaction tx,EvaluationPlanReview item)
    {
        try
        {
            var input=Capture(db,tx,item.TaskId,item.RunId);
            return input.Scope.SourceFingerprint==item.SourceSha256 && input.Scope.ScopeSha256==item.ScopeSha256 &&
                input.ArchiveSha256==item.ArchiveSha256;
        }
        catch(StoreFailure){return false;}
    }

    public EvaluationPlanReview Stage(string taskId,string runId,JsonElement hostPreview)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var input=Capture(db,tx,taskId,runId);
        var admission=ValidateDescriptor(hostPreview,input);
        var raw=hostPreview.GetRawText();var descriptorHash=Sha(raw);
        using(var cmd=TaskStore.Command(db,tx,Columns+" WHERE task_id=@task AND run_id=@run AND admission_sha256=@sha",
            ("@task",taskId),("@run",runId),("@sha",admission)))
        using(var row=cmd.ExecuteReader())
            if(row.Read())
            {
                var existing=Decode(row);row.Close();
                if(existing.DescriptorSha256!=descriptorHash)throw new StoreFailure("IDEMPOTENCY_CONFLICT");
                tx.Commit();return existing with {Current=true};
            }
        var now=DateTimeOffset.UtcNow.ToString("O");
        var item=new EvaluationPlanReview(Guid.NewGuid().ToString("N"),taskId,runId,input.Scope.SourceFingerprint!,
            input.Scope.ScopeSha256!,input.ArchiveSha256,admission,descriptorHash,"Previewed",now,now,true);
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_evaluation_plan_reviews(id,task_id,run_id,source_sha256,scope_sha256,archive_sha256,
                admission_sha256,descriptor_sha256,descriptor_json,state,created_utc,updated_utc)
            VALUES(@id,@task,@run,@source,@scope,@archive,@admission,@descriptor,@json,'Previewed',@now,@now)
            """,("@id",item.Id),("@task",taskId),("@run",runId),("@source",item.SourceSha256),
            ("@scope",item.ScopeSha256),("@archive",item.ArchiveSha256),("@admission",admission),
            ("@descriptor",descriptorHash),("@json",raw),("@now",now));
        tx.Commit();return item;
    }

    public EvaluationPlanReview Review(string taskId,string id,string admissionSha256,string confirmation)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(id);
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State!="Previewed" || admissionSha256!=item.AdmissionSha256 ||
            confirmation!="REVIEW EVALUATION "+item.AdmissionSha256)
            throw new StoreFailure("EVALUATION_PLAN_CONFIRMATION_REQUIRED");
        var raw=TaskStore.Scalar(db,tx,"SELECT descriptor_json FROM engineering_evaluation_plan_reviews WHERE id=@id",("@id",id))
            ??throw new StoreFailure("NOT_FOUND");
        if(Sha(raw)!=item.DescriptorSha256 || !Current(db,tx,item))throw new StoreFailure("SOURCE_SCOPE_CHANGED");
        using var descriptor=JsonDocument.Parse(raw);
        if(ValidateDescriptor(descriptor.RootElement,Capture(db,tx,taskId,item.RunId))!=admissionSha256)
            throw new StoreFailure("EVALUATION_DESCRIPTOR_NOT_CURRENT");
        var now=DateTimeOffset.UtcNow.ToString("O");
        if(TaskStore.Execute(db,tx,"""
            UPDATE engineering_evaluation_plan_reviews SET state='Reviewed',updated_utc=@now
            WHERE id=@id AND state='Previewed'
            """,("@id",id),("@now",now))!=1)throw new StoreFailure("INVALID_STATE");
        tx.Commit();return item with {State="Reviewed",UpdatedUtc=now,Current=true};
    }

    public EvaluationPlanReview Cancel(string taskId,string id)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(id);
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        var item=Find(db,tx,taskId,id);
        if(item.State is "Previewed" or "Reviewed")
        {
            var now=DateTimeOffset.UtcNow.ToString("O");
            TaskStore.Execute(db,tx,"UPDATE engineering_evaluation_plan_reviews SET state='Cancelled',updated_utc=@now WHERE id=@id",
                ("@id",id),("@now",now));
            item=item with {State="Cancelled",UpdatedUtc=now};
        }
        var current=Current(db,tx,item);tx.Commit();return item with {Current=current};
    }

    public EvaluationPlanReview Get(string taskId,string id)
    {
        ExecutionStore.CheckId(taskId);ExecutionStore.CheckId(id);
        using var db=tasks.Open();using var tx=db.BeginTransaction(deferred:true);
        var item=Find(db,tx,taskId,id);var current=Current(db,tx,item);
        tx.Commit();return item with {Current=current};
    }

    // Incomplete owner review never resumes silently after restart.
    // Completed reviews remain historical planning records only.
    public void Recover()
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        TaskStore.Execute(db,tx,"""
            UPDATE engineering_evaluation_plan_reviews SET state='Interrupted',updated_utc=@now WHERE state='Previewed'
            """,("@now",DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
    }
}
