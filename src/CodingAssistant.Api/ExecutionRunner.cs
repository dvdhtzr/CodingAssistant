using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace CodingAssistant.Api;

public sealed record M1ExecutablePreview(string PlanHash, JsonElement Plan);
public enum M1ExecutionOutcome { Verified, Uncertain }

public sealed record M1RunResult(
    string RunId, string PlanHash, string Status, string Phase,
    string? ReasonCode, bool? EvaluatorVerified, bool? SourceChanged,
    int? InferenceRequests, string Patch, bool PatchOmitted);

public interface IM1ResultReader
{
    Task<M1RunResult?> ReadResultAsync(
        string runId, string expectedPlanHash, CancellationToken cancellation);
}

public sealed class DisabledM1ResultReader : IM1ResultReader
{
    public Task<M1RunResult?> ReadResultAsync(
        string runId, string hash, CancellationToken cancellation) =>
        Task.FromResult<M1RunResult?>(null);
}

public interface IM1ExecutionRunner
{
    Task<M1ExecutablePreview> PreviewAsync(RunItem run, CancellationToken cancellation);
    Task<M1ExecutionOutcome> ExecuteAsync(
        RunItem run, string executionHash, CancellationToken cancellation);
    Task<bool> ReconcileAsync(string requestId, CancellationToken cancellation);
}

public sealed class ExecutionUnavailable : Exception { }

// Never trust arbitrary agent stdout for progress. Only fixed M1 host-runner
// categories are mapped to durable, non-content run events.
public static class M1StageNames
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "ScopeVerified", "WorkspacePrepared", "InferenceBridgeReady",
        "GatewayLaunched", "GatewayReady", "CodingRequestSent",
        "CodingRequestReturned", "GatewayRemoved", "EvaluatorStarted",
        "EvaluatorPassed",
    };

    public static bool IsAllowed(string stage) => Allowed.Contains(stage);
}

public interface IM1ProgressReporter
{
    Task<M1ExecutionOutcome> ExecuteWithProgressAsync(
        RunItem run, string executionHash,
        Func<string, Task> onStage, CancellationToken cancellation);
}

public sealed class DisabledM1Runner : IM1ExecutionRunner
{
    public Task<M1ExecutablePreview> PreviewAsync(RunItem run, CancellationToken token) =>
        throw new ExecutionUnavailable();
    public Task<M1ExecutionOutcome> ExecuteAsync(
        RunItem run, string hash, CancellationToken token) =>
        throw new ExecutionUnavailable();
    public Task<bool> ReconcileAsync(string requestId, CancellationToken token) =>
        throw new ExecutionUnavailable();
}

