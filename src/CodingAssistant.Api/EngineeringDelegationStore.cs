using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record RoutineRepairScope(string BaselineRunId, string BaselineSourceSha256,
    string[] Paths, int MaximumRepairs);
public sealed record ProposeDelegationRequest(int ExpectedRevision,
    string PlanSha256, string[] Operations, RoutineRepairScope? RoutineRepair = null);
public sealed record ApproveDelegationRequest(int Revision,
    string PolicySha256, string Confirmation);
public sealed record DelegationPolicy(string TaskId, int Revision,
    int PlanRevision, string PlanSha256, int SpecificationVersion,
    string SpecificationSha256, string ArchitectureSha256,
    IReadOnlyList<string> Operations, string PolicySha256, string Status,
    string CreatedUtc, string? ApprovedUtc, bool Current = false,
    string EffectiveStatus = "UNKNOWN", RoutineRepairScope? RoutineRepair = null);

/// <summary>Versioned, owner-approved opt-in for future autonomous routine
/// actions. This is an additional permission gate, never an alternative
/// to exact-scope source, repair, runtime or execution admission checks.
/// No execution, Git, deployment, scope expansion or approval operation
/// exists in this allowlist.</summary>
public sealed class EngineeringDelegationStore(TaskStore tasks)
{
    private static readonly string[] AllowedOperations =
        ["SendMessage", "ScheduleWork", "WriteSource", "ReviewCode",
         "ReviewAcceptance", "RequestRepair"];

