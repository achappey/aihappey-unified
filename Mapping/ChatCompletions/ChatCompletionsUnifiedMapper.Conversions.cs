using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    /// <summary>Converts normalized usage while retaining raw provider token details.</summary>
    public static ChatCompletionUsage ToChatCompletionUsage(this AIUsage usage)
    {
        ArgumentNullException.ThrowIfNull(usage);

        if (usage.AdditionalProperties is { Count: > 0 } rawUsage)
        {
            var preserved = JsonSerializer.SerializeToElement(rawUsage).ToChatCompletionUsage()!;
            preserved.PromptTokens = usage.InputTokens ?? preserved.PromptTokens;
            preserved.CompletionTokens = usage.OutputTokens ?? preserved.CompletionTokens;
            preserved.TotalTokens = usage.TotalTokens ?? preserved.TotalTokens;
            return preserved;
        }

        return new ChatCompletionUsage
        {
            PromptTokens = usage.InputTokens ?? 0,
            CompletionTokens = usage.OutputTokens ?? 0,
            TotalTokens = usage.TotalTokens ?? ((usage.InputTokens ?? 0) + (usage.OutputTokens ?? 0)),
            PromptTokensDetails = usage.CachedInputTokens is null && usage.CacheWriteInputTokens is null
                ? null
                : new ChatCompletionPromptTokenDetails
                {
                    CachedTokens = usage.CachedInputTokens,
                    CacheWriteTokens = usage.CacheWriteInputTokens
                },
            CompletionTokensDetails = usage.ReasoningTokens is null
                ? null
                : new ChatCompletionCompletionTokenDetails { ReasoningTokens = usage.ReasoningTokens },
            AdditionalProperties = new()
            {
                ["input_tokens"] = JsonSerializer.SerializeToElement(usage.InputTokens),
                ["output_tokens"] = JsonSerializer.SerializeToElement(usage.OutputTokens)
            }
        };
    }

    // Mapper implementation details, not a public raw-JSON/legacy adapter surface.
    private static T ReadChatContract<T>(this JsonElement value)
        => value.Deserialize<T>(JsonSerializerOptions.Web)
            ?? throw new JsonException($"Expected {typeof(T).Name}.");

    private static ChatCompletionUsage? ToChatCompletionUsage(this object? value)
    {
        if (value is null)
            return null;
        if (value is ChatCompletionUsage typed)
            return typed;
        if (value is AIUsage unified)
            return unified.ToChatCompletionUsage();

        var raw = value is JsonElement json ? json : JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
        if (raw.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        var usage = raw.ReadChatContract<ChatCompletionUsage>();
        if (!raw.TryGetProperty("prompt_tokens", out _) && raw.TryGetProperty("input_tokens", out var input))
            usage.PromptTokens = input.ValueKind == JsonValueKind.Null ? 0 : input.GetInt32();
        if (!raw.TryGetProperty("completion_tokens", out _) && raw.TryGetProperty("output_tokens", out var output))
            usage.CompletionTokens = output.ValueKind == JsonValueKind.Null ? 0 : output.GetInt32();
        return usage;
    }

    private static void SetProviderMetadata(this ChatCompletionOptions options, Dictionary<string, object?>? metadata)
    {
        if (metadata is not null)
            (options.AdditionalProperties ??= [])["providerMetadata"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
    }
}
