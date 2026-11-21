using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

/// <summary>
/// Keeps the ignored Vite output in sync with the checked-out React source
/// when the Windows debug API is started directly by Visual Studio F5.
/// No build takes place in published/Release builds or on Linux.
/// This is a trusted developer checkout build, never generated project code.
/// </summary>
public static class WindowsDevelopmentFrontend
{
    public const string UiVersion = "windows-auto-session-v1";
    private const string FingerprintFile = ".codingassistant-dev-ui.sha256";
    private static readonly Regex BundleSource = new(
        "src=\"/assets/([^\"/]+\\.js)\"",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static string? _preparedWebRoot = null;
    public static string? PreparedWebRoot => _preparedWebRoot;

    public static string? EnsureCurrent()
    {
#if !DEBUG
        return null;
#else
        if (!OperatingSystem.IsWindows())
            return null;

        var checkout = FindCheckout();
        if (checkout is null)
            return null; // Published binary: use its packaged wwwroot.

        var webProject = Path.Combine(checkout, "src", "CodingAssistant.Web");
        var webRoot = Path.Combine(checkout, "src", "CodingAssistant.Api", "wwwroot");
        var sourceSha = Fingerprint(webProject);
        if (!HasCurrentFrontend(webRoot, sourceSha))
        {
            Console.WriteLine(
                "CodingAssistant: building current Mission Control UI for Windows F5...");
            var vite = Path.Combine(webProject, "node_modules", ".bin", "vite.cmd");
            if (!File.Exists(vite))
                RunNpm(webProject, "ci", "--prefer-offline",
                    "--no-audit", "--no-fund");
            RunNpm(webProject, "run", "build");

            // Never mark a stale/broken frontend as successful.
            if (!HasAutoSessionBundle(webRoot))
                throw new InvalidOperationException(
                    "Mission Control build did not produce the current Windows " +
                    "automatic-login UI. Startup stopped rather than serving stale assets.");
            Directory.CreateDirectory(webRoot);
            var manifest = Path.Combine(webRoot, FingerprintFile);
            var staging = manifest + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(staging, sourceSha + "\n", Encoding.ASCII);
                File.Move(staging, manifest, overwrite: true);
            }
            finally
            {
                if (File.Exists(staging))
                    File.Delete(staging);
            }
        }

        _preparedWebRoot = webRoot;
        return webRoot;
#endif
    }

    private static string? FindCheckout()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(),
                                      AppContext.BaseDirectory })
        {
            DirectoryInfo? ancestor = new DirectoryInfo(Path.GetFullPath(start));
            for (var i = 0; ancestor is not null && i < 9; i++,
                     ancestor = ancestor.Parent)
            {
                var path = ancestor.FullName;
                if (File.Exists(Path.Combine(path, "CodingAssistant.sln")) &&
                    File.Exists(Path.Combine(path, "src", "CodingAssistant.Web",
                        "package.json")) &&
                    File.Exists(Path.Combine(path, "src", "CodingAssistant.Api",
                        "CodingAssistant.Api.csproj")))
                    return path;
            }
        }
        return null;
    }

    private static string Fingerprint(string webProject)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = Directory.EnumerateFiles(webProject, "*",
            SearchOption.TopDirectoryOnly)
            .Where(f => Path.GetFileName(f) is "package.json" or
                "package-lock.json" or "index.html" or "vite.config.ts" or
                "tsconfig.json" or "tsconfig.node.json")
            .Concat(Directory.EnumerateFiles(Path.Combine(webProject, "src"),
                "*", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal);
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(webProject, file)
                .Replace('\\', '/');
            incremental.AppendData(Encoding.UTF8.GetBytes(relative + "\n"));
            incremental.AppendData(File.ReadAllBytes(file));
        }
        incremental.AppendData(Encoding.UTF8.GetBytes(UiVersion));
        return Convert.ToHexString(incremental.GetHashAndReset()).ToLowerInvariant();
    }

    public static bool HasAutoSessionBundle(string webRoot)
    {
        var indexPath = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexPath)) return false;
        var index = File.ReadAllText(indexPath);
        var match = BundleSource.Match(index);
        if (!match.Success) return false;
        var bundle = Path.Combine(webRoot, "assets", match.Groups[1].Value);
        return File.Exists(bundle) &&
            File.ReadAllText(bundle).Contains("X-CodingAssistant-Client",
                StringComparison.Ordinal);
    }

    private static bool HasCurrentFrontend(string root, string fingerprint)
    {
        var marker = Path.Combine(root, FingerprintFile);
        return File.Exists(marker) &&
            string.Equals(File.ReadAllText(marker).Trim(), fingerprint,
                StringComparison.Ordinal) &&
            HasAutoSessionBundle(root);
    }

    private static void RunNpm(string webProject, params string[] arguments)
    {
        var processInfo = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = webProject,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        processInfo.ArgumentList.Add("/d");
        processInfo.ArgumentList.Add("/c");
        processInfo.ArgumentList.Add("npm.cmd");
        foreach (var argument in arguments)
            processInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(processInfo) ??
                throw new InvalidOperationException("Could not start npm");
            if (!process.WaitForExit(180_000))
            {
                process.Kill(entireProcessTree: true);
                throw new InvalidOperationException(
                    "Windows Mission Control frontend build timed out.");
            }
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    "Windows Mission Control frontend build failed. " +
                    "Correct the npm build errors instead of using an older UI.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException(
                "Node.js/npm is required to build the Windows debug UI. " +
                "Install Node.js or use an already-published CodingAssistant build.",
                error);
        }
    }
}