/// <summary>
/// Thin, non-shell Linux adapter to the already-qualified fixed M1.12 runner.
/// No arbitrary project, model, script, mount or worktree is caller-selectable.
/// The agent process receives NO API bearer token or cloud-provider credentials.
/// </summary>
public sealed class M1ProcessRunner :
    IM1ExecutionRunner, IM1ProgressReporter, IM1ResultReader
{
    private readonly string _script;
    private readonly string _root;

    public M1ProcessRunner(string? repositoryRoot)
    {
        if (!OperatingSystem.IsLinux() || string.IsNullOrWhiteSpace(repositoryRoot))
            throw new ExecutionUnavailable();
        _root = Path.GetFullPath(repositoryRoot);
        if ((File.GetAttributes(_root) & FileAttributes.ReparsePoint) != 0)
            throw new ExecutionUnavailable();
        _script = Path.Combine(_root, "tools", "m1", "approved_gateway_coding.py");
        if (!File.Exists(_script) ||
            (File.GetAttributes(_script) & FileAttributes.ReparsePoint) != 0)
            throw new ExecutionUnavailable();
    }

    private static string[] Binding(RunItem run) =>
    [
        "--control-task-id", run.TaskId,
        "--control-run-id", run.Id,
        "--control-plan-version", run.PlanVersion.ToString(CultureInfo.InvariantCulture),
        "--control-plan-hash", run.PlanHash
    ];

    private ProcessStartInfo Command(params string[] args)
    {
        var info = new ProcessStartInfo("python3")
        {
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Do not pass control-plane credentials or cloud tokens into an
        // autonomous execution process. Rootless Podman still uses these
        // ordinary login-session environment values.
        var inherited = Environment.GetEnvironmentVariables();
        info.Environment.Clear();
        foreach (var key in new[] {
            "PATH", "HOME", "USER", "LOGNAME", "LANG", "LC_ALL",
            "XDG_RUNTIME_DIR", "DBUS_SESSION_BUS_ADDRESS", "TMPDIR"
        })
        {
            if (inherited[key] is string value && value.Length is > 0 and < 4096)
                info.Environment[key] = value;
        }
        info.ArgumentList.Add(_script);
        foreach (var value in args)
            info.ArgumentList.Add(value);
        return info;
    }

    private static async Task<string> ReadSafeProgressAsync(
        StreamReader stdout, Func<string, Task> onStage)
    {
        var output = new StringBuilder();
        var stagesSeen = 0;
        string? line;
        while ((line = await stdout.ReadLineAsync()) is not null)
        {
            if (line.StartsWith("CA_M1_STAGE:", StringComparison.Ordinal))
            {
                var stage = line["CA_M1_STAGE:".Length..];
                // Bound cardinality and never publish arbitrary model content.
                if (!M1StageNames.IsAllowed(stage) || ++stagesSeen > 32)
                    throw new ExecutionUnavailable();
                try { await onStage(stage); }
                catch (Exception) { /* Optional telemetry must not abort coding. */ }
                continue;
            }
            if (output.Length + line.Length + 1 > 16000)
                throw new ExecutionUnavailable();
            output.AppendLine(line);
        }
        return output.ToString();
    }

    private async Task<(int Exit, string Output)> InvokeAsync(
        string[] arguments, string? approval,
        TimeSpan timeout, CancellationToken cancellation,
        Func<string, Task>? onStage = null)
    {
        using var process = new Process { StartInfo = Command(arguments) };
        try
        {
            if (!process.Start())
                throw new ExecutionUnavailable();
            var stdout = onStage is null
                ? process.StandardOutput.ReadToEndAsync()
                : ReadSafeProgressAsync(process.StandardOutput, onStage);
            var stderr = process.StandardError.ReadToEndAsync();
            if (approval is not null)
                await process.StandardInput.WriteLineAsync(approval);
            process.StandardInput.Close();
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timer.CancelAfter(timeout);
            await process.WaitForExitAsync(timer.Token);
            var output = await stdout;
            _ = await stderr; // Never log potentially sensitive child stderr.
            if (output.Length > 16000)
                throw new ExecutionUnavailable();
            return (process.ExitCode, output);
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or InvalidOperationException
                or System.ComponentModel.Win32Exception or IOException)
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                try { await process.WaitForExitAsync(CancellationToken.None); }
                catch (InvalidOperationException) { }
            }
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            throw new ExecutionUnavailable();
        }
    }

    public async Task<M1ExecutablePreview> PreviewAsync(
        RunItem run, CancellationToken cancellation)
    {
        var args = new[] { "--preview-json" }.Concat(Binding(run)).ToArray();
        var result = await InvokeAsync(args, null, TimeSpan.FromSeconds(120), cancellation);
        if (result.Exit != 0)
            throw new ExecutionUnavailable();
        try
        {
            using var parsed = JsonDocument.Parse(result.Output);
            var root = parsed.RootElement;
            if (root.GetProperty("schema").GetString() !=
                    "codingassistant-execution-preview-v1")
                throw new ExecutionUnavailable();
            var plan = root.GetProperty("plan");
            var hash = root.GetProperty("plan_hash").GetString() ?? "";
            VerifyExactBoundPlan(run, plan, hash);
            return new M1ExecutablePreview(hash, plan.Clone());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException
                                    or InvalidOperationException or FormatException)
        {
            throw new ExecutionUnavailable();
        }
    }

    public static void VerifyExactBoundPlan(
        RunItem run, JsonElement plan, string hash)
    {
        if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ExecutionUnavailable();
        try
        {
            var keys = new[] {
                "schema_version", "stage", "assignment", "fixture",
                "fixture_sha256", "manifest_sha256", "model", "model_digest",
                "image_id", "evaluator_image_id", "source_sha256",
                "writable_files", "gateway_method", "no_cloud",
                "no_git_remote", "control"
            };
            if (!plan.EnumerateObject().Select(p => p.Name)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(keys.OrderBy(x => x, StringComparer.Ordinal)))
                throw new ExecutionUnavailable();
            var control = plan.GetProperty("control");
            if (!control.EnumerateObject().Select(p => p.Name)
                    .OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(new[] {
                        "task_id", "run_id", "plan_version", "plan_hash"
                    }.OrderBy(x => x, StringComparer.Ordinal)))
                throw new ExecutionUnavailable();
            if (control.GetProperty("task_id").GetString() != run.TaskId ||
                control.GetProperty("run_id").GetString() != run.Id ||
                control.GetProperty("plan_version").GetInt32() != run.PlanVersion ||
                control.GetProperty("plan_hash").GetString() != run.PlanHash ||
                plan.GetProperty("schema_version").GetInt32() != 1 ||
                plan.GetProperty("stage").GetString() != "M1.12" ||
                plan.GetProperty("fixture").GetString() != "02-build-repair" ||
                plan.GetProperty("assignment").GetString() !=
                    "repair fixed C# compiler errors in disposable api/Program.cs" ||
                plan.GetProperty("model").GetString() != TaskStore.Model ||
                plan.GetProperty("gateway_method").GetString() != "codingassistant.coding.run" ||
                !plan.GetProperty("no_cloud").GetBoolean() ||
                !plan.GetProperty("no_git_remote").GetBoolean())
                throw new ExecutionUnavailable();
            var writable = plan.GetProperty("writable_files");
            if (writable.GetArrayLength() != 1 ||
                writable[0].GetString() != "api/Program.cs")
                throw new ExecutionUnavailable();
            foreach (var key in new[] {
                "fixture_sha256", "manifest_sha256", "model_digest",
                "image_id", "evaluator_image_id", "source_sha256"
            })
            {
                var field = plan.GetProperty(key).GetString();
                if (field is null || field.Length != 64 ||
                    field.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    throw new ExecutionUnavailable();
            }
            // Independently recalculate the Python sorted-key compact JSON
            // SHA-256 instead of trusting an unverified field in stdout.
            var computed = ComputeCanonicalHash(plan);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(computed)))
                throw new ExecutionUnavailable();
        }
        catch (Exception ex) when (
            ex is KeyNotFoundException or InvalidOperationException
                or FormatException or IndexOutOfRangeException)
        {
            throw new ExecutionUnavailable();
        }
    }

    public static string ComputeCanonicalHash(JsonElement plan)
    {
        using var bytes = new MemoryStream();
        using (var writer = new Utf8JsonWriter(bytes,
                   new JsonWriterOptions { Indented = false }))
            WriteCanonical(writer, plan);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()))
            .ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String: writer.WriteStringValue(value.GetString()); break;
            case JsonValueKind.Number: writer.WriteRawValue(value.GetRawText()); break;
            case JsonValueKind.True: writer.WriteBooleanValue(true); break;
            case JsonValueKind.False: writer.WriteBooleanValue(false); break;
            case JsonValueKind.Null: writer.WriteNullValue(); break;
            default: throw new ExecutionUnavailable();
        }
    }

    public Task<M1ExecutionOutcome> ExecuteAsync(
        RunItem run, string executionHash, CancellationToken cancellation) =>
        ExecuteWithProgressAsync(run, executionHash, _ => Task.CompletedTask, cancellation);

    public async Task<M1ExecutionOutcome> ExecuteWithProgressAsync(
        RunItem run, string executionHash,
        Func<string, Task> onStage, CancellationToken cancellation)
    {
        var args = new[] { "--approval-stdin", "--request-id", run.Id }
            .Concat(Binding(run)).ToArray();
        var result = await InvokeAsync(args, "APPROVE " + executionHash,
                                       TimeSpan.FromMinutes(40), cancellation,
                                       onStage);
        // The M1.12 runner only returns zero AFTER a separate offline .NET
        // evaluator has declared VERIFIED and a durable ledger is committed.
        return result.Exit == 0 &&
               result.Output.Contains("M1.12 GATEWAY_APPROVED_CODING_PASS",
                                      StringComparison.Ordinal)
            ? M1ExecutionOutcome.Verified
            : M1ExecutionOutcome.Uncertain;
    }

    /// <summary>
    /// Read the host-owned immutable result ledger using a bounded, fixed
    /// Python inspection command. Never starts an agent, model or evaluator.
    /// Validates M2 run ID and exact M1 executable hash before rendering.
    /// Can work even when current host execution is disabled.
    /// </summary>
    public async Task<M1RunResult?> ReadResultAsync(
        string runId, string expectedPlanHash, CancellationToken cancellation)
    {
        ExecutionStore.CheckId(runId);
        if (!Regex.IsMatch(expectedPlanHash, "^[0-9a-f]{64}$",
                           RegexOptions.CultureInvariant))
            throw new ExecutionUnavailable();
        var result = await InvokeAsync(
            ["--result-by-request-id", runId], null,
            TimeSpan.FromSeconds(15), cancellation);
        if (result.Exit != 0)
            throw new ExecutionUnavailable();
        return ParseResult(result.Output, runId, expectedPlanHash);
    }

    public static M1RunResult? ParseResult(
        string raw, string runId, string expectedPlanHash)
    {
        try
        {
            using var parsed = JsonDocument.Parse(raw);
            var root = parsed.RootElement;
            if (root.GetProperty("schema").GetString() !=
                    "codingassistant-run-result-v1")
                throw new ExecutionUnavailable();
            if (!root.GetProperty("available").GetBoolean())
                return null;
            var id = root.GetProperty("runId").GetString() ?? "";
            var hash = root.GetProperty("planHash").GetString() ?? "";
            var status = root.GetProperty("status").GetString() ?? "";
            var phase = root.GetProperty("phase").GetString() ?? "";
            var reason = root.GetProperty("reasonCode").ValueKind ==
                         JsonValueKind.Null ? null :
                         root.GetProperty("reasonCode").GetString();
            var evaluator = root.GetProperty("evaluatorVerified");
            var verified = evaluator.ValueKind == JsonValueKind.Null ? (bool?)null :
                evaluator.GetBoolean();
            var changed = root.GetProperty("sourceChanged");
            var sourceChanged = changed.ValueKind == JsonValueKind.Null ? (bool?)null :
                changed.GetBoolean();
            var calls = root.GetProperty("inferenceRequests");
            var count = calls.ValueKind == JsonValueKind.Null ? (int?)null :
                calls.GetInt32();
            var patch = root.GetProperty("patch").GetString() ?? "";
            var omitted = root.GetProperty("patchOmitted").GetBoolean();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(id), Encoding.ASCII.GetBytes(runId)) ||
                !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(hash),
                    Encoding.ASCII.GetBytes(expectedPlanHash)) ||
                status is not ("GATEWAY_APPROVED_CODING_PASS" or "BLOCKED" or "NOT_APPROVED") ||
                !Regex.IsMatch(phase, "^[a-z_]{1,45}$") ||
                (reason is not null && !Regex.IsMatch(
                    reason, "^[A-Za-z_]{1,64}$")) ||
                count is < 0 or > 10 ||
                patch.Length > 7000 ||
                (patch.Length > 0 && !patch.StartsWith(
                    "--- a/api/Program.cs\\n", StringComparison.Ordinal)) ||
                (status != "GATEWAY_APPROVED_CODING_PASS" &&
                    (patch.Length != 0 || omitted)) ||
                (status == "GATEWAY_APPROVED_CODING_PASS" &&
                    verified is not true))
                throw new ExecutionUnavailable();
            return new M1RunResult(id, hash, status, phase,
                reason, verified, sourceChanged, count, patch, omitted);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or
                                   InvalidOperationException or FormatException or
                                   ArgumentException or OverflowException)
        {
            throw new ExecutionUnavailable();
        }
    }

    public async Task<bool> ReconcileAsync(
        string requestId, CancellationToken cancellation)
    {
        ExecutionStore.CheckId(requestId);
        var result = await InvokeAsync(
            ["--recover-request-id", requestId], null,
            TimeSpan.FromSeconds(90), cancellation);
        if (result.Exit != 0)
            return false;
        try
        {
            using var doc = JsonDocument.Parse(result.Output);
            var state = doc.RootElement.GetProperty("result").GetString();
            return state is "NOT_FOUND" or "INTERRUPTED_CLEAN" or "ALREADY_TERMINAL";
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>
/// Admission commits before launching a child. No recovery ever auto-replays.
/// On uncertain cleanup the next task remains blocked until explicit
/// model-free reconciliation confirms the labeled M1 container is gone.
/// </summary>
public sealed class ExecutionCoordinator(
    RunStore runs, ExecutionStore store, IM1ExecutionRunner runner,
    RunNotifier notifier)
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _active = new();

    private async Task Publish(ExecutionChange change)
    {
        if (change.NewEvent is not null)
            await notifier.PublishAsync(change.NewEvent);
    }

    public async Task<ExecutionPlanView> PrepareAsync(
        string runId, CancellationToken cancellation)
    {
        var run = runs.Get(runId);
        if (run.State is not ("AwaitingExecutionAuthorization" or "Previewed") ||
            !run.PlanCurrent)
            throw new StoreFailure("INVALID_STATE");
        var preview = await runner.PreviewAsync(run, cancellation);
        // Revalidate the exact scope/sha on the trusted side immediately
        // before recording the preview, never from user-supplied JSON.
        M1ProcessRunner.VerifyExactBoundPlan(run, preview.Plan, preview.PlanHash);
        var change = store.RecordPreview(runId, preview.PlanHash, preview.Plan);
        await Publish(change);
        return change.Plan;
    }

    public async Task<ExecutionPlanView> AuthorizeAsync(
        string runId, AuthorizeExecutionRequest request)
    {
        var change = store.Authorize(runId, request);
        await Publish(change);
        return change.Plan;
    }

    public async Task<ExecutionPlanView> StartAsync(string runId)
    {
        var run = runs.Get(runId);
        var approved = store.Get(runId);
        if (!run.PlanCurrent || approved.State != "Authorized")
            throw new StoreFailure("INVALID_STATE");
        M1ProcessRunner.VerifyExactBoundPlan(run, approved.Plan, approved.PlanHash);
        var change = store.Admit(runId);
        var cancellation = new CancellationTokenSource();
        if (!_active.TryAdd(runId, cancellation))
        {
            // Fail closed; a committed admission is never retried.
            await Publish(store.Move(runId, "Starting", "RecoveryBlocked",
                                     "ExecutionRecoveryRequired"));
            throw new StoreFailure("EXECUTION_BUSY");
        }
        await Publish(change);
        _ = Task.Run(() => DriveAsync(run, approved.PlanHash, cancellation));
        return change.Plan;
    }

    private async Task DriveAsync(RunItem run, string hash,
        CancellationTokenSource cancellation)
    {
        try
        {
            if (cancellation.IsCancellationRequested)
            {
                await ReconcileCancellation(run.Id);
                return;
            }
            await Publish(store.Move(run.Id, "Starting", "Running",
                                     "ExecutionStarted"));
            var result = runner is IM1ProgressReporter progress
                ? await progress.ExecuteWithProgressAsync(run, hash,
                    async stage =>
                    {
                        var stageChange = store.RecordStage(run.Id, stage);
                        if (stageChange is not null)
                            await Publish(stageChange);
                    }, cancellation.Token)
                : await runner.ExecuteAsync(run, hash, cancellation.Token);
            if (cancellation.IsCancellationRequested)
                await ReconcileCancellation(run.Id);
            else if (result == M1ExecutionOutcome.Verified)
                await Publish(store.Move(run.Id, "Running", "Succeeded",
                                         "ExecutionVerified"));
            else
                await Publish(store.Move(run.Id, "Running", "RecoveryBlocked",
                                         "ExecutionRecoveryRequired"));
        }
        catch (OperationCanceledException)
        {
            await ReconcileCancellation(run.Id);
        }
        catch (Exception)
        {
            try
            {
                var state = store.Get(run.Id).State;
                if (state is "Running" or "Starting" or "Cancelling")
                    await Publish(store.Move(run.Id, state, "RecoveryBlocked",
                                             "ExecutionRecoveryRequired"));
            }
            catch (Exception) { /* Persisted admission is fail-closed on restart. */ }
        }
        finally
        {
            _active.TryRemove(run.Id, out _);
            cancellation.Dispose();
        }
    }

    private async Task ReconcileCancellation(string runId)
    {
        bool clean;
        try { clean = await runner.ReconcileAsync(runId, CancellationToken.None); }
        catch (Exception) { clean = false; }
        try
        {
            var state = store.Get(runId).State;
            if (state is "Starting" or "Running" or "Cancelling")
                await Publish(store.Move(runId, state,
                    clean ? "Cancelled" : "RecoveryBlocked",
                    clean ? "ExecutionCancelled" : "ExecutionRecoveryRequired"));
        }
        catch (StoreFailure) { }
    }

    public async Task<ExecutionPlanView> CancelAsync(string runId)
    {
        var item = store.Get(runId);
        if (item.State is "Previewed" or "Authorized")
        {
            var change = store.CancelBeforeStart(runId);
            await Publish(change);
            return change.Plan;
        }
        if (item.State is "Starting" or "Running")
        {
            var change = store.Move(runId, item.State, "Cancelling",
                                    "ExecutionCancellationRequested");
            await Publish(change);
            if (_active.TryGetValue(runId, out var cancellation))
                cancellation.Cancel();
            return change.Plan;
        }
        if (item.State is "Cancelling" or "Cancelled")
            return item;
        throw new StoreFailure("INVALID_STATE");
    }

    public async Task<ExecutionPlanView> RecoverAsync(string runId,
        CancellationToken cancellation)
    {
        var item = store.Get(runId);
        if (item.State != "RecoveryBlocked")
            throw new StoreFailure("INVALID_STATE");
        if (await runner.ReconcileAsync(runId, cancellation))
        {
            var change = store.Move(runId, "RecoveryBlocked", "Interrupted",
                                    "ExecutionInterrupted");
            await Publish(change);
            return change.Plan;
        }
        return item; // Still blocked. Never infer clean state or restart.
    }
}
