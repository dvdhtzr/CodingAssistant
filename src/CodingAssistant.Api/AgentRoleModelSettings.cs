using Microsoft.Data.Sqlite;

namespace CodingAssistant.Api;

/// <summary>
/// Owner-configured Ollama preferences for individual MAF participants.
/// These are model choices, not new skills, privileges or execution approvals.
/// A running job pins its selected model in the existing immutable job record.
/// </summary>
public sealed record SetAgentRoleModelRequest(string? Model);
public sealed record AgentRoleModelSelection(
    string Role, string Model, bool Overridden, string Source);

public sealed class AgentRoleModelSettings(TaskStore tasks)
{
    public const string PreferredCoder = "qwen2.5-coder:14b";
    private static readonly string[] Configurable =
        ["SolutionArchitect", "TechnicalLead", "SoftwareEngineer", "QaEngineer"];

    public void Initialize()
    {
        using var db = tasks.Open();
        TaskStore.Execute(db, null, """
            CREATE TABLE IF NOT EXISTS agent_role_models (
                task_id TEXT NOT NULL REFERENCES tasks(id),
                agent_id TEXT NOT NULL CHECK(agent_id IN
                    ('SolutionArchitect','TechnicalLead','SoftwareEngineer','QaEngineer')),
                model TEXT NOT NULL,
                updated_utc TEXT NOT NULL,
                PRIMARY KEY(task_id,agent_id)
            );
            """);
    }

    private static void ValidateRole(string role)
    {
        if (!Configurable.Contains(role, StringComparer.Ordinal))
            throw new StoreFailure("INVALID_AGENT_ROLE");
    }

    private static string DefaultFor(string role, string inherited) =>
        role is "SoftwareEngineer" or "QaEngineer" ? PreferredCoder : inherited;

    public string Resolve(string taskId, string role, string inherited)
    {
        ValidateRole(role);
        if (!LocalOllamaModelCatalog.ValidLocalName(inherited))
            throw new StoreFailure("INVALID_INPUT");
        using var db = tasks.Open();
        _ = tasks.GetTask(taskId);
        var selected = TaskStore.Scalar(db, null, """
            SELECT model FROM agent_role_models
            WHERE task_id=@task AND agent_id=@role
            """, ("@task", taskId), ("@role", role));
        return selected ?? DefaultFor(role, inherited);
    }

    public IReadOnlyList<AgentRoleModelSelection> Read(string taskId, string inherited,
        string? interviewModel = null)
    {
        if (!LocalOllamaModelCatalog.ValidLocalName(inherited))
            throw new StoreFailure("INVALID_INPUT");
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        var selections = new List<AgentRoleModelSelection> {
            new("ProductOwner", interviewModel ?? inherited, false,
                interviewModel is null ? "Choose in Product Discovery" :
                    "Pinned interview model")
        };
        foreach (var role in Configurable)
        {
            var selected = TaskStore.Scalar(db, null, """
                SELECT model FROM agent_role_models
                WHERE task_id=@task AND agent_id=@role
                """, ("@task", taskId), ("@role", role));
            selections.Add(new(role, selected ?? DefaultFor(role, inherited),
                selected is not null,
                selected is not null ? "Owner choice" :
                    role is "SoftwareEngineer" or "QaEngineer" ?
                    "Preferred coding model" : "Inherited specification model"));
        }
        return selections;
    }

    public AgentRoleModelSelection Set(string taskId, string role, string? model,
        string inherited)
    {
        ValidateRole(role);
        if (!LocalOllamaModelCatalog.ValidLocalName(inherited) ||
            model is not null && !LocalOllamaModelCatalog.ValidLocalName(model))
            throw new StoreFailure("INVALID_INPUT");
        _ = tasks.GetTask(taskId);
        using var db = tasks.Open();
        using var tx = db.BeginTransaction();
        if (model is null)
            TaskStore.Execute(db, tx, """
                DELETE FROM agent_role_models
                WHERE task_id=@task AND agent_id=@role
                """, ("@task", taskId), ("@role", role));
        else
            TaskStore.Execute(db, tx, """
                INSERT INTO agent_role_models(task_id,agent_id,model,updated_utc)
                VALUES(@task,@role,@model,@time)
                ON CONFLICT(task_id,agent_id) DO UPDATE
                SET model=excluded.model,updated_utc=excluded.updated_utc
                """, ("@task", taskId), ("@role", role), ("@model", model),
                ("@time", DateTimeOffset.UtcNow.ToString("O")));
        tx.Commit();
        return new AgentRoleModelSelection(role,
            model ?? DefaultFor(role, inherited), model is not null,
            model is not null ? "Owner choice" :
                role is "SoftwareEngineer" or "QaEngineer" ?
                "Preferred coding model" : "Inherited specification model");
    }
}
