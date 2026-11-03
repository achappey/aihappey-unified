using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static AIUsage? ToUnifiedUsage(object? value)
    {
        if (value is AIUsage normalized)
            return normalized;

        var usage = ToResponseUsageObject(value);
        return usage is null
            ? null
            : new AIUsage
            {
                InputTokens = usage.InputTokens,
                OutputTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens
                    ?? (usage.InputTokens is not null && usage.OutputTokens is not null
                        ? usage.InputTokens + usage.OutputTokens
                        : null),
                CachedInputTokens = usage.InputTokensDetails?.CachedTokens,
                CacheWriteInputTokens = usage.InputTokensDetails?.CacheWriteTokens,
                ReasoningTokens = usage.OutputTokensDetails?.ReasoningTokens,
                AdditionalProperties = (usage.Raw ?? JsonSerializer.SerializeToElement(usage, ResponseJson.Default))
                    .EnumerateObject()
                    .Where(property => property.Name is not ("inputTokens" or "outputTokens" or "totalTokens"
                        or "cachedInputTokens" or "cacheWriteInputTokens" or "reasoningTokens"))
                    .ToDictionary(property => property.Name, property => property.Value.Clone())
            };
    }

    private static ResponseUsage? ToResponseUsage(object? value)
    {
        if (value is null)
            return null;

        if (value is ResponseUsage responseUsage)
            return responseUsage;

        if (value is AIUsage usage)
            return ToResponseUsage(usage);

        return ToResponseUsageObject(value);
    }

    private static ResponseUsage ToResponseUsage(AIUsage usage)
    {
        var result = ToResponseUsageObject(usage.AdditionalProperties) ?? new ResponseUsage();
        if (usage.AdditionalProperties?.TryGetValue("input_tokens_details", out var inputDetails) == true
            && inputDetails.ValueKind == JsonValueKind.Object)
            result.InputTokensDetails = inputDetails.Deserialize<ResponseInputTokensDetails>(Json);
        if (usage.AdditionalProperties?.TryGetValue("output_tokens_details", out var outputDetails) == true
            && outputDetails.ValueKind == JsonValueKind.Object)
            result.OutputTokensDetails = outputDetails.Deserialize<ResponseOutputTokensDetails>(Json);
        result.InputTokens = usage.InputTokens;
        result.OutputTokens = usage.OutputTokens;
        result.TotalTokens = usage.TotalTokens
            ?? (usage.InputTokens is not null && usage.OutputTokens is not null
                ? usage.InputTokens + usage.OutputTokens : null);
        if (usage.CachedInputTokens is not null || usage.CacheWriteInputTokens is not null)
        {
            result.InputTokensDetails ??= new ResponseInputTokensDetails();
            result.InputTokensDetails.CachedTokens = usage.CachedInputTokens ?? result.InputTokensDetails.CachedTokens;
            result.InputTokensDetails.CacheWriteTokens = usage.CacheWriteInputTokens ?? result.InputTokensDetails.CacheWriteTokens;
        }
        if (usage.ReasoningTokens is not null)
        {
            result.OutputTokensDetails ??= new ResponseOutputTokensDetails();
            result.OutputTokensDetails.ReasoningTokens = usage.ReasoningTokens;
        }
        // Raw is diagnostic input, not an alternative to the updated normalized counters.
        result.Raw = null;
        return result;
    }

    private static ResponseUsage? ToResponseUsageObject(object? value)
    {
        if (value is null)
            return null;

        if (value is ResponseUsage usage)
            return usage;

        try
        {
            var raw = value is JsonElement element
                ? element.Clone()
                : JsonSerializer.SerializeToElement(value, Json);
            return raw.ValueKind == JsonValueKind.Object
                ? raw.Deserialize<ResponseUsage>(ResponseJson.Default)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static object GetRawUsage(object usage)
    {
        var normalized = ToResponseUsageObject(usage);
        if (normalized?.Raw is { } raw && raw.ValueKind == JsonValueKind.Object)
            return raw.Clone();

        return usage is JsonElement element
            ? element.Clone()
            : JsonSerializer.SerializeToElement(usage, Json);
    }

    private static Dictionary<string, object?> AddRawUsageMetadata(
        Dictionary<string, object?>? metadata,
        string providerId,
        object? usage)
    {
        var result = metadata?.ToDictionary(entry => entry.Key, entry => entry.Value) ?? [];
        if (usage is null || string.IsNullOrWhiteSpace(providerId))
            return result;

        var providerMetadata = ToObjectDictionary(
            result.TryGetValue(providerId, out var existingProviderMetadata)
                ? existingProviderMetadata
                : null);
        providerMetadata["usage"] = GetRawUsage(usage);
        result[providerId] = providerMetadata;
        return result;
    }

    private static Dictionary<string, object?> ToObjectDictionary(object? value)
    {
        if (value is Dictionary<string, object?> nullableDictionary)
            return nullableDictionary.ToDictionary(entry => entry.Key, entry => entry.Value);

        if (value is Dictionary<string, object> dictionary)
            return dictionary.ToDictionary(entry => entry.Key, entry => (object?)entry.Value);

        try
        {
            var json = value is JsonElement element
                ? element
                : JsonSerializer.SerializeToElement(value, Json);
            return json.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Deserialize<Dictionary<string, object?>>(json.GetRawText(), Json) ?? []
                : [];
        }
        catch
        {
            return [];
        }
    }
}