    internal static void Initialize(SqliteConnection db) =>
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS engineering_delegation (
                task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
                revision INTEGER NOT NULL CHECK(revision>0),
                plan_revision INTEGER NOT NULL,
                plan_sha256 TEXT NOT NULL,
                spec_version INTEGER NOT NULL,
                spec_sha256 TEXT NOT NULL,
                architecture_sha256 TEXT NOT NULL,
                operations_json TEXT NOT NULL,
                policy_sha256 TEXT NOT NULL,
                status TEXT NOT NULL CHECK(status IN
                    ('Proposed','Approved','Superseded','Revoked')),
                created_utc TEXT NOT NULL,
                approved_utc TEXT,
                PRIMARY KEY(task_id,revision)
            );
            CREATE TABLE IF NOT EXISTS engineering_routine_repair_scope (
                task_id TEXT NOT NULL, revision INTEGER NOT NULL,
                scope_json TEXT NOT NULL,
                PRIMARY KEY(task_id,revision),
                FOREIGN KEY(task_id,revision) REFERENCES engineering_delegation(task_id,revision) ON DELETE CASCADE
            );
            CREATE TABLE IF NOT EXISTS engineering_routine_repair_admissions (
                task_id TEXT NOT NULL, revision INTEGER NOT NULL,
                policy_sha256 TEXT NOT NULL, cycle_id TEXT NOT NULL UNIQUE,
                base_run_id TEXT NOT NULL, candidate_run_id TEXT NOT NULL UNIQUE,
                path TEXT NOT NULL, created_utc TEXT NOT NULL,
                PRIMARY KEY(task_id,revision,cycle_id),
                FOREIGN KEY(task_id,revision) REFERENCES engineering_delegation(task_id,revision) ON DELETE CASCADE
            );
            """);

    internal static DelegationPolicy? Latest(SqliteConnection db,
        SqliteTransaction? tx, string taskId)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT task_id,revision,plan_revision,plan_sha256,spec_version,
                spec_sha256,architecture_sha256,operations_json,policy_sha256,
                status,created_utc,approved_utc,
                (SELECT scope_json FROM engineering_routine_repair_scope s
                 WHERE s.task_id=engineering_delegation.task_id AND s.revision=engineering_delegation.revision)
            FROM engineering_delegation WHERE task_id=@task
            ORDER BY revision DESC LIMIT 1
            """, ("@task", taskId));
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;
        return new DelegationPolicy(reader.GetString(0),reader.GetInt32(1),
            reader.GetInt32(2),reader.GetString(3),reader.GetInt32(4),
            reader.GetString(5),reader.GetString(6),
            JsonSerializer.Deserialize<string[]>(reader.GetString(7))!,
            reader.GetString(8),reader.GetString(9),reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            RoutineRepair: reader.IsDBNull(12) ? null : JsonSerializer.Deserialize<RoutineRepairScope>(reader.GetString(12)));
    }

    private static void RequireBinding(DelegationPolicy policy, ImplementationPlan plan)
    {
        if (policy.PlanRevision != plan.Revision ||
            policy.PlanSha256 != plan.PlanSha256 ||
            policy.SpecificationVersion != plan.SpecificationVersion ||
            policy.SpecificationSha256 != plan.SpecificationSha256 ||
            policy.ArchitectureSha256 != plan.ArchitectureSha256)
            throw new StoreFailure("STALE_DELEGATION_POLICY");
    }

    private static ImplementationPlan? CurrentPlan(SqliteConnection db,
        SqliteTransaction tx, string taskId)
    {
        try { return ImplementationPlanStore.RequireCurrentApproved(db, tx, taskId); }
        catch (StoreFailure) { return null; }
    }

    private static DelegationPolicy Effective(DelegationPolicy policy,
        ImplementationPlan? plan, bool latest)
    {
        var current = latest && plan is not null &&
            policy.PlanRevision == plan.Revision &&
            policy.PlanSha256 == plan.PlanSha256 &&
            policy.SpecificationVersion == plan.SpecificationVersion &&
            policy.SpecificationSha256 == plan.SpecificationSha256 &&
            policy.ArchitectureSha256 == plan.ArchitectureSha256;
        return policy with { Current = current,
            EffectiveStatus = !current && policy.Status is ("Approved" or "Proposed")
                ? "STALE_APPROVALS" : policy.Status };
    }

    public DelegationPolicy? Read(string taskId)
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        if (TaskStore.FindTask(db, tx, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        var policy = Latest(db, tx, taskId);
        var result = policy is null ? null : Effective(policy, CurrentPlan(db, tx, taskId), true);
        tx.Commit();
        return result;
    }

    public IReadOnlyList<DelegationPolicy> History(string taskId)
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        if (TaskStore.FindTask(db, tx, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        var plan = CurrentPlan(db, tx, taskId);
        var history = new List<DelegationPolicy>();
        using (var cmd = TaskStore.Command(db, tx, """
            SELECT task_id,revision,plan_revision,plan_sha256,spec_version,
                spec_sha256,architecture_sha256,operations_json,policy_sha256,
                status,created_utc,approved_utc,
                (SELECT scope_json FROM engineering_routine_repair_scope s
                 WHERE s.task_id=engineering_delegation.task_id AND s.revision=engineering_delegation.revision)
            FROM engineering_delegation WHERE task_id=@task ORDER BY revision
            """, ("@task",taskId)))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                history.Add(new DelegationPolicy(reader.GetString(0),reader.GetInt32(1),
                    reader.GetInt32(2),reader.GetString(3),reader.GetInt32(4),
                    reader.GetString(5),reader.GetString(6),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(7))!,
                    reader.GetString(8),reader.GetString(9),reader.GetString(10),
                    reader.IsDBNull(11) ? null : reader.GetString(11),
            RoutineRepair: reader.IsDBNull(12) ? null : JsonSerializer.Deserialize<RoutineRepairScope>(reader.GetString(12))));
        var result = history.Select((item,index) =>
            Effective(item,plan,index == history.Count-1)).ToArray();
        tx.Commit();
        return result;
    }

    private static RoutineRepairScope? ValidateRoutineScope(SqliteConnection db,
        SqliteTransaction tx, string taskId, ImplementationPlan plan,
        IReadOnlyList<string> operations, RoutineRepairScope? scope)
    {
        if (scope is null) return null;
        if (scope.BaselineRunId is not { Length: 32 } ||
            !scope.BaselineRunId.All(Uri.IsHexDigit) ||
            scope.BaselineSourceSha256 is not { Length: 64 } ||
            !scope.BaselineSourceSha256.All(Uri.IsHexDigit) ||
            scope.MaximumRepairs is < 1 or > 8 || scope.Paths is null ||
            scope.Paths.Length is < 1 or > 12 ||
            scope.Paths.Any(p => string.IsNullOrWhiteSpace(p) || ProjectDraftStore.SafePath(p) != p) ||
            scope.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != scope.Paths.Length ||
            new[] {"ScheduleWork","RequestRepair","WriteSource","ReviewCode","ReviewAcceptance"}
                .Any(op => !operations.Contains(op, StringComparer.Ordinal)))
            throw new StoreFailure("INVALID_INPUT");
        var source = EngineeringRunStore.Find(db, tx, scope.BaselineRunId);
        if (source is null || source.TaskId != taskId ||
            !RepairCycleStore.IsCoordinationSource(db,tx,source,scope.BaselineSourceSha256) ||
            source.PlanSha256 != plan.PlanSha256 ||
            TaskStore.Scalar(db, tx, """
                SELECT id FROM engineering_runs WHERE task_id=@task ORDER BY created_utc DESC,id DESC LIMIT 1
                """, ("@task", taskId)) != source.Id ||
            EngineeringMessageStore.SourceFingerprint(db, tx, source.Id) != scope.BaselineSourceSha256)
            throw new StoreFailure("SOURCE_CHANGED");
        EngineeringRunStore.RequireBinding(db, tx, source);
        foreach (var path in scope.Paths)
            if (TaskStore.Scalar(db, tx, """
                SELECT path FROM engineering_run_files WHERE run_id=@run AND path=@path
                """, ("@run",source.Id),("@path",path)) != path)
                throw new StoreFailure("REPAIR_SCOPE_VIOLATION");
        return scope with { Paths = scope.Paths.OrderBy(p => p, StringComparer.Ordinal).ToArray() };
    }

    public DelegationPolicy Propose(string taskId, ProposeDelegationRequest input)
    {
        if (input.ExpectedRevision < 0 || input.Operations is null ||
            input.Operations.Length is < 1 or > 6 ||
            input.Operations.Distinct(StringComparer.Ordinal).Count() != input.Operations.Length ||
            input.Operations.Any(op => !AllowedOperations.Contains(op, StringComparer.Ordinal)))
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var plan = ImplementationPlanStore.RequireCurrentApproved(db, tx, taskId);
        if (input.PlanSha256 != plan.PlanSha256)
            throw new StoreFailure("STALE_IMPLEMENTATION_PLAN");
        var previous = Latest(db, tx, taskId);
        if ((previous?.Revision ?? 0) != input.ExpectedRevision)
            throw new StoreFailure("STALE_DELEGATION_POLICY");
        var revision = input.ExpectedRevision + 1;
        var operations = input.Operations.OrderBy(op => op, StringComparer.Ordinal).ToArray();
        var routine = ValidateRoutineScope(db, tx, taskId, plan, operations, input.RoutineRepair);
        var sha = Convert.ToHexString(SHA256.HashData(routine is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, taskId, revision,
                planRevision = plan.Revision, plan.PlanSha256, plan.SpecificationVersion,
                plan.SpecificationSha256, plan.ArchitectureSha256, operations })
            : JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, taskId, revision,
                planRevision = plan.Revision, plan.PlanSha256, plan.SpecificationVersion,
                plan.SpecificationSha256, plan.ArchitectureSha256, operations, routine }))).ToLowerInvariant();
        if (previous is not null && previous.Status is ("Approved" or "Proposed"))
            TaskStore.Execute(db, tx, """
                UPDATE engineering_delegation SET status='Superseded'
                WHERE task_id=@task AND revision=@revision
                """, ("@task",taskId),("@revision",previous.Revision));
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            INSERT INTO engineering_delegation(task_id,revision,plan_revision,
                plan_sha256,spec_version,spec_sha256,architecture_sha256,
                operations_json,policy_sha256,status,created_utc)
            VALUES(@task,@revision,@pr,@plan,@sv,@spec,@arch,@ops,@sha,'Proposed',@now)
            """, ("@task",taskId),("@revision",revision),("@pr",plan.Revision),
            ("@plan",plan.PlanSha256),("@sv",plan.SpecificationVersion),
            ("@spec",plan.SpecificationSha256),("@arch",plan.ArchitectureSha256),
            ("@ops",JsonSerializer.Serialize(operations)),("@sha",sha),("@now",now));
        if (routine is not null)
            TaskStore.Execute(db, tx, """
                INSERT INTO engineering_routine_repair_scope(task_id,revision,scope_json)
                VALUES(@task,@revision,@scope)
                """, ("@task",taskId),("@revision",revision),("@scope",JsonSerializer.Serialize(routine)));
        var result = Effective(Latest(db, tx, taskId)!,plan,true);
        EngineeringActivityStore.Record(db,tx,taskId,"DelegationProposed","HumanOwner",
            "DelegationPolicy",sha,revision);
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return result;
    }

    public DelegationPolicy Approve(string taskId, ApproveDelegationRequest input)
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var policy = Latest(db, tx, taskId) ?? throw new StoreFailure("NOT_FOUND");
        if (policy.Status != "Proposed" || policy.Revision != input.Revision ||
            policy.PolicySha256 != input.PolicySha256 ||
            input.Confirmation != "APPROVE " + policy.PolicySha256)
            throw new StoreFailure("APPROVAL_MISMATCH");
        var plan = ImplementationPlanStore.RequireCurrentApproved(db, tx, taskId);
        RequireBinding(policy, plan);
        _ = ValidateRoutineScope(db, tx, taskId, plan, policy.Operations, policy.RoutineRepair);
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            UPDATE engineering_delegation SET status='Approved',approved_utc=@now
            WHERE task_id=@task AND revision=@revision
            """, ("@task",taskId),("@revision",policy.Revision),("@now",now));
        EngineeringActivityStore.Record(db,tx,taskId,"DelegationApproved","HumanOwner",
            "DelegationPolicy",policy.PolicySha256,policy.Revision);
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return policy with { Status = "Approved", ApprovedUtc = now,
            Current = true, EffectiveStatus = "Approved" };
    }

    public DelegationPolicy Revoke(string taskId, ApproveDelegationRequest input)
    {
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        var policy = Latest(db, tx, taskId) ?? throw new StoreFailure("NOT_FOUND");
        if (policy.Status is not ("Approved" or "Proposed") ||
            policy.Revision != input.Revision || policy.PolicySha256 != input.PolicySha256 ||
            input.Confirmation != "REVOKE " + policy.PolicySha256)
            throw new StoreFailure("APPROVAL_MISMATCH");
        TaskStore.Execute(db, tx, """
            UPDATE engineering_delegation SET status='Revoked'
            WHERE task_id=@task AND revision=@revision
            """, ("@task",taskId),("@revision",policy.Revision));
        var result = Effective(policy with { Status = "Revoked" },
            CurrentPlan(db,tx,taskId),true);
        EngineeringActivityStore.Record(db,tx,taskId,"DelegationRevoked","HumanOwner",
            "DelegationPolicy",policy.PolicySha256,policy.Revision);
        tx.Commit();
        tasks.SignalEngineeringActivity(taskId);
        return result;
    }

    // Admission is host-internal and must be called in the same transaction
    // as the original exact-source repair checks and candidate creation.
    internal static void AdmitRoutineRepair(SqliteConnection db, SqliteTransaction tx,
        CollaborationRun parent, RepairProposal proposal, string path,
        string cycleId, string candidateRunId)
    {
        var taskId = parent.TaskId;
        Require(db,tx,taskId,parent.PolicyRevision,parent.PolicySha256,"TechnicalLead","RequestRepair");
        Require(db,tx,taskId,parent.PolicyRevision,parent.PolicySha256,"SoftwareEngineer","WriteSource");
        var policy = Latest(db,tx,taskId)!;
        var scope = policy.RoutineRepair ?? throw new StoreFailure("REPAIR_NOT_AUTHORIZED");
        if (!scope.Paths.Contains(path,StringComparer.Ordinal) ||
            parent.RunId != proposal.EngineeringRunId ||
            parent.SourceSha256 != proposal.SourceSha256 ||
            parent.PlanSha256 != proposal.PlanSha256)
            throw new StoreFailure("REPAIR_SCOPE_VIOLATION");
        if (EngineeringMessageStore.SourceFingerprint(db,tx,scope.BaselineRunId) != scope.BaselineSourceSha256)
            throw new StoreFailure("SOURCE_CHANGED");
        var count = int.Parse(TaskStore.Scalar(db,tx,"""
            SELECT COUNT(*) FROM engineering_routine_repair_admissions
            WHERE task_id=@task AND revision=@rev
            """,("@task",taskId),("@rev",policy.Revision)) ?? "0");
        if (count >= scope.MaximumRepairs)
            throw new StoreFailure("ROUTINE_REPAIR_BUDGET_EXHAUSTED");
        // No unrelated rerun or manually created repair can silently inherit
        // the delegation. Each later candidate must descend from a previous
        // admission under the same policy and have independent recheck evidence.
        var cursor = parent.RunId;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (cursor != scope.BaselineRunId)
        {
            if (!visited.Add(cursor) || visited.Count > scope.MaximumRepairs)
                throw new StoreFailure("SOURCE_CHANGED");
            cursor = TaskStore.Scalar(db,tx,"""
                SELECT a.base_run_id FROM engineering_routine_repair_admissions a
                JOIN repair_cycles c ON c.id=a.cycle_id
                WHERE a.task_id=@task AND a.revision=@rev AND a.policy_sha256=@sha
                AND a.candidate_run_id=@run AND c.engineering_run_id=@run
                AND c.state IN ('Generated','QaReviewed')
                """,("@task",taskId),("@rev",policy.Revision),("@sha",policy.PolicySha256),
                ("@run",cursor)) ?? throw new StoreFailure("SOURCE_CHANGED");
        }
        if (parent.RunId != scope.BaselineRunId)
        {
            var progress = RepairProgressStore.Read(db,tx,taskId,parent.RunId,scope);
            if (progress.StrategyChangeRequired ||
                progress.Attempts.LastOrDefault() is not { IndependentStaticResolution:true })
                throw new StoreFailure("ROUTINE_REPAIR_RECHECK_REQUIRED");
        }
        TaskStore.Execute(db,tx,"""
            INSERT INTO engineering_routine_repair_admissions(task_id,revision,policy_sha256,
                cycle_id,base_run_id,candidate_run_id,path,created_utc)
            VALUES(@task,@rev,@sha,@cycle,@base,@candidate,@path,@utc)
            """,("@task",taskId),("@rev",policy.Revision),("@sha",policy.PolicySha256),
            ("@cycle",cycleId),("@base",parent.RunId),("@candidate",candidateRunId),
            ("@path",path),("@utc",DateTimeOffset.UtcNow.ToString("O")));
    }

    // Revalidate revocation and source-only scope at each candidate write,
    // not merely when the agent was dispatched. The manual path is unchanged.
    internal static void RequireRoutineWrite(SqliteConnection db,SqliteTransaction tx,
        EngineeringRun job,IReadOnlyList<EngineeringFileChange>? files=null)
    {
        int revision; string sha; string path;
        using (var cmd=TaskStore.Command(db,tx,"""
            SELECT revision,policy_sha256,path FROM engineering_routine_repair_admissions
            WHERE task_id=@task AND candidate_run_id=@run
            """,("@task",job.TaskId),("@run",job.Id)))
        using (var reader=cmd.ExecuteReader())
        {
            if (!reader.Read()) return;
            revision=reader.GetInt32(0);sha=reader.GetString(1);path=reader.GetString(2);
        }
        Require(db,tx,job.TaskId,revision,sha,"SoftwareEngineer","WriteSource");
        var scope=Latest(db,tx,job.TaskId)?.RoutineRepair;
        if (scope is null || !scope.Paths.Contains(path,StringComparer.Ordinal) ||
            (files is not null && files.Any(f=>f.Path!=path)))
            throw new StoreFailure("REPAIR_SCOPE_VIOLATION");
    }

    // Called inside the same transaction as a future autonomous action.
    // Callers must ALSO pass the existing action-specific admission checks.
    internal static void Require(SqliteConnection db, SqliteTransaction tx,
        string taskId, int revision, string sha, string role, string operation)
    {
        var capability = operation switch
        {
            "SendMessage" => EngineeringCapability.SendMessage,
            "ScheduleWork" or "RequestRepair" => EngineeringCapability.ScheduleWork,
            "WriteSource" => EngineeringCapability.WriteSource,
            "ReviewCode" => EngineeringCapability.ReviewCode,
            "ReviewAcceptance" => EngineeringCapability.ReviewAcceptance,
            _ => throw new StoreFailure("AGENT_CAPABILITY_DENIED")
        };
        EngineeringRoleCapabilities.Require(role, capability);
        var policy = Latest(db, tx, taskId);
        if (policy is null || policy.Status != "Approved" ||
            policy.Revision != revision || policy.PolicySha256 != sha ||
            !policy.Operations.Contains(operation, StringComparer.Ordinal))
            throw new StoreFailure("DELEGATION_NOT_AUTHORIZED");
        RequireBinding(policy, ImplementationPlanStore.RequireCurrentApproved(db, tx, taskId));
    }
}
