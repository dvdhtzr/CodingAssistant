using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

// One owner-approved future-file rule, consumed by exactly one admitted run.
// It is not an engineering delegation until completed source is bound.
public sealed record UpfrontTeamAuthorization(string TaskId,int PlanRevision,string PlanSha256,
    string[] Paths,int MaximumRepairs,int ExpectedDelegationRevision,string State,
    string? RunId,string? Reason,int? BoundPolicyRevision,string? BoundPolicySha256);

public sealed class UpfrontTeamAuthorizationStore(TaskStore tasks)
{
    internal static void Initialize(SqliteConnection db)=>TaskStore.Execute(db,null,"""
        CREATE TABLE IF NOT EXISTS upfront_team_authorizations(
            task_id TEXT NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
            plan_revision INTEGER NOT NULL,plan_sha256 TEXT NOT NULL,
            paths_json TEXT NOT NULL,maximum_repairs INTEGER NOT NULL,
            expected_delegation_revision INTEGER NOT NULL,
            state TEXT NOT NULL CHECK(state IN ('Pending','Claimed','Bound','Stopped')),
            run_id TEXT UNIQUE,reason TEXT,bound_policy_revision INTEGER,bound_policy_sha256 TEXT,
            PRIMARY KEY(task_id,plan_revision)
        );
        """);

    internal static UpfrontTeamAuthorization? Read(SqliteConnection db,SqliteTransaction? tx,string taskId,int? revision=null)
    {
        using var cmd=TaskStore.Command(db,tx,"""
            SELECT task_id,plan_revision,plan_sha256,paths_json,maximum_repairs,
                expected_delegation_revision,state,run_id,reason,bound_policy_revision,bound_policy_sha256
            FROM upfront_team_authorizations WHERE task_id=@task
                AND (@rev IS NULL OR plan_revision=@rev)
            ORDER BY plan_revision DESC LIMIT 1
            """,("@task",taskId),("@rev",(object?)revision??DBNull.Value));
        using var reader=cmd.ExecuteReader();
        if(!reader.Read())return null;
        return new(reader.GetString(0),reader.GetInt32(1),reader.GetString(2),
            JsonSerializer.Deserialize<string[]>(reader.GetString(3))!,reader.GetInt32(4),
            reader.GetInt32(5),reader.GetString(6),reader.IsDBNull(7)?null:reader.GetString(7),
            reader.IsDBNull(8)?null:reader.GetString(8),reader.IsDBNull(9)?null:reader.GetInt32(9),
            reader.IsDBNull(10)?null:reader.GetString(10));
    }
    public UpfrontTeamAuthorization? Read(string taskId)
    {
        using var db=tasks.Open();
        if(TaskStore.FindTask(db,null,taskId) is null)throw new StoreFailure("NOT_FOUND");
        return Read(db,null,taskId);
    }
    internal static void Record(SqliteConnection db,SqliteTransaction tx,ImplementationPlan plan,UpfrontRepairAuthorization scope)
    {
        if(scope.MaximumRepairs is <1 or >8||scope.Paths is null||scope.Paths.Length is <1 or >12||
            scope.Paths.Any(p=>string.IsNullOrWhiteSpace(p)||ProjectDraftStore.SafePath(p)!=p)||
            scope.Paths.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=scope.Paths.Length)
            throw new StoreFailure("INVALID_INPUT");
        var prior=EngineeringDelegationStore.Latest(db,tx,plan.TaskId);
        if(prior?.Status is "Approved" or "Proposed")throw new StoreFailure("DELEGATION_NOT_AUTHORIZED");
        TaskStore.Execute(db,tx,"""
            INSERT INTO upfront_team_authorizations(task_id,plan_revision,plan_sha256,paths_json,
                maximum_repairs,expected_delegation_revision,state)
            VALUES(@task,@rev,@sha,@paths,@budget,@prior,'Pending')
            """,("@task",plan.TaskId),("@rev",plan.Revision),("@sha",plan.PlanSha256),
            ("@paths",JsonSerializer.Serialize(scope.Paths.OrderBy(p=>p,StringComparer.Ordinal).ToArray())),
            ("@budget",scope.MaximumRepairs),("@prior",prior?.Revision??0));
    }
    internal static void Claim(SqliteConnection db,SqliteTransaction tx,EngineeringRun run)
    {
        var intent=Read(db,tx,run.TaskId,run.PlanRevision);
        if(intent is null||intent.State!="Pending"||intent.PlanSha256!=run.PlanSha256||
            (EngineeringDelegationStore.Latest(db,tx,run.TaskId)?.Revision??0)!=intent.ExpectedDelegationRevision)
            throw new StoreFailure("DELEGATION_NOT_AUTHORIZED");
        if(TaskStore.Execute(db,tx,"""
            UPDATE upfront_team_authorizations SET state='Claimed',run_id=@run
            WHERE task_id=@task AND plan_revision=@rev AND state='Pending'
            """,("@run",run.Id),("@task",run.TaskId),("@rev",run.PlanRevision))!=1)
            throw new StoreFailure("DELEGATION_NOT_AUTHORIZED");
    }
    internal static void Stop(SqliteConnection db,SqliteTransaction tx,string taskId,string runId)
    {
        var intent=Read(db,tx,taskId);
        if(intent?.RunId!=runId||intent.State is not ("Claimed" or "Bound"))return;
        if(intent.BoundPolicyRevision is int rev)
            TaskStore.Execute(db,tx,"""
                UPDATE engineering_delegation SET status='Revoked'
                WHERE task_id=@task AND revision=@rev AND policy_sha256=@sha AND status='Approved'
                """,("@task",taskId),("@rev",rev),("@sha",intent.BoundPolicySha256!));
        TaskStore.Execute(db,tx,"""
            UPDATE upfront_team_authorizations SET state='Stopped',reason='OWNER_CANCELLED'
            WHERE task_id=@task AND run_id=@run
            """,("@task",taskId),("@run",runId));
    }
    public void MarkStopped(string taskId,string runId,string reason)
    {
        using var db=tasks.Open();using var tx=db.BeginTransaction();
        TaskStore.Execute(db,tx,"""
            UPDATE upfront_team_authorizations SET state='Stopped',reason=@reason
            WHERE task_id=@task AND run_id=@run AND state='Claimed'
            """,("@task",taskId),("@run",runId),("@reason",reason));
        tx.Commit();tasks.SignalEngineeringActivity(taskId);
    }
}
