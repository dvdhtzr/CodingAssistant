using System.Security.Cryptography;
using System.Text;

namespace CodingAssistant.Api;

public sealed record SkillCatalogEntry(
    string Name, string Description, string Version, string SourceUrl,
    string SourceCommit, string ContentSha256, string License,
    string Compatibility, string Role, string Scope, int MaximumBytes,
    IReadOnlyList<string> RequiredToolCategories, string Status,
    bool Approved, bool Active, string Instructions);

/// <summary>
/// Fixed, embedded, repository-owned instruction candidates. Discovery is
/// read-only; no external skill import, file lookup, approval or activation.
/// Content is never passed to a model by this catalog.
/// </summary>
public static class SkillCatalog
{
    private const string SourceCommit = "e12ea7c3b86b03abef633ddc7003d0e284722b89";
    private const int MaximumBytes = 4096;
    private sealed record Pin(string Name, string Role, string Scope, string Hash);
    private static readonly Pin[] Pins =
    [
        new("requirements-discovery", "ProductOwner", "product-discovery",
            "8caf936b87f8c9518d62cebbb9f77661e2422a6720f54de6956a2c79fcfd26b3"),
        new("architecture-tradeoffs", "SolutionArchitect", "architecture-review",
            "220ea05969fc7e37dd98e74a323a648987e4e9390c6621859213562da828d5f8"),
        new("static-web-verification", "QaEngineer", "static-code-review",
            "a44462505576db34ccf61b57aa9952281828ced76402111389007c50f5f391e6")
    ];

    public static IReadOnlyList<SkillCatalogEntry> Discover() =>
        Pins.Select(Read).ToArray();

    private static SkillCatalogEntry Read(Pin pin)
    {
        if (!EngineeringAgentRoster.Get(pin.Role).AllowedSkillScopes.Contains(pin.Scope))
            throw new StoreFailure("SKILL_SCOPE_DENIED");
        var resource = "CodingAssistant.Api.Skills." + pin.Name + ".SKILL.md";
        using var stream = typeof(SkillCatalog).Assembly.GetManifestResourceStream(resource)
            ?? throw new StoreFailure("SKILL_CONTENT_MISSING");
        if (stream.Length is <= 0 or > MaximumBytes)
            throw new StoreFailure("SKILL_CONTENT_INVALID");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var (description, instructions) = Validate(memory.ToArray(), pin.Name, pin.Hash);
        return new SkillCatalogEntry(pin.Name, description, "1",
            "https://github.com/dvdhtzr/CodingAssistant/blob/" + SourceCommit +
                "/src/CodingAssistant.Api/Skills/" + pin.Name + "/SKILL.md",
            SourceCommit, pin.Hash, "UNSPECIFIED_REPOSITORY_OWNED",
            "MAF local Ollama instruction-only candidate; live activation not qualified",
            pin.Role, pin.Scope, MaximumBytes, Array.Empty<string>(),
            "DISCOVERED_NOT_APPROVED", false, false, instructions);
    }

    // This deliberately supports only the two scalar fields of our fixed
    // instruction-only packages, not general YAML or external references.
    // Hash is over UTF-8 canonical LF text to tolerate Git CRLF checkouts.
    public static (string Description, string Instructions) Validate(
        byte[] data, string name, string expectedSha256)
    {
        if (data.Length is <= 0 or > MaximumBytes ||
            data.Any(b => b < 32 && b is not (10 or 13)))
            throw new StoreFailure("SKILL_CONTENT_INVALID");
        string canonical;
        try
        {
            canonical = new UTF8Encoding(false, true).GetString(data)
                .Replace("\r\n", "\n", StringComparison.Ordinal);
        }
        catch (DecoderFallbackException)
        {
            throw new StoreFailure("SKILL_CONTENT_INVALID");
        }
        if (canonical.Contains('\r'))
            throw new StoreFailure("SKILL_CONTENT_INVALID");
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        if (!string.Equals(hash, expectedSha256, StringComparison.Ordinal))
            throw new StoreFailure("SKILL_HASH_MISMATCH");
        var lines = canonical.Split('\n');
        if (lines.Length < 7 || lines[0] != "---" || lines[1] != "name: " + name ||
            !lines[2].StartsWith("description: ", StringComparison.Ordinal) ||
            lines[3] != "---" || lines[4] != "" ||
            name.Length is < 1 or > 64 ||
            name.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
            throw new StoreFailure("SKILL_CONTENT_INVALID");
        var description = lines[2]["description: ".Length..];
        var instructions = string.Join("\n", lines.Skip(5)).TrimEnd('\n');
        if (description.Length is < 1 or > 160 || string.IsNullOrWhiteSpace(instructions) ||
            description.Any(c => c is ':' or '[' or ']' or '{' or '}' or '&' or '*' or '!' or '|' or '>') ||
            instructions.Contains("---", StringComparison.Ordinal))
            throw new StoreFailure("SKILL_CONTENT_INVALID");
        return (description, instructions);
    }
}
