using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CodingAssistant.Api;

/// <summary>
/// Pure, bounded import of a commit-pinned GitHub ZIP archive. This parser
/// never extracts files to disk or executes source. Imported paths are data,
/// not executable instructions, command arguments or filesystem targets.
/// </summary>
public sealed record AtlasArchiveContents(
    IReadOnlyList<EngineeringSourceFile> Files, int ExcludedFiles,
    IReadOnlyList<string> Diagnostics);

public static class CodeAtlasRepositoryArchive
{
    public const int MaxArchiveBytes = 16 * 1024 * 1024;
    public const int MaxSourceBytes = 10 * 1024 * 1024;
    public const int MaxFileBytes = 192 * 1024;
    public const int MaxIndexedFiles = 750;
    public const int MaxEntries = 15000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> Extensions = new(
        [".cs", ".csproj", ".sln", ".slnx", ".props", ".targets",
         ".ts", ".tsx", ".js", ".jsx", ".mjs", ".cjs", ".py",
         ".java", ".go", ".rs", ".cpp", ".cc", ".h", ".hpp",
         ".json", ".html", ".css", ".sql", ".xml", ".razor",
         ".cshtml", ".vue", ".fs", ".fsproj"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ExcludedDirectories = new(
        ["node_modules", ".git", ".svn", ".hg", "bin", "obj",
         "dist", "build", ".next", ".nuxt", "coverage",
         "packages", ".venv", "venv", "__pycache__"],
        StringComparer.OrdinalIgnoreCase);

    private static bool SafePath(string path)
    {
        if (path.Length is < 1 or > 240 ||
            path.StartsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.Any(char.IsControl)) return false;
        var components = path.Split('/');
        return components.All(segment =>
            segment.Length is > 0 and <= 100 && segment is not ("." or ".."));
    }

    private static string Sha(string value) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static AtlasArchiveContents Parse(byte[] archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Length is < 22 or > MaxArchiveBytes)
            throw new StoreFailure("ATLAS_ARCHIVE_SIZE_INVALID");
        using var stream = new MemoryStream(archive, false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, false);
        if (zip.Entries.Count is < 1 or > MaxEntries)
            throw new StoreFailure("ATLAS_ARCHIVE_ENTRY_LIMIT");

        var files = new List<EngineeringSourceFile>();
        var diagnostics = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        var excluded = 0;
        string? archiveRoot = null;
        foreach (var entry in zip.Entries)
        {
            // Unix mode symlinks and other non-regular entries must never
            // be interpreted as source files, even though no extraction occurs.
            var unixKind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixKind == 0xA000)
                throw new StoreFailure("ATLAS_ARCHIVE_SYMLINK_REJECTED");
            if (unixKind != 0 && unixKind is not (0x8000 or 0x4000))
                throw new StoreFailure("ATLAS_ARCHIVE_ENTRY_INVALID");

            var raw = entry.FullName;
            var separator = raw.IndexOf('/');
            if (separator <= 0 ||
                raw.StartsWith('/') || raw.Contains('\\') ||
                raw.Any(char.IsControl))
                throw new StoreFailure("ATLAS_ARCHIVE_PATH_INVALID");
            var root = raw[..separator];
            if (root is "." or ".." || root.Contains(':'))
                throw new StoreFailure("ATLAS_ARCHIVE_PATH_INVALID");
            if (archiveRoot is null) archiveRoot = root;
            else if (!string.Equals(root, archiveRoot, StringComparison.Ordinal))
                throw new StoreFailure("ATLAS_ARCHIVE_ROOT_MISMATCH");

            if (raw.EndsWith('/')) continue;
            var path = raw[(separator + 1)..];
            if (!SafePath(path))
                throw new StoreFailure("ATLAS_ARCHIVE_PATH_INVALID");
            var parts = path.Split('/');
            if (parts.SkipLast(1).Any(ExcludedDirectories.Contains) ||
                !Extensions.Contains(Path.GetExtension(path)))
            {
                excluded++;
                continue;
            }
            if (!seen.Add(path))
                throw new StoreFailure("ATLAS_ARCHIVE_DUPLICATE_PATH");
            if (files.Count >= MaxIndexedFiles ||
                entry.Length < 0 || entry.Length > MaxFileBytes ||
                total + entry.Length > MaxSourceBytes)
            {
                excluded++;
                continue;
            }
            using var input = entry.Open();
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int size;
            while ((size = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (bytes.Length + size > MaxFileBytes ||
                    total + bytes.Length + size > MaxSourceBytes)
                    throw new StoreFailure("ATLAS_ARCHIVE_EXPANSION_LIMIT");
                bytes.Write(buffer, 0, size);
            }
            string content;
            try { content = StrictUtf8.GetString(bytes.ToArray()); }
            catch (DecoderFallbackException)
            {
                excluded++;
                continue;
            }
            total += bytes.Length;
            files.Add(new(path, "Imported GitHub source", content,
                Sha(content), "GitHubCommit", ""));
        }

        if (files.Count == 0)
            diagnostics.Add("NO_INDEXABLE_SOURCE");
        if (excluded > 0)
            diagnostics.Add("EXCLUDED_SOURCE_FILES:" + excluded);
        return new(files.AsReadOnly(), excluded, diagnostics.AsReadOnly());
    }
}
