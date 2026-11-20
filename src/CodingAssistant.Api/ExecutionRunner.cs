using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace CodingAssistant.Api;

public sealed record M1ExecutablePreview(string PlanHash, JsonElement Plan);
public enum M1ExecutionOutcome { Verified, Uncertain }

public interface IM1ExecutionRunner
{
    Task<M1ExecutablePreview> PreviewAsync(RunItem run, CancellationToken cancellation);
    Task<M1ExecutionOutcome> ExecuteAsync(
        RunItem run, string executionHash, CancellationToken cancellation);
    Task<bool> ReconcileAsync(string requestId, CancellationToken cancellation);
}

public sealed class ExecutionUnavailable : Exception;

/// <summary>
/// Thin, non-shell Linux adapter to the already-qualified fixed M1.12 runner.
/// No arbitrary project, model, script, mount or worktree is caller-selectable.
/// The agent process receives NO API bearer token or cloud-provider credentials.
/// </summary>
public sealed class M1ProcessRunner : IM1ExecutionRunner
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

    private async Task<(int Exit, string Output)> InvokeAsync(
        string[] arguments, string? approval,
        TimeSpan timeout, CancellationToken cancellation)
    {
        using var process = new Process { StartInfo = Command(arguments) };
        try
        {
            if (!process.Start())
                throw new ExecutionUnavailable();
            var stdout = process.StandardOutput.ReadToEndAsync();
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
            var control = plan.GetProperty("control");
            if (control.GetProperty("task_id").GetString() != run.TaskId ||
                control.GetProperty("run_id").GetString() != run.Id ||
                control.GetProperty("plan_version").GetInt32() != run.PlanVersion ||
                control.GetProperty("plan_hash").GetString() != run.PlanHash ||
                plan.GetProperty("schema_version").GetInt32() != 1 ||
                plan.GetProperty("stage").GetString() != "M1.12" ||
                plan.GetProperty("fixture").GetString() != "02-build-repair" ||
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
            using var bytes = new MemoryStream();
            using (var writer = new Utf8JsonWriter(bytes,
                       new JsonWriterOptions { Indented = false }))
                WriteCanonical(writer, plan);
            var computed = Convert.ToHexString(SHA256.HashData(bytes.ToArray()))
                .ToLowerInvariant();
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

    public async Task<M1ExecutionOutcome> ExecuteAsync(
        RunItem run, string executionHash, CancellationToken cancellation)
    {
        var args = new[] { "--approval-stdin", "--request-id", run.Id }
            .Concat(Binding(run)).ToArray();
        var result = await InvokeAsync(args, "APPROVE " + executionHash,
                                       TimeSpan.FromMinutes(40), cancellation);
        // The M1.12 runner only returns zero AFTER a separate offline .NET
        // evaluator has declared VERIFIED and a durable ledger is committed.
        return result.Exit == 0 &&
               result.Output.Contains("M1.12 GATEWAY_APPROVED_CODING_PASS",
                                      StringComparison.Ordinal)
            ? M1ExecutionOutcome.Verified
            : M1ExecutionOutcome.Uncertain;
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
        if (run.State != "AwaitingExecutionAuthorization" || !run.PlanCurrent)
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
            var result = await runner.ExecuteAsync(run, hash, cancellation.Token);
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
