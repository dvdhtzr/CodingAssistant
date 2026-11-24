using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

public sealed record ProposeDelegationRequest(int ExpectedRevision,
    string PlanSha256, string[] Operations);
public sealed record ApproveDelegationRequest(int Revision,
    string PolicySha256, string Confirmation);
public sealed record DelegationPolicy(string TaskId, int Revision,
    int PlanRevision, string PlanSha256, int SpecificationVersion,
    string SpecificationSha256, string ArchitectureSha256,
    IReadOnlyList<string> Operations, string PolicySha256, string Status,
    string CreatedUtc, string? ApprovedUtc);

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
            """);

    private static DelegationPolicy? Latest(SqliteConnection db,
        SqliteTransaction? tx, string taskId)
    {
        using var cmd = TaskStore.Command(db, tx, """
            SELECT task_id,revision,plan_revision,plan_sha256,spec_version,
                spec_sha256,architecture_sha256,operations_json,policy_sha256,
                status,created_utc,approved_utc
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
            reader.IsDBNull(11) ? null : reader.GetString(11));
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

    public DelegationPolicy? Read(string taskId)
    {
        using var db = tasks.Open();
        if (TaskStore.FindTask(db, null, taskId) is null)
            throw new StoreFailure("NOT_FOUND");
        return Latest(db, null, taskId);
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
        var sha = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { schemaVersion = 1, taskId, revision, planRevision = plan.Revision,
                plan.PlanSha256, plan.SpecificationVersion, plan.SpecificationSha256,
                plan.ArchitectureSha256, operations }))).ToLowerInvariant();
        if (previous is not null && previous.Status is "Approved" or "Proposed")
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
        var result = Latest(db, tx, taskId)!;
        tx.Commit();
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
        RequireBinding(policy, ImplementationPlanStore.RequireCurrentApproved(db, tx, taskId));
        var now = DateTimeOffset.UtcNow.ToString("O");
        TaskStore.Execute(db, tx, """
            UPDATE engineering_delegation SET status='Approved',approved_utc=@now
            WHERE task_id=@task AND revision=@revision
            """, ("@task",taskId),("@revision",policy.Revision),("@now",now));
        tx.Commit();
        return policy with { Status = "Approved", ApprovedUtc = now };
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
        tx.Commit();
        return policy with { Status = "Revoked" };
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
