using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodingAssistant.Api;

// The interview/source-draft model picker is distinct from the separately
// qualified, immutably pinned M1 executable coding runtime.
public interface IOllamaModelCatalog
{
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellation);
}

public sealed class LocalOllamaModelCatalog : IOllamaModelCatalog
{
    private static readonly Regex ModelName = new(
        @"\A[A-Za-z0-9][A-Za-z0-9._/-]{0,119}(?::[A-Za-z0-9][A-Za-z0-9._-]{0,63})?\z",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    // A name alone never authorizes a model; the API also checks the exact
    // model against /api/tags before accepting a new interview/model call.
    // Reject Ollama cloud tags, including local-server cloud proxies.
    public static bool ValidLocalName(string? name) =>
        name is { Length: > 0 and <= 160 } &&
        ModelName.IsMatch(name) &&
        !name.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) &&
        !name.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ParseTags(string json)
    {
        try
        {
            using var parsed = JsonDocument.Parse(json);
            if (!parsed.RootElement.TryGetProperty("models", out var models) ||
                models.ValueKind != JsonValueKind.Array ||
                models.GetArrayLength() > 512)
                throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");

            var found = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in models.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var nameValue) ||
                    nameValue.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("size", out var sizeValue) ||
                    sizeValue.ValueKind != JsonValueKind.Number ||
                    !sizeValue.TryGetInt64(out var size) || size <= 0)
                    continue; // Not a locally downloaded model.

                var name = nameValue.GetString();
                if (ValidLocalName(name))
                    found.Add(name!);
            }
            return found.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }
        catch (JsonException)
        {
            throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
        }
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellation)
    {
        // Fixed loopback URL; no credentials, proxy, redirects or cloud fallback.
        using var handler = new HttpClientHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false
        };
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 200_000
        };
        try
        {
            using var response = await client.GetAsync(
                "api/tags", HttpCompletionOption.ResponseContentRead, cancellation);
            if (response.StatusCode != HttpStatusCode.OK)
                throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
            var json = await response.Content.ReadAsStringAsync(cancellation);
            if (json.Length > 200_000)
                throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
            return ParseTags(json);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException)
        {
            if (cancellation.IsCancellationRequested)
                throw new OperationCanceledException(cancellation);
            throw new StoreFailure("LOCAL_MODEL_UNAVAILABLE");
        }
    }
}
