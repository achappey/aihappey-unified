using AIHappey.Responses.Streaming;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static ToolSearchStreamState GetToolSearchStreamState(ResponseStreamMappingState mappingState)
        => mappingState.ToolSearch;

    private static void ClearToolSearchStreamState(ResponseStreamMappingState mappingState)
        => mappingState.ToolSearch = new();

    private static string RegisterToolSearchCall(ResponseStreamMappingState mappingState, ResponseStreamItem item)
    {
        var unifiedId = item.CallId ?? item.Id ?? Guid.NewGuid().ToString("N");

        if (!string.IsNullOrWhiteSpace(item.CallId))
            GetToolSearchStreamState(mappingState).CallIds[item.CallId] = unifiedId;

        if (!string.IsNullOrWhiteSpace(item.Id))
            GetToolSearchStreamState(mappingState).ItemIds[item.Id] = unifiedId;

        if (IsServerToolSearch(item) && string.IsNullOrWhiteSpace(item.CallId))
            GetToolSearchStreamState(mappingState).PendingHostedCallIds.Enqueue(unifiedId);

        return unifiedId;
    }

    private static string RegisterToolSearchOutput(ResponseStreamMappingState mappingState, ResponseStreamItem item)
    {
        var unifiedId = ResolveRegisteredToolSearchId(mappingState, item);

        if (IsServerToolSearch(item)
            && string.IsNullOrWhiteSpace(item.CallId)
            && GetToolSearchStreamState(mappingState).PendingHostedCallIds.TryDequeue(out var hostedCallId))
        {
            unifiedId = hostedCallId;
        }

        if (!string.IsNullOrWhiteSpace(item.Id))
            GetToolSearchStreamState(mappingState).ItemIds[item.Id] = unifiedId;

        return unifiedId;
    }

    private static string ResolveRegisteredToolSearchId(ResponseStreamMappingState mappingState, ResponseStreamItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.CallId)
            && GetToolSearchStreamState(mappingState).CallIds.TryGetValue(item.CallId, out var byCallId))
        {
            return byCallId;
        }

        if (!string.IsNullOrWhiteSpace(item.Id)
            && GetToolSearchStreamState(mappingState).ItemIds.TryGetValue(item.Id, out var byItemId))
        {
            return byItemId;
        }

        return item.CallId ?? item.Id ?? Guid.NewGuid().ToString("N");
    }

    internal sealed class ToolSearchStreamState
    {
        public Queue<string> PendingHostedCallIds { get; } = new();

        public Dictionary<string, string> CallIds { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, string> ItemIds { get; } = new(StringComparer.Ordinal);
    }
}
