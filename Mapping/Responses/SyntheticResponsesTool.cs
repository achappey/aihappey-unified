using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Responses.Output;

namespace AIHappey.Responses.Mapping;

/// <summary>
/// Projects gateway-executed tools into the standard Responses server-tool contract.
/// Transport metadata lives in MCP's namespaced _meta, never in private Responses fields/events.
/// </summary>
public static class SyntheticResponsesTool
{
    public const string ServerLabel = "aihappey";
    public const string MetadataKey = "aihappey/gateway";

    public static ResponseOutputMcpCall Create(string id, string name, object? input, object? output,
        string providerId, object? providerMetadata = null, string status = "completed", string? title = null)
    {
        var result = output is null ? null : ToResult(output);
        if (result is not null)
        {
            var meta = result["_meta"] as JsonObject ?? new JsonObject();
            result["_meta"] = meta;
            if (meta[MetadataKey] is not JsonObject previous || previous["provider"]?.GetValue<string>() != providerId)
                meta[MetadataKey] = new JsonObject
                {
                    ["provider"] = providerId,
                    ["title"] = title,
                    ["providerMetadata"] = providerMetadata is null ? null : JsonSerializer.SerializeToNode(providerMetadata, JsonSerializerOptions.Web)
                };
        }
        return new ResponseOutputMcpCall
        {
            Id = id, Name = name, ServerLabel = ServerLabel,
            Arguments = SerializeArguments(input),
            Output = result?.ToJsonString(JsonSerializerOptions.Web),
            Status = status == "incomplete" || result?["isError"]?.GetValue<bool>() == true ? "failed" : status
        };
    }

    private static string SerializeArguments(object? input)
    {
        if (input is string text)
        {
            try { using var document = JsonDocument.Parse(text); return text; }
            catch (JsonException) { /* Preserve a plain-text input as a JSON string. */ }
        }
        return JsonSerializer.Serialize(input ?? new { }, JsonSerializerOptions.Web);
    }

    public static string? ReadTitle(JsonElement result)
        => TryGetTransportMetadata(result, out var meta) && meta.TryGetProperty("title", out var title)
            && title.ValueKind == JsonValueKind.String ? title.GetString() : null;

    // Work on a JSON copy: preserve every MCP content block, _meta, and future field,
    // and never mutate or deserialize/re-serialize an existing result through a narrower model.
    private static JsonObject ToResult(object output)
    {
        var value = JsonSerializer.SerializeToNode(output, JsonSerializerOptions.Web);
        if (value is JsonObject obj && obj["content"] is JsonArray)
            return obj;
        if (value is JsonObject envelope && (envelope.ContainsKey("structuredContent") || envelope.ContainsKey("isError")))
        {
            envelope["content"] ??= new JsonArray();
            return envelope;
        }
        return value is JsonObject structured
            ? new JsonObject { ["content"] = new JsonArray(), ["structuredContent"] = structured }
            : new JsonObject { ["content"] = new JsonArray(new JsonObject
                { ["type"] = "text", ["text"] = value is JsonValue scalar && scalar.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "null" }) };
    }

    public static bool IsGatewayCall(string? type, string? serverLabel)
        => type == "mcp_call" && serverLabel == ServerLabel;

    public static JsonElement ParseResult(string output)
    {
        var result = JsonSerializer.Deserialize<JsonElement>(output);
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
            throw new JsonException("Gateway MCP output must be a complete CallToolResult.");
        return result;
    }

    public static bool BelongsToProvider(JsonElement result, string providerId)
        => TryGetTransportMetadata(result, out var meta)
            && meta.TryGetProperty("provider", out var provider) && provider.GetString() == providerId;

    public static Dictionary<string, Dictionary<string, object>> ReadProviderMetadata(JsonElement result, string providerId)
    {
        var metadata = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
        if (BelongsToProvider(result, providerId) && TryGetTransportMetadata(result, out var meta)
            && meta.TryGetProperty("providerMetadata", out var original) && original.ValueKind == JsonValueKind.Object)
        {
            // Unified metadata and stream metadata both use provider-keyed envelopes.
            if (original.TryGetProperty(providerId, out var scoped) && scoped.ValueKind == JsonValueKind.Object)
                metadata[providerId] = scoped.EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
        }
        if (!metadata.TryGetValue(providerId, out var values)) metadata[providerId] = values = [];
        values["server_label"] = ServerLabel;
        values["gateway_synthetic"] = true;
        return metadata;
    }

    private static bool TryGetTransportMetadata(JsonElement result, out JsonElement metadata)
    {
        metadata = default;
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("_meta", out var meta)
            && meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty(MetadataKey, out metadata)
            && metadata.ValueKind == JsonValueKind.Object;
    }
}
