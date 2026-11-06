using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    // These are source-protocol options, not Google Interactions root fields.
    // Keep capture/round-trip behavior intact in the source mappers, and consume
    // them only when translating Messages or Chat Completions to Interactions.
    private static readonly HashSet<string> EndpointOnlyInteractionOptions = new(StringComparer.Ordinal)
    {
        "system", "max_tokens", "max_completion_tokens", "tool_choice", "parallel_tool_calls",
        "response_format", "generation_config", "stop", "stop_sequences", "seed", "top_k",
        "n", "frequency_penalty", "presence_penalty", "logit_bias", "logprobs", "top_logprobs",
        "stream_options", "user", "functions", "function_call", "modalities", "audio", "prediction",
        "reasoning_effort", "verbosity", "web_search_options", "safety_identifier", "prompt_cache_key",
        "prompt_cache_retention", "prompt_cache_options", "moderation", "thinking", "output_config",
        "cache_control", "container", "context_management", "compaction", "inference_geo"
    };

    private static Dictionary<string, object?> ToEndpointInteractionOptions(
        Dictionary<string, object?> metadata, string providerId)
    {
        var options = metadata.TryGetValue(providerId, out var configured) ? configured.ToDictionary() : null;
        return new Dictionary<string, object?>
        {
            [providerId] = options?.Where(pair => !EndpointOnlyInteractionOptions.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
        };
    }

    private static void ApplyEndpointGenerationOptions(InteractionGenerationConfig config,
        Dictionary<string, object?> metadata)
    {
        config.Seed ??= ExtractValue<int?>(metadata, "chatcompletions.request.seed");
        config.StopSequences ??= ExtractObject<List<string>>(metadata, "messages.request.stop_sequences");
        var stop = ExtractObject<JsonElement?>(metadata, "chatcompletions.request.stop");
        if (config.StopSequences is null && stop is { } value)
            config.StopSequences = value.ValueKind == JsonValueKind.String ? [value.GetString()!]
                : value.ValueKind == JsonValueKind.Array ? value.Deserialize<List<string>>(Json) : null;
        var topK = ExtractValue<int?>(metadata, "messages.request.top_k");
        if (topK is not null)
            (config.AdditionalProperties ??= [])["top_k"] = JsonSerializer.SerializeToElement(topK, Json);
    }

    private static bool IsInteractionInstructionRole(string? role)
        => string.Equals(role, "system", StringComparison.OrdinalIgnoreCase)
           || string.Equals(role, "developer", StringComparison.OrdinalIgnoreCase);

    private static string? ToInteractionSystemInstruction(AIRequest request)
    {
        var instructions = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Instructions)) instructions.Add(request.Instructions);
        foreach (var item in (request.Input?.Items ?? []).Where(item => IsInteractionInstructionRole(item.Role)))
        {
            if ((item.Content ?? []).Any(part => part is not AITextContentPart))
                throw new ArgumentException("Google system/developer instructions support text content only.", nameof(request));
            var text = string.Join("\n\n", (item.Content ?? []).OfType<AITextContentPart>()
                .Select(part => part.Text).Where(text => !string.IsNullOrWhiteSpace(text)));
            if (!string.IsNullOrWhiteSpace(text)) instructions.Add(text);
        }
        return instructions.Count == 0 ? null : string.Join("\n\n", instructions);
    }

    private static InteractionToolChoiceValue? ToEndpointInteractionToolChoice(object? choice, bool? parallel)
    {
        if (parallel == false)
            throw new NotSupportedException("Google Interactions cannot disable parallel client function calls.");
        if (choice is null) return null;
        var value = JsonSerializer.SerializeToElement(choice, Json);
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        if (value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("disable_parallel_tool_use", out var disable)
            && disable.ValueKind == JsonValueKind.True)
            throw new NotSupportedException("Google Interactions cannot disable parallel client function calls.");

        var type = value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var kind)
                ? kind.GetString() : null;
        if (type is "auto" or "none" or "any" or "required")
            return new InteractionToolChoiceValue(type == "required" ? "any" : type);
        if (type is "function" or "tool")
        {
            var source = value.TryGetProperty("function", out var function) ? function : value;
            if (source.ValueKind == JsonValueKind.Object && source.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(name.GetString()))
                return new InteractionToolChoiceConfiguration
                {
                    AllowedTools = new InteractionAllowedTools { Mode = "any", Tools = [name.GetString()!] }
                };
        }
        throw new NotSupportedException("This endpoint tool choice cannot be represented by Google Interactions.");
    }

    private static List<InteractionTool> NormalizeEndpointInteractionTools(List<InteractionTool> tools)
    {
        var result = new List<InteractionTool>();
        var byKey = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            if (tool is InteractionFunctionTool function && string.IsNullOrWhiteSpace(function.Name))
                throw new ArgumentException("Google client function declarations require a name.", nameof(tools));
            var key = tool switch
            {
                InteractionFunctionTool fn => "function:" + fn.Name,
                InteractionMcpServerTool mcp => "mcp_server:" + mcp.Name,
                _ => tool.Type
            };
            var wire = JsonSerializer.SerializeToElement(tool, Json);
            if (byKey.TryGetValue(key, out var previous))
            {
                if (!JsonElement.DeepEquals(previous, wire))
                    throw new ArgumentException($"Conflicting Google tool declarations for '{key}'.", nameof(tools));
                continue;
            }
            byKey.Add(key, wire);
            result.Add(tool);
        }
        return result;
    }
}
