using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetCoreAI.Hub;

namespace NetCoreAI.Knowledge;

/// <summary>
/// Documents fetched from a JSON HTTP endpoint.
/// </summary>
/// <remarks>
/// A path into the response selects the array of items; further paths name each item's id, title and text.
/// Phase 3 will let this reuse a Tool definition for the call itself; until then it is a plain GET, which
/// covers the common case of an internal API that already returns the documents.
/// </remarks>
internal sealed class RestApiDataSource(
    IHttpClientFactory httpClientFactory,
    ISecretProtector protector,
    ILogger<RestApiDataSource> logger) : IDataSource
{
    public const string TypeName = "rest";

    public const string UrlSetting = "url";

    /// <summary>Dotted path to the array of items, e.g. "data.items". Empty means the response is the array.</summary>
    public const string ItemsPathSetting = "itemsPath";

    public const string IdPathSetting = "idPath";

    public const string TitlePathSetting = "titlePath";

    /// <summary>Path to the text to index. Empty means the whole item is flattened.</summary>
    public const string ContentPathSetting = "contentPath";

    /// <summary>Header settings are prefixed, e.g. "header:Authorization".</summary>
    public const string HeaderPrefix = "header:";

    /// <summary>A setting value with this prefix is stored encrypted, so an API key never sits in plaintext.</summary>
    public const string ProtectedPrefix = "enc:";

    public string Type => TypeName;

    public async IAsyncEnumerable<SourceDocument> EnumerateAsync(DataSourceDefinition definition, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var items = await FetchAsync(definition, cancellationToken).ConfigureAwait(false);
        var index = 0;

        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Value(item, definition, IdPathSetting) ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var title = Value(item, definition, TitlePathSetting) ?? $"Item {id}";
            var content = Value(item, definition, ContentPathSetting) ?? JsonExtractor.Flatten(item, string.Empty);
            index++;

            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogDebug("Item {Id} from {Source} has no content; skipping it.", id, definition.Name);
                continue;
            }

            var bytes = Encoding.UTF8.GetBytes(content);
            yield return new SourceDocument(id, title)
            {
                // Given as .txt so the plain-text extractor handles it: the JSON has already been reduced
                // to the text worth indexing.
                FileName = $"{id}.txt",
                ContentType = "text/plain",
                SizeBytes = bytes.Length,
                Source = definition.Settings.GetValueOrDefault(UrlSetting),
                OpenAsync = _ => Task.FromResult<Stream>(new MemoryStream(bytes)),
            };
        }
    }

    public async Task<DataSourceTestResult> TestAsync(DataSourceDefinition definition, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);

        try
        {
            var items = await FetchAsync(definition, cancellationToken).ConfigureAwait(false);
            if (items.Count == 0)
            {
                return new DataSourceTestResult(
                    true,
                    "The endpoint answered, but no items were found. Check the items path against the shape of the response.");
            }

            return new DataSourceTestResult(true, $"The endpoint returned {items.Count} item(s).", items.Count)
            {
                SampleTitles = [.. items.Take(5).Select(i => Value(i, definition, TitlePathSetting) ?? "(untitled)")],
            };
        }
        catch (Exception ex) when (ex is NetCoreAIException or HttpRequestException or JsonException or TaskCanceledException)
        {
            return new DataSourceTestResult(false, ex.Message);
        }
    }

    private async Task<List<JsonElement>> FetchAsync(DataSourceDefinition definition, CancellationToken cancellationToken)
    {
        if (definition.Settings.GetValueOrDefault(UrlSetting) is not { Length: > 0 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new NetCoreAIException("This source has no valid URL. Set an absolute http(s) address.");
        }

        using var client = httpClientFactory.CreateClient(NetCoreAIHttp.HubClient);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");

        foreach (var (key, value) in definition.Settings.Where(s => s.Key.StartsWith(HeaderPrefix, StringComparison.OrdinalIgnoreCase)))
        {
            // An API key is stored encrypted, so header values are unprotected rather than used as written.
            request.Headers.TryAddWithoutValidation(key[HeaderPrefix.Length..], Reveal(value));
        }

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new NetCoreAIException(
                $"{uri.Host} answered {(int)response.StatusCode} {response.ReasonPhrase}. Check the URL and any authentication headers.");
        }

        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        using var document = Parse(json, uri);

        var root = Navigate(document.RootElement, definition.Settings.GetValueOrDefault(ItemsPathSetting));
        return root.ValueKind switch
        {
            // Clone: the elements have to outlive the JsonDocument they came from.
            JsonValueKind.Array => [.. root.EnumerateArray().Select(e => e.Clone())],
            JsonValueKind.Object => [root.Clone()],
            JsonValueKind.Undefined => throw new NetCoreAIException(
                $"The path '{definition.Settings.GetValueOrDefault(ItemsPathSetting)}' was not found in the response from {uri.Host}."),
            _ => [],
        };
    }

    /// <summary>Decrypts a setting stored with the protected prefix; anything else is used as written.</summary>
    internal string Reveal(string value)
    {
        if (!value.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            return protector.Unprotect(value[ProtectedPrefix.Length..]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Data Protection keys are per-deployment: a restored database on a new host cannot read them.
            logger.LogWarning(ex, "A protected setting could not be decrypted; the request will be sent without it.");
            return string.Empty;
        }
    }

    private static JsonDocument Parse(string json, Uri uri)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new NetCoreAIException($"{uri.Host} did not return valid JSON: {ex.Message}", ex);
        }
    }

    private static string? Value(JsonElement item, DataSourceDefinition definition, string setting)
    {
        if (definition.Settings.GetValueOrDefault(setting) is not { Length: > 0 } path)
        {
            return null;
        }

        var element = Navigate(item, path);
        return element.ValueKind switch
        {
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object or JsonValueKind.Array => JsonExtractor.Flatten(element, string.Empty),
            _ => element.ToString(),
        };
    }

    /// <summary>Walks a dotted path, supporting "items[0].name" as well as "data.items".</summary>
    internal static JsonElement Navigate(JsonElement element, string? path)
    {
        if (path is not { Length: > 0 })
        {
            return element;
        }

        var current = element;
        foreach (var rawSegment in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = rawSegment;
            var bracket = segment.IndexOf('[', StringComparison.Ordinal);
            var index = -1;

            if (bracket >= 0 && segment.EndsWith(']'))
            {
                _ = int.TryParse(segment[(bracket + 1)..^1], System.Globalization.CultureInfo.InvariantCulture, out index);
                segment = segment[..bracket];
            }

            if (segment.Length > 0)
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    return default;
                }
            }

            if (index >= 0)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                {
                    return default;
                }

                current = current[index];
            }
        }

        return current;
    }
}
