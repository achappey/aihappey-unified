using System.Text.Json;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static AIToolCallContentPart CreateSyntheticToolPart(string id, string name, string arguments,
        string? output, string? status, string providerId)
    {
        JsonElement? result = output is null ? null : SyntheticResponsesTool.ParseResult(output);
        var metadata = SyntheticResponsesTool.ReadProviderMetadata(result ?? default, providerId);
        return new AIToolCallContentPart
        {
            Type = "tool-call", ToolCallId = id, ToolName = name,
            Title = result is { } payload ? SyntheticResponsesTool.ReadTitle(payload) ?? name : name,
            Input = ParseJsonString(arguments), Output = result,
            State = result is null ? "input-available" : "output-available", ProviderExecuted = true,
            Metadata = metadata.ToDictionary(p => p.Key, p => (object?)p.Value)
        };
    }

    private static bool IsSyntheticStreamItem(ResponseStreamItem item)
        => SyntheticResponsesTool.IsGatewayCall(item.Type,
            item.AdditionalProperties?.TryGetValue("server_label", out var label) == true ? label.GetString() : null);

    private static IEnumerable<AIEventEnvelope> CreateSyntheticToolEnvelopes(ResponseStreamItem item,
        string providerId, ResponseStreamMappingState state)
    {
        if (item.Id is null || item.AdditionalProperties?.TryGetValue("output", out var output) != true
            || output.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(output.GetString())
            || !state.CompletedSyntheticTools.Add(item.Id)) yield break;
        var tool = CreateSyntheticToolPart(item.Id, item.Name ?? "tool", item.Arguments?.ToString() ?? "{}",
            output.GetString()!, item.Status, providerId);
        var metadata = SyntheticResponsesTool.ReadProviderMetadata((JsonElement)tool.Output!, providerId);
        yield return CreateToolInputStartEnvelope(item.Id, tool.ToolName!, tool.Title, true, metadata);
        yield return CreateToolInputEndEnvelope(item.Id, tool.ToolName!, tool.Input ?? new { }, tool.Title, true, metadata);
        yield return CreateToolOutputEnvelope(item.Id, tool.Output!, tool.ToolName, providerExecuted: true, providerMetadata: metadata);
    }
}
