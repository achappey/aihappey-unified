using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Unified.Models;

/// <summary>
/// Bridges native root fields and the unified executor's provider-scoped options.
/// Native metadata is application data, never an execution-options envelope.
/// </summary>
internal static class NativeRequestOptions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static JsonElement Capture(JsonElement request)
        => JsonSerializer.SerializeToElement(request.EnumerateObject()
            .Where(property => property.Name is not ("metadata" or "model" or "messages" or "input"
                or "instructions" or "tools" or "stream" or "temperature" or "top_p"
                or "max_tokens" or "max_completion_tokens" or "max_output_tokens"))
            .GroupBy(property => property.Name)
            .ToDictionary(group => group.Key, group => group.Last().Value.Clone()), Json);

    public static T Apply<T>(T native, Dictionary<string, object?>? executionOptions, string providerId,
        bool includeProviderTools = false)
    {
        if (executionOptions?.TryGetValue(providerId, out var configured) != true || configured is null)
            return native;
        var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (executionOptions?.TryGetValue(providerId, out var options) == true && options is not null)
        {
            var root = options is JsonElement element ? element : JsonSerializer.SerializeToElement(options, Json);
            if (root.ValueKind == JsonValueKind.Object)
                foreach (var property in root.EnumerateObject())
                    if (property.Name is not ("headers" or "tools" or "anthropic-beta" or "openai-beta"))
                        fields[property.Name] = property.Value.Clone();
        }

        // Typed/native values win. Deserializing the merged root materializes known
        // options into typed properties and leaves only unknown fields in extension data.
        foreach (var property in JsonSerializer.SerializeToElement(native, Json).EnumerateObject())
            fields[property.Name] = property.Value.Clone();

        if (includeProviderTools && executionOptions?.TryGetValue(providerId, out var toolOptions) == true && toolOptions is not null)
        {
            var root = JsonSerializer.SerializeToElement(toolOptions, Json);
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tools", out var tools)
                && tools.ValueKind == JsonValueKind.Array)
            {
                var combined = fields.TryGetValue("tools", out var current) && current.ValueKind == JsonValueKind.Array
                    ? current.EnumerateArray().Select(item => item.Clone()).ToList() : [];
                combined.AddRange(tools.EnumerateArray().Select(item => item.Clone()));
                fields["tools"] = JsonSerializer.SerializeToElement(combined, Json);
            }
        }

        return JsonSerializer.SerializeToElement(fields, Json).Deserialize<T>(Json)
            ?? throw new JsonException($"Could not construct native {typeof(T).Name} request.");
    }

    public static Dictionary<string, string>? Headers(Dictionary<string, object?>? executionOptions,
        string providerId, Dictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (executionOptions?.TryGetValue(providerId, out var options) == true && options is not null)
        {
            var root = JsonSerializer.SerializeToElement(options, Json);
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("headers", out var extra) && extra.ValueKind == JsonValueKind.Object)
                    foreach (var property in extra.EnumerateObject())
                        if (!AIHappey.Responses.ProviderCredentialHeaders.IsProviderKey(property.Name)
                            && property.Value.ValueKind == JsonValueKind.String)
                            result[property.Name] = property.Value.GetString()!;
                foreach (var name in new[] { "anthropic-beta", "openai-beta" })
                    if (root.TryGetProperty(name, out var beta) && beta.ValueKind == JsonValueKind.String)
                        result[name] = beta.GetString()!;
            }
        }
        foreach (var header in headers ?? [])
            result[header.Key] = header.Value;
        return result.Count == 0 ? null : result;
    }
}
