using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.ChatCompletions;

internal static class ChatCompletionsStreamTestHelper
{
    internal static async Task<ChatCompletionUpdate> MapSingleUpdateAsync(AIStreamEvent streamEvent)
        => Assert.Single(await MapUpdatesAsync([streamEvent]));

    internal static async Task<List<ChatCompletionUpdate>> MapUpdatesAsync(IEnumerable<AIStreamEvent> events)
    {
        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in Source(events).ToChatCompletionUpdates())
            updates.Add(update);
        return updates;
    }

    private static async IAsyncEnumerable<AIStreamEvent> Source(IEnumerable<AIStreamEvent> events)
    {
        foreach (var streamEvent in events)
        {
            yield return streamEvent;
            await Task.Yield();
        }
    }
}
