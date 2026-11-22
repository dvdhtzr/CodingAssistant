using System.Diagnostics;
using System.Text.Json;

namespace CodingAssistant.Api;

/// <summary>
/// One explicit owner click runs an already-saved, approved Static Web project
/// through the existing Windows Docker-only smoke lane. No file download or
/// arbitrary command/path selection is exposed through the API.
/// This is NOT complete independent verification.
/// </summary>
public sealed record StartSourceSmokeRequest(
    string? ScopeSha256, bool AcceptContainerOnlyBrowserSandbox);

public sealed record SourceSmokeResponse(
    string EngineeringRunId, string ScopeSha256, string Status,
    int Passed, int Required, bool ContainerRemoved,
    string BrowserSandbox, string? Reason,
    IReadOnlyDictionary<string, bool> Checks,
    bool Verified);

public sealed record LocalSourceSmokeResult(
    string Status, int Passed, int Required, bool ContainerRemoved,
    string BrowserSandbox, string? Reason,
    IReadOnlyDictionary<string, bool> Checks);

public interface ILocalSourceSmokeRunner
{
    Task<LocalSourceSmokeResult> RunAsync(byte[] sourceZip,
        CancellationToken cancellationToken);
}

public sealed class WindowsDockerSourceSmokeRunner(string? repositoryRoot = null)
    : ILocalSourceSmokeRunner
{
    private static readonly string[] RequiredChecks =
    [
        "exact_source_hashes", "non_root", "no_host_mounts",
        "root_write_denied", "network_denied", "javascript_syntax",
        "local_assets_linked", "browser_rendered",
        "source_integrity_unchanged"
    ];

    private static string? LocateScript(string? suppliedRoot)
    {
        // A trusted administrator may set repositoryRoot. Otherwise locate
        // this app's installed source tree; never accept a path from the
        // browser, an AI model, an archive, or a generated file.
        var starts = new[] { suppliedRoot, Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory };
        foreach (var start in starts)
        {
            if (string.IsNullOrWhiteSpace(start)) continue;
            var current = new DirectoryInfo(Path.GetFullPath(start));
            for (var i = 0; i < 8 && current is not null; i++,
                 current = current.Parent)
            {
                var file = Path.Combine(current.FullName, "tools", "m4",
                    "smoke_generated_zip.mjs");
                if (File.Exists(file) &&
                    (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    return file;
            }
        }
        return null;
    }

    private static LocalSourceSmokeResult Blocked(string code) =>
        new("BLOCKED", 0, 9, false, "DISABLED_CONTAINER_ONLY",
            code, new Dictionary<string, bool>());

    public async Task<LocalSourceSmokeResult> RunAsync(byte[] sourceZip,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            return Blocked("WINDOWS_DOCKER_ONLY");
        var script = LocateScript(repositoryRoot);
        if (script is null) return Blocked("RUNNER_SOURCE_NOT_FOUND");
        if (sourceZip.Length is < 22 or > 2 * 1024 * 1024)
            return Blocked("SOURCE_ZIP_BOUNDS_INVALID");

        var privateDir = Path.Combine(Path.GetTempPath(),
            "ca-smoke-api-" + Guid.NewGuid().ToString("N"));
        string? originalFailure = null;
        LocalSourceSmokeResult? outcome = null;
        try
        {
            Directory.CreateDirectory(privateDir);
            var zipFile = Path.Combine(privateDir, "source.zip");
            await File.WriteAllBytesAsync(zipFile, sourceZip, cancellationToken);

            var start = new ProcessStartInfo("node")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(script);
            start.ArgumentList.Add("--zip");
            start.ArgumentList.Add(zipFile);
            start.ArgumentList.Add("--json");

            using var process = new Process { StartInfo = start };
            if (!process.Start()) return Blocked("RUNNER_START_FAILED");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(130));
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                // Discard stderr. It can contain sensitive host information.
                var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var output = await outputTask;
                _ = await errorTask;
                if (output.Length is < 2 or > 16000)
                {
                    outcome = Blocked("RUNNER_RESPONSE_INVALID");
                }
                else
                {
                    outcome = ParseRunnerResult(output);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                originalFailure = "RUNNER_TIMED_OUT";
            }
            finally
            {
                if (!process.HasExited)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch (InvalidOperationException) { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            originalFailure = "RUNNER_CANCELLED";
        }
        catch (Exception)
        {
            // No raw error message or private source path crosses to UI.
            originalFailure = "RUNNER_UNAVAILABLE";
        }
        finally
        {
            try
            {
                if (Directory.Exists(privateDir))
                    Directory.Delete(privateDir, recursive: true);
            }
            catch
            {
                originalFailure = "TEMP_SOURCE_CLEANUP_UNCONFIRMED";
            }
        }
        return originalFailure is not null ? Blocked(originalFailure) :
            outcome ?? Blocked("RUNNER_RESULT_UNAVAILABLE");
    }

    public static LocalSourceSmokeResult ParseRunnerResult(string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var status = root.GetProperty("status").GetString() ?? "";
            if (status is not ("BLOCKED" or "SMOKE_TEST_FAILED" or
                "SMOKE_PASS_NOT_VERIFIED") ||
                root.GetProperty("generatedArtifactVerified").GetBoolean() ||
                root.GetProperty("independentFunctionalVerification").GetBoolean() ||
                root.GetProperty("browserSandbox").GetString() !=
                    "DISABLED_CONTAINER_ONLY")
                throw new JsonException("Invalid smoke contract");

            var passed = root.GetProperty("passed").GetInt32();
            var required = root.GetProperty("required").GetInt32();
            var cleaned = root.GetProperty("containerRemoved").GetBoolean();
            var checkValues = root.GetProperty("checks");
            var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
            if (checkValues.ValueKind == JsonValueKind.Object)
                foreach (var item in checkValues.EnumerateObject())
                {
                    if (!RequiredChecks.Contains(item.Name, StringComparer.Ordinal))
                        throw new JsonException("Unknown check");
                    checks.Add(item.Name, item.Value.GetBoolean());
                }
            else if (checkValues.ValueKind != JsonValueKind.Null)
                throw new JsonException("Invalid checks");
            var reasonElement = root.GetProperty("reason");
            var reason = reasonElement.ValueKind == JsonValueKind.String
                ? reasonElement.GetString() : null;
            if (reason is not null &&
                (reason.Length > 140 || reason.Any(char.IsControl)))
                throw new JsonException("Unsafe reason");
            if (required != 9 || passed is < 0 or > 9 ||
                (checks.Count > 0 &&
                    (checks.Count != 9 || checks.Values.Count(x => x) != passed)) ||
                (status == "SMOKE_PASS_NOT_VERIFIED" &&
                    (!cleaned || checks.Count != 9 || passed != 9)) ||
                (status == "SMOKE_TEST_FAILED" &&
                    (!cleaned || checks.Count != 9 || passed == 9)))
                throw new JsonException("Contradictory smoke result");
            // The frontend always receives an honest smoke-only status.
            return new LocalSourceSmokeResult(status, passed, required, cleaned,
                "DISABLED_CONTAINER_ONLY", reason, checks);
        }
        catch (Exception)
        {
            return Blocked("RUNNER_RESPONSE_INVALID");
        }
    }
}

public sealed class SourceSmokeCoordinator(
    TaskStore tasks, EngineeringRunStore engineering,
    ImplementationPlanStore plans, InterviewStore interviews,
    ILocalSourceSmokeRunner runner)
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public static void RequireOwnerConsent(StartSourceSmokeRequest request,
        StaticWebVerificationScopePreview scope)
    {
        if (!request.AcceptContainerOnlyBrowserSandbox)
            throw new StoreFailure("SMOKE_ISOLATION_ACK_REQUIRED");
        if (scope.Status != "SCOPE_CANDIDATE" ||
            scope.ExecutionEnabled ||
            scope.VerificationStatus != "GENERATED_NOT_TESTED" ||
            string.IsNullOrEmpty(scope.ScopeSha256) ||
            request.ScopeSha256 != scope.ScopeSha256)
            throw new StoreFailure("SOURCE_SCOPE_CHANGED");
    }

    private StaticWebVerificationScopePreview ReadScope(
        string taskId, string runId)
    {
        var source = engineering.Get(taskId, runId);
        var latest = engineering.Latest(taskId);
        return StaticWebVerificationScope.Preview(
            source, plans.Read(taskId).Plan,
            interviews.Read(taskId).Specification,
            DraftArchitecturePolicy.Readiness(tasks, interviews, taskId),
            latest?.Job.Id == runId);
    }

    public async Task<SourceSmokeResponse> RunAsync(
        string taskId, string runId, StartSourceSmokeRequest request,
        CancellationToken cancellationToken)
    {
        if (!await _oneAtATime.WaitAsync(0, cancellationToken))
            throw new StoreFailure("SMOKE_BUSY");
        try
        {
            var scope = ReadScope(taskId, runId);
            RequireOwnerConsent(request, scope);
            // Recheck immediately before handing source to the local runner.
            var checkedScope = ReadScope(taskId, runId);
            RequireOwnerConsent(request, checkedScope);
            var snapshot = engineering.Get(taskId, runId);
            if (snapshot.Job.State != "Generated" ||
                checkedScope.SourceFingerprint !=
                    QaReviewStore.Fingerprint(snapshot.Files))
                throw new StoreFailure("SOURCE_CHANGED");
            // Internal, short-lived bytes only. No browser download.
            var sourceZip = engineering.Zip(taskId, runId);
            var report = await runner.RunAsync(sourceZip, cancellationToken);
            return new SourceSmokeResponse(runId, checkedScope.ScopeSha256!,
                report.Status, report.Passed, report.Required,
                report.ContainerRemoved, report.BrowserSandbox,
                report.Reason, report.Checks, Verified: false);
        }
        finally { _oneAtATime.Release(); }
    }
}
