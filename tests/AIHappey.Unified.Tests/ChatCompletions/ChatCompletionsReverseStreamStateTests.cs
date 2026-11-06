using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;
using State = AIHappey.ChatCompletions.Mapping.ChatCompletionsUnifiedMapper.ChatCompletionsReverseStreamMappingState;

namespace AIHappey.Unified.Tests.ChatCompletions;

public class ChatCompletionsReverseStreamStateTests
{
    [Fact]
    public async Task ParallelCallsAndLaterOutputsUseDistinctStableIndexes()
    {
        AIStreamEvent[] events =
        [
            Event("call-a", new AIToolInputStartEventData { ToolName = "lookup" }),
            Event("call-b", new AIToolInputStartEventData { ToolName = "lookup" }),
            Input("call-b"), Input("call-a"),
            Event("call-a", new AIToolOutputAvailableEventData { ToolName = "lookup", Output = new { found = true } }),
            Event("unrelated-envelope-id", new AIToolOutputErrorEventData { ToolCallId = "call-b", ErrorText = "not found" })
        ];
        var before = JsonSerializer.Serialize(events);
        var calls = new List<ChatCompletionToolCallChunk>();
        await foreach (var update in Source(events).ToChatCompletionUpdates())
            calls.AddRange(update.Choices.SelectMany(choice => choice.Delta?.ToolCalls ?? []));

        Assert.Equal(new[] { "call-b", "call-a", "call-a", "call-b" }, calls.Select(call => call.Id));
        Assert.Equal(new[] { 1, 0, 0, 1 }, calls.Select(call => call.Index));
        Assert.Equal(before, JsonSerializer.Serialize(events));
    }

    [Fact]
    public async Task InterleavedEnumerationsOfSameSequenceHaveIndependentState()
    {
        var enumerations = 0;
        var mapped = Events().ToChatCompletionUpdates();
        await using var first = mapped.GetAsyncEnumerator();
        await using var second = mapped.GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.Equal(0, Call(first.Current).Index);
        Assert.True(await second.MoveNextAsync());
        Assert.Equal(0, Call(second.Current).Index);
        Assert.True(await first.MoveNextAsync());
        Assert.Equal(1, Call(first.Current).Index);
        Assert.True(await second.MoveNextAsync());
        Assert.Equal(1, Call(second.Current).Index);

        async IAsyncEnumerable<AIStreamEvent> Events()
        {
            var firstCall = Interlocked.Increment(ref enumerations) == 1 ? "call-a" : "call-b";
            yield return Input(firstCall);
            await Task.Yield();
            yield return Input(firstCall == "call-a" ? "call-b" : "call-a");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeChunksKeepTheirChoiceToolIndexesAndExtensions(bool wrapped)
    {
        var raw = JsonSerializer.Deserialize<JsonElement>("""
            {"id":"native","model":"model","choices":[{"index":2,"delta":{"tool_calls":[
            {"index":7,"id":"native-call","type":"function","function":{"name":"lookup","arguments":"{}"},"future_tool":42}]}}],
            "future_root":{"keep":true}}
            """);
        var data = wrapped ? (object)new AIDataEventData { Data = raw } : raw;
        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in Source([Input("synthetic"), Event("native", data)]).ToChatCompletionUpdates())
            updates.Add(update);

        var wire = JsonSerializer.SerializeToElement(updates[1], JsonSerializerOptions.Web);
        var choice = Assert.Single(wire.GetProperty("choices").EnumerateArray());
        Assert.Equal(2, choice.GetProperty("index").GetInt32());
        var call = Assert.Single(choice.GetProperty("delta").GetProperty("tool_calls").EnumerateArray());
        Assert.Equal(7, call.GetProperty("index").GetInt32());
        Assert.Equal(42, call.GetProperty("future_tool").GetInt32());
        Assert.True(wire.GetProperty("future_root").GetProperty("keep").GetBoolean());
    }

    [Fact]
    public void ExplicitStatesDoNotShareCallIndexes()
    {
        var first = new State();
        var second = new State();
        Assert.Equal(0, Call(Input("call-a").ToChatCompletionUpdate(first)).Index);
        Assert.Equal(0, Call(Input("call-b").ToChatCompletionUpdate(second)).Index);
        Assert.Equal(1, Call(Input("call-b").ToChatCompletionUpdate(first)).Index);
        Assert.Equal(1, Call(Input("call-a").ToChatCompletionUpdate(second)).Index);
    }

    [Fact]
    public async Task CancelingSequenceDisposesUpstream()
    {
        var disposed = false;
        using var cancellation = new CancellationTokenSource();
        await using var iterator = Events().ToChatCompletionUpdates(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
        Assert.True(disposed);

        async IAsyncEnumerable<AIStreamEvent> Events([EnumeratorCancellation] CancellationToken ct = default)
        {
            try
            {
                yield return Input("call-a");
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                yield return Input("call-b");
            }
            finally { disposed = true; }
        }
    }

    private static ChatCompletionToolCallChunk Call(ChatCompletionUpdate update)
        => Assert.Single(Assert.Single(update.Choices).Delta!.ToolCalls!);

    private static AIStreamEvent Input(string id)
        => Event(id, new AIToolInputAvailableEventData { ToolName = "lookup", Input = new { id } });

    private static AIStreamEvent Event(string id, object data) => new()
    {
        ProviderId = "google", Event = new AIEventEnvelope { Id = id, Type = "test", Data = data }
    };

    private static async IAsyncEnumerable<AIStreamEvent> Source(IEnumerable<AIStreamEvent> events)
    {
        foreach (var item in events)
        {
            yield return item;
            await Task.Yield();
        }
    }
}
