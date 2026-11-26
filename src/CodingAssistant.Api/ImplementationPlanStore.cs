using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

/// <summary>
/// Owner-private immutable Technical Lead plans. The host owns all approval
/// gates and transactionally binds a plan to the exact approved specification
/// and both current approved architecture decisions. No execution privileges.
/// </summary>
public sealed class ImplementationPlanStore(TaskStore tasks, InterviewStore interviews)
{
    public const int SchemaVersion = 1;
    private static readonly Regex Id = new("^[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha = new("^[0-9a-f]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS implementation_plans (
                task_id TEXT NOT NULL REFERENCES tasks(id),
                revision INTEGER NOT NULL CHECK(revision > 0),
                schema_version INTEGER NOT NULL CHECK(schema_version = 1),
                specification_version INTEGER NOT NULL CHECK(specification_version > 0),
                specification_sha256 TEXT NOT NULL,
                architecture_sha256 TEXT NOT NULL,
                model TEXT NOT NULL,
                outline_json TEXT NOT NULL,
                plan_sha256 TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN ('Proposed','Approved','Superseded')),
                created_utc TEXT NOT NULL,
                approved_utc TEXT,
                PRIMARY KEY(task_id,revision)
            );
            CREATE INDEX IF NOT EXISTS idx_implementation_plans_task
                ON implementation_plans(task_id,revision);
            """);
    }

    private static void CheckTaskId(string taskId)
    {
        if (taskId is null || !Id.IsMatch(taskId))
            throw new StoreFailure("INVALID_ID");
    }

    private static void CheckBinding(
        SqliteConnection db, SqliteTransaction tx, string taskId,
        int version, string? specificationSha, string? architectureSha)
    {
        if (version < 1 || specificationSha is null || !Sha.IsMatch(specificationSha) ||
            architectureSha is null || !Sha.IsMatch(architectureSha))
            throw new StoreFailure("INVALID_INPUT");
        if (TaskStore.FindTask(db, tx, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        using (var spec = TaskStore.Command(db, tx, """
            SELECT version,sha256,status FROM interview_specs
            WHERE task_id=@id ORDER BY version DESC LIMIT 1
            """, ("@id", taskId)))
        using (var reader = spec.ExecuteReader())
        {
            if (!reader.Read() || reader.GetString(2) != "Approved")
                throw new StoreFailure("SPECIFICATION_NOT_APPROVED");
            if (reader.GetInt32(0) != version ||
                reader.GetString(1) != specificationSha)
                throw new StoreFailure("SPECIFICATION_CONFLICT");
        }
        DraftArchitecturePolicy.AssertCurrent(db, tx, taskId, version,
            specificationSha, architectureSha);
    }

    private static string ComputeSha(string taskId, int revision,
        int version, string specSha, string archSha, string model,
        ImplementationOutline outline)
    {
        var payload = JsonSerializer.Serialize(new {
            schemaVersion = SchemaVersion, taskId, revision,
            specificationVersion = version, specificationSha256 = specSha,
            architectureSha256 = archSha, model, outline
        });
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static ImplementationPlan Decode(SqliteDataReader reader)
    {
        var outline = JsonSerializer.Deserialize<ImplementationOutline>(
            reader.GetString(7)) ??
            throw new InvalidOperationException("Invalid persisted implementation plan");
        return new ImplementationPlan(
            reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5),
            outline, reader.GetString(8), reader.GetString(9),
            reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
            false, "UNKNOWN");
    }

    private const string SelectColumns = """
        SELECT task_id,revision,specification_version,specification_sha256,
               architecture_sha256,model,schema_version,outline_json,
               plan_sha256,status,created_utc,approved_utc
        FROM implementation_plans
        """;

    private static ImplementationPlan? Latest(SqliteConnection db,
        SqliteTransaction? tx, string taskId)
    {
        using var command = TaskStore.Command(db, tx,
            SelectColumns + " WHERE task_id=@id ORDER BY revision DESC LIMIT 1",
            ("@id", taskId));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Decode(reader) : null;
    }

    internal static ImplementationPlan RequireCurrentApproved(SqliteConnection db,
        SqliteTransaction tx, string taskId)
    {
        CheckTaskId(taskId);
        var plan = Latest(db, tx, taskId);
        if (plan is null || plan.Status != "Approved")
            throw new StoreFailure("PLAN_NOT_APPROVED");
        CheckBinding(db, tx, taskId, plan.SpecificationVersion,
            plan.SpecificationSha256, plan.ArchitectureSha256);
        return plan;
    }

    // Historical QA must use its exact formerly approved plan, not the
    // current plan or a text match against a newer revision.
    internal static ImplementationPlan ReadForQa(SqliteConnection db,
        SqliteTransaction tx, QaReviewRun review)
    {
        using var command = TaskStore.Command(db, tx, SelectColumns +
            " WHERE task_id=@id AND revision=@revision AND plan_sha256=@hash" +
            " AND approved_utc IS NOT NULL",
            ("@id", review.TaskId), ("@revision", review.PlanRevision),
            ("@hash", review.PlanSha256));
        using var reader = command.ExecuteReader();
        return reader.Read() ? Decode(reader) : throw new StoreFailure("SOURCE_CHANGED");
    }

    private static ImplementationPlan Effective(ImplementationPlan value,
        int? specVersion, string? specSha, string? archSha)
    {
        var current = specVersion == value.SpecificationVersion &&
            specSha == value.SpecificationSha256 &&
            archSha == value.ArchitectureSha256;
        return value with {
            Current = current,
            EffectiveStatus = current ? value.Status : "STALE_APPROVALS"
        };
    }

    public ImplementationPlanSnapshot Read(string taskId)
    {
        CheckTaskId(taskId);
        var snapshot = interviews.Read(taskId); // validates task existence
        var spec = snapshot.Specification;
        var readiness = DraftArchitecturePolicy.Readiness(tasks, interviews, taskId);
        var eligible = spec?.Status == "Approved" &&
            readiness.ReadyForSourceGeneration;
        using var db = tasks.Open();
        using var command = TaskStore.Command(db, null,
            SelectColumns + " WHERE task_id=@id ORDER BY revision ASC",
            ("@id", taskId));
        using var reader = command.ExecuteReader();
        var history = new List<ImplementationPlan>();
        while (reader.Read())
            history.Add(Effective(Decode(reader),
                eligible ? spec?.Version : null,
                eligible ? spec?.Sha256 : null,
                eligible ? readiness.ArchitectureSha256 : null));
        return new ImplementationPlanSnapshot(SchemaVersion,
            history.LastOrDefault(), history.AsReadOnly());
    }

    public ImplementationPlan Propose(string taskId,
        GenerateImplementationPlanRequest request, string model,
        ImplementationOutline outline)
    {
        CheckTaskId(taskId);
        if (request.SchemaVersion != SchemaVersion || request.ExpectedRevision < 0 ||
            !LocalInterviewModel.AllowedModel(model))
            throw new StoreFailure("INVALID_INPUT");
        var safe = ImplementationPlanContract.Validate(outline);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        CheckBinding(db, tx, taskId, request.SpecificationVersion,
            request.SpecificationSha256, request.ArchitectureSha256);
        var previous = Latest(db, tx, taskId);
        if ((previous?.Revision ?? 0) != request.ExpectedRevision)
            throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
        var revision = request.ExpectedRevision + 1;
        var sha = ComputeSha(taskId, revision, request.SpecificationVersion,
            request.SpecificationSha256!, request.ArchitectureSha256!, model, safe);
        if (previous is not null)
            TaskStore.Execute(db, tx, """
                UPDATE implementation_plans SET status='Superseded'
                WHERE task_id=@id AND revision=@previous
                """, ("@id", taskId), ("@previous", previous.Revision));
        var created = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            INSERT INTO implementation_plans (
                task_id,revision,schema_version,specification_version,
                specification_sha256,architecture_sha256,model,outline_json,
                plan_sha256,status,created_utc)
            VALUES (@id,@revision,1,@specVersion,@specSha,@archSha,
                @model,@outline,@sha,'Proposed',@created)
            """, ("@id", taskId), ("@revision", revision),
            ("@specVersion", request.SpecificationVersion),
            ("@specSha", request.SpecificationSha256!),
            ("@archSha", request.ArchitectureSha256!),
            ("@model", model), ("@outline", JsonSerializer.Serialize(safe)),
            ("@sha", sha), ("@created", created));
        EngineeringActivityStore.Record(db,tx,taskId,"PlanProposed","TechnicalLead",
            "ImplementationPlan",sha,revision,toAgent:"HumanOwner",model:model);
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return Read(taskId).Plan!;
    }

    public ImplementationPlan Approve(string taskId,
        ApproveImplementationPlanRequest request)
    {
        CheckTaskId(taskId);
        if (request.SchemaVersion != SchemaVersion || request.ExpectedRevision < 1 ||
            request.PlanSha256 is null || !Sha.IsMatch(request.PlanSha256) ||
            request.Confirmation != "APPROVE " + request.PlanSha256)
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var plan = Latest(db, tx, taskId);
        if (plan is null)
            throw new StoreFailure("NOT_FOUND");
        if (plan.Revision != request.ExpectedRevision ||
            plan.PlanSha256 != request.PlanSha256 ||
            plan.Status != "Proposed")
            throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
        CheckBinding(db, tx, taskId, plan.SpecificationVersion,
            plan.SpecificationSha256, plan.ArchitectureSha256);
        var approved = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            UPDATE implementation_plans SET status='Approved', approved_utc=@approved
            WHERE task_id=@id AND revision=@revision AND status='Proposed'
            """, ("@id", taskId), ("@revision", plan.Revision),
            ("@approved", approved));
        if(request.TeamRepair is not null)
            UpfrontTeamAuthorizationStore.Record(db,tx,plan,request.TeamRepair);
        EngineeringActivityStore.Record(db,tx,taskId,"PlanApproved","HumanOwner",
            "ImplementationPlan",plan.PlanSha256,plan.Revision,toAgent:"TechnicalLead");
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return Read(taskId).Plan!;
    }

    public IReadOnlyList<MafHandoffEvent> Handoffs(string taskId)
    {
        var history = Read(taskId).History;
        var events = new List<MafHandoffEvent>();
        foreach (var plan in history)
        {
            events.Add(new MafHandoffEvent(taskId, "ArchitectureHandoff",
                "SolutionArchitect", "TechnicalLead",
                plan.SpecificationVersion, plan.SpecificationSha256,
                plan.Revision, plan.Model, plan.CreatedUtc, plan.Current,
                plan.PlanSha256, plan.ArchitectureSha256));
            events.Add(new MafHandoffEvent(taskId, "PlanProposed",
                "TechnicalLead", "HumanOwner",
                plan.SpecificationVersion, plan.SpecificationSha256,
                plan.Revision, plan.Model, plan.CreatedUtc, plan.Current,
                plan.PlanSha256, plan.ArchitectureSha256));
            if (plan.ApprovedUtc is not null)
                events.Add(new MafHandoffEvent(taskId, "PlanApproved",
                    "HumanOwner", "EngineeringBacklog",
                    plan.SpecificationVersion, plan.SpecificationSha256,
                    plan.Revision, plan.Model, plan.ApprovedUtc,
                    plan.Current && plan.Status == "Approved",
                    plan.PlanSha256, plan.ArchitectureSha256));
        }
        return events.AsReadOnly();
    }
}
