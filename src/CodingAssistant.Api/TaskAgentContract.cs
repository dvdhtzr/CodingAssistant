using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodingAssistant.Api;

/// <summary>
/// Converts only the CURRENT approved interview and completed project-file plan
/// into the exact immutable task contract consumed by the isolated Gateway.
/// Preview is read-only and explicitly NOT executable authorization.
/// </summary>
public sealed record TaskAgentContractPreview(
    string ContractSha256, JsonElement Contract, bool ExecutionEnabled);

public static class TaskAgentContract
{
    public const string Schema = "codingassistant-task-contract-v1";

    public static TaskAgentContractPreview Preview(
        TaskItem task, InterviewSpecification? specification,
        ProjectDraftSnapshot? draft)
    {
        if (specification is null || specification.Status != "Approved" ||
            draft is null || draft.Job.State != "Generated" ||
            draft.Job.TaskId != task.Id ||
            draft.Job.SpecificationVersion != specification.Version ||
            draft.Job.SpecificationSha256 != specification.Sha256 ||
            draft.Files.Count is < 1 or > ProjectDraftStore.MaxFiles)
            throw new StoreFailure("SPECIFICATION_CONFLICT");

        // M1.12 has been qualified only with this pinned model. Qwen Coder
        // must earn its own M1 runtime profile before executable admission.
        if (specification.Model != TaskStore.Model ||
            draft.Job.Model != TaskStore.Model)
            throw new StoreFailure("MODEL_NOT_QUALIFIED");

        var files = draft.Files
            .Select(file => ProjectDraftStore.SafePath(file.Path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        if (files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != files.Length)
            throw new StoreFailure("INVALID_PROJECT_PLAN");

        var contentSha = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(specification.Content))).ToLowerInvariant();

        // Property order is deterministic, but the opaque contract digest is
        // computed over the actual UTF-8 JSON bytes received by the agent.
        var value = new
        {
            schema = Schema,
            taskId = task.Id,
            specificationVersion = specification.Version,
            approvedSpecificationSha256 = specification.Sha256,
            specificationSha256 = contentSha,
            objective = task.Objective,
            specification = specification.Content,
            model = specification.Model,
            files
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > 24576)
            throw new StoreFailure("PROJECT_SIZE_LIMIT");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        using var document = JsonDocument.Parse(bytes);
        return new TaskAgentContractPreview(sha, document.RootElement.Clone(),
            ExecutionEnabled: false);
    }
}
