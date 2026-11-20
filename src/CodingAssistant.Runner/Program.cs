using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using CodingAssistant.Api;
using CodingAssistant.Runner;

return await DemoRunner.MainAsync(args);

internal static class DemoRunner
{
    private static readonly Regex Id = new(
        "^[0-9a-f]{32}\\z", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    internal static async Task<int> MainAsync(string[] args)
    {
        if (args.Length is not (1 or 2) ||
            !(args[0] == "--demo" && args.Length == 1 ||
              args[0] is "--execute" or "--recover" && args.Length == 2))
        {
            Console.WriteLine(
                "Usage: dotnet run --project src/CodingAssistant.Runner -- --demo | --execute RUN_ID | --recover RUN_ID");
            return 2;
        }

        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("BLOCKED: actual coding is Dell Linux-only until independently qualified on other hosts");
            return 2;
        }

        var root = Directory.GetCurrentDirectory();
        var m1Script = Path.Combine(root, "tools", "m1", "approved_gateway_coding.py");
        if (!File.Exists(m1Script) ||
            (File.GetAttributes(m1Script) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(Path.Combine(root, "docs", "IMPLEMENTATION_PLAN.md")))
        {
            Console.WriteLine("BLOCKED: run from the unchanged CodingAssistant repository root");
            return 2;
        }

        var dir = ApiSettings.FromEnvironment().DataDirectory;
        var taskStore = new TaskStore(dir);
        var runs = new RunStore(taskStore);
        runs.Initialize();

        try
        {
            if (args[0] == "--demo")
            {
                var task = taskStore.CreateTask(new CreateTaskRequest(
                    FrozenDemo.Title, FrozenDemo.Objective,
                    Guid.NewGuid().ToString("N")));
                var plan = taskStore.DraftPlan(task.Id,
                    new DraftPlanRequest(FrozenDemo.Summary, 0));
                Console.WriteLine("M2 fixed disposable C# repair scope:");
                Console.WriteLine("  " + FrozenDemo.Objective);
                Console.WriteLine("  pinned local model: " + TaskStore.Model);
                Console.WriteLine("  M2 control-plan hash: " + plan.PlanHash);
                Console.WriteLine("Type the exact line to approve the planning record:");
                Console.WriteLine("APPROVE " + plan.PlanHash);
                Console.Write("Planning approval: ");
                var answer = Console.ReadLine();
                if (answer != "APPROVE " + plan.PlanHash)
                {
                    Console.WriteLine("NOT_APPROVED: no M1 coding or container started");
                    return 2;
                }
                taskStore.ApprovePlan(task.Id,
                    new ApprovePlanRequest(plan.Version, answer));
                var intent = runs.CreateIntent(task.Id,
                    new CreateRunIntentRequest(plan.Version, plan.PlanHash,
                        Guid.NewGuid().ToString("N")));
                Console.WriteLine("M2 durable run ID: " + intent.Run.Id);
                return await ExecuteAsync(
                    runs, intent.Run.Id, root, m1Script, dir);
            }

            var runId = args[1];
            if (!Id.IsMatch(runId))
            {
                Console.WriteLine("BLOCKED: invalid M2 run ID");
                return 2;
            }
            return args[0] == "--execute"
                ? await ExecuteAsync(runs, runId, root, m1Script, dir)
                : await RecoverAsync(runs, runId, root, m1Script, dir);
        }
        catch (Exception ex) when (
            ex is StoreFailure or InvalidOperationException or IOException or
                UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.WriteLine("M2 EXECUTION BRIDGE BLOCKED: " + ex.GetType().Name +
                              (ex is StoreFailure failure ? " " + failure.Code : ""));
            return 2;
        }
    }

    private static FileStream LockRun(string dir, string runId)
    {
        var path = Path.Combine(dir, "runner-locks");
        if (!Directory.Exists(path))
            Directory.CreateDirectory(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead |
                UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute)) != 0)
            throw new InvalidOperationException("Owner-only run lock directory required");
        var name = Path.Combine(path, runId + ".lock");
        if (File.Exists(name) &&
            (File.GetAttributes(name) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("Refuse symlinked run lock");
        return new FileStream(name, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
    }

    private static async Task<int> ExecuteAsync(
        RunStore runs, string runId, string root, string script, string dataDir)
    {
        using var guard = LockRun(dataDir, runId);
        // Commit the one-shot claim BEFORE starting the child. On a crash
        // the run stays Executing until explicit non-replaying recovery.
        var claimed = runs.ClaimFrozenDemo(runId);
        Console.WriteLine("M2 run claimed: " + claimed.Run.Id + " state=Executing");
        Console.WriteLine("This is a fixed, disposable fixture only. " +
                          "The M1 runner will require a SECOND, distinct " +
                          "exact plan-hash approval before any model inference.");
        await PulseAsync(runId);
        var psi = Python(script, root, "--prepare", "--request-id", runId);
        using var child = Process.Start(psi)
            ?? throw new InvalidOperationException("Could not start pinned M1 command");
        await child.WaitForExitAsync();
        // NEVER infer VERIFIED from exit code or guest stdout.
        var receipt = M1ReceiptVerifier.Inspect(root, runId);
        var outcome = Outcome(receipt);
        if (outcome is null)
        {
            Console.WriteLine("M2 EXECUTION_UNCERTAIN — explicit --recover required; no retry");
            return 2;
        }
        var finished = runs.FinishFrozenDemo(runId, outcome, receipt.M1RunId);
        Console.WriteLine("M2 state: " + finished.Run.State +
                          " (M1 evidence checked; never from process exit alone)");
        await PulseAsync(runId);
        return outcome == "Verified" ? 0 : 2;
    }

    private static async Task<int> RecoverAsync(
        RunStore runs, string runId, string root, string script, string dataDir)
    {
        // This file handle is exclusive for the complete execution lifetime.
        // If original owner process is alive, we refuse to reconcile it.
        using var guard = LockRun(dataDir, runId);
        var current = runs.Get(runId);
        if (current.State != "Executing")
        {
            Console.WriteLine("M2 run is not executing: " + current.State +
                              "; no model or state change");
            return 0;
        }

        var receipt = M1ReceiptVerifier.Inspect(root, runId);
        if (receipt.M1RunId is not null && Outcome(receipt) is null)
        {
            Console.WriteLine("M1 remains nonterminal; requesting its " +
                              "label-verified, model-free Podman reconciliation");
            var psi = Python(script, root, "--recover", receipt.M1RunId);
            using var cleanup = Process.Start(psi)
                ?? throw new InvalidOperationException("Cannot launch fixed recovery");
            await cleanup.WaitForExitAsync();
            if (cleanup.ExitCode != 0)
            {
                Console.WriteLine("RECOVERY_BLOCKED: M1 cleanup uncertain; no false terminal state");
                return 2;
            }
            receipt = M1ReceiptVerifier.Inspect(root, runId);
        }

        var outcome = Outcome(receipt);
        if (outcome is null)
        {
            Console.WriteLine("RECOVERY_BLOCKED: no verified M1 terminal state; no replay");
            return 2;
        }
        var finished = runs.FinishFrozenDemo(runId, outcome, receipt.M1RunId);
        Console.WriteLine("M2 RECOVERED: " + finished.Run.State +
                          "; no model request or repeat coding started");
        await PulseAsync(runId);
        return outcome == "Verified" ? 0 : 2;
    }

    private static string? Outcome(M1Receipt receipt) => receipt.State switch
    {
        "SUCCEEDED" when receipt.Verified => "Verified",
        "Untrusted" or "FAILED" => "Blocked",
        "DECLINED" => "Declined",
        "Missing" or "INTERRUPTED_CLEAN" => "Interrupted",
        _ => null
    };

    private static ProcessStartInfo Python(
        string script, string root, params string[] arguments)
    {
        var psi = new ProcessStartInfo("python3")
        {
            UseShellExecute = false,
            WorkingDirectory = root,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
        };
        psi.ArgumentList.Add(script);
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        // The M1 worker already restricts its own guest environment. Avoid
        // unnecessarily handing host cloud/Git credentials to Python itself.
        foreach (var key in psi.Environment.Keys.ToArray())
            if (key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase) ||
                key is "GITHUB_TOKEN" or "GH_TOKEN" or "AZURE_CLIENT_SECRET")
                psi.Environment.Remove(key);
        return psi;
    }

    private static async Task PulseAsync(string runId)
    {
        // Optional notification, never authoritative. If API is not
        // running the browser will replay the durable SQLite event later.
        var settings = ApiSettings.FromEnvironment();
        if (settings.OwnerToken.Length != 64) return;
        try
        {
            using var client = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false
            })
            {
                BaseAddress = new Uri(settings.BindUrl),
                Timeout = TimeSpan.FromSeconds(3)
            };
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.OwnerToken);
            using var result = await client.PostAsync(
                $"/api/runs/{runId}/refresh", content: null);
            // The bridge cannot infer success from a SignalR hint.
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }
}
