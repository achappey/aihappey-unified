using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    /// <summary>Only an explicit local configuration supplies the fallback; hosted calls always win.</summary>
    public static bool HasLocalShell(this ResponseRequest request) => request.Tools?.Any(tool => tool.Type == "shell"
        && tool.Extra?.TryGetValue("environment", out var environment) == true
        && environment.ValueKind == JsonValueKind.Object && environment.TryGetProperty("type", out var type)
        && type.GetString() == "local") == true;

    private static bool IsLocalShellCall(ResponseStreamItem item, bool fallback = false)
        => TryGetItemJson(item, "environment", out var environment)
            ? environment.ValueKind == JsonValueKind.Object && environment.TryGetProperty("type", out var type) && type.GetString() == "local"
            : fallback;

    private static bool IsShellPart(AIToolCallContentPart part, string? callType, string? resultType)
        => part.Type is "shell_call" or "shell_call_output" || callType is "shell_call" or "shell_call_output"
            || resultType == "shell_call_output" || IsHostedShellToolPart(part, callType, resultType);

    private static AIInputItem CreateUnifiedShellInputItem(ResponseInputItem item, string providerId, bool localShell)
    {
        var map = ToJsonMap(item);
        var outputOnly = item is ResponseShellCallOutputItem;
        var metadata = CreateResponsesToolMetadata(providerId, item.Type!, item, map, 0);
        var scope = GetOrCreateProviderScopedMetadata(metadata, providerId);
        foreach (var field in new[] { "environment", "created_by", "max_output_length" })
            if (map.TryGetValue(field, out var value) && HasMeaningfulValue(value))
                scope[field] = CloneIfJsonElement(value)!;

        return new AIInputItem
        {
            Type = item.Type!, Role = outputOnly ? "tool" : "assistant",
            Content = [new AIToolCallContentPart
            {
                Type = item.Type!, ToolName = "shell_call", Title = "shell_call",
                ToolCallId = GetValue<string>(map, "call_id") ?? throw new JsonException("Shell call_id is required."),
                ProviderExecuted = !localShell, State = GetValue<string>(map, "status"), Metadata = metadata,
                Input = outputOnly ? null : GetValue<object>(map, "action"),
                Output = outputOnly ? new { output = GetValue<object>(map, "output"), max_output_length = GetValue<int?>(map, "max_output_length") } : null
            }],
            Metadata = metadata
        };
    }

    private static IEnumerable<ResponseInputItem> CreateLocalShellReplayItems(AIToolCallContentPart part,
        Dictionary<string, object?> itemMetadata, string providerId)
    {
        var metadata = part.Metadata ?? [];
        T? Value<T>(string key) => ExtractNestedChannelValue<T>(metadata, "messages.provider.call.metadata", providerId, key)
            ?? ExtractNestedValue<T>(metadata, providerId, key) ?? ExtractNestedValue<T>(itemMetadata, providerId, key);
        var callId = Value<string>("call_id") ?? part.ToolCallId;
        var outputOnly = part.Type == "shell_call_output" || Value<string>("type") == "shell_call_output";
        var action = Value<ResponseShellCallAction>("action") ?? TryConvertValue<ResponseShellCallAction>(part.Input);
        if (!outputOnly)
            yield return new ResponseShellCallItem
            {
                Id = Value<string>("id"), CallId = callId, Status = Value<string>("status"),
                CreatedBy = Value<string>("created_by"), Environment = Value<ResponseShellCallEnvironment>("environment"),
                Action = action ?? throw new JsonException("Shell action is required for native replay.")
            };
        if (!HasToolOutput(part) && part.Approval?.Approved != false && part.State is not ("output-error" or "output-denied")) yield break;
        var output = JsonSerializer.SerializeToElement(part.Output, Json);
        var result = output.ValueKind == JsonValueKind.Object && output.TryGetProperty("structuredContent", out var structured) ? structured : output;
        var failed = part.Approval?.Approved == false || part.State is "output-error" or "output-denied"
            || output.ValueKind == JsonValueKind.Object && output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True;
        List<ResponseShellCallOutputChunk>? chunks = !failed && result.ValueKind == JsonValueKind.Object && result.TryGetProperty("output", out var values)
            ? values.Deserialize<List<ResponseShellCallOutputChunk>>(Json) : null;
        chunks ??= [new() { Stderr = part.Approval?.Approved == false
            ? part.Approval.Reason ?? "The user denied release of this tool result. Commands were already executed."
            : "Shell execution failed.", Outcome = new ResponseShellExitOutcome { ExitCode = 1 } }];
        var maximum = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("max_output_length", out var limit)
            ? limit.GetInt32() : action?.MaxOutputLength;
        yield return new ResponseShellCallOutputItem
        {
            CallId = callId, Output = chunks, MaxOutputLength = maximum,
            Status = outputOnly ? Value<string>("status") : ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "status"),
            CreatedBy = outputOnly ? Value<string>("created_by") : ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "created_by"),
            Id = outputOnly ? Value<string>("id") : ExtractNestedChannelValue<string>(metadata, "messages.provider.result.metadata", providerId, "id")
        };
    }
}
