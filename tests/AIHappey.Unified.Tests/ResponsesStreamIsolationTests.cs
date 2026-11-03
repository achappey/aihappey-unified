using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using State = AIHappey.Responses.Mapping.ResponsesUnifiedMapper.ResponseStreamMappingState;

namespace AIHappey.Unified.Tests;

public sealed class ResponsesStreamIsolationTests
{
    private const string Provider = "same-provider";
    private const string ShellAdded = """
        {"type":"response.output_item.added","output_index":0,"item":{"id":"same-item","type":"shell_call","call_id":"same-call","action":{"commands":[]}}}
        """;
    private const string ShellDone = """
        {"type":"response.output_item.done","output_index":0,"item":{"id":"same-item","type":"shell_call","call_id":"same-call","action":{"commands":[]}}}
        """;
    private const string CodeDone = """
        {"type":"response.output_item.done","output_index":0,"item":{"id":"same-code-item","type":"code_interpreter_call","status":"completed","outputs":[{"type":"logs","logs":"hello"}]}}
        """;

    [Fact]
    public void Identical_ids_and_indexes_in_interleaved_streams_keep_their_own_shell_arguments()
    {
        var first = new State();
        var second = new State();
        Start(first, "first-private-command");
        Start(second, "second-private-command");
        AssertCommand(first, "first-private-command");
        AssertCommand(second, "second-private-command");
    }

    [Fact]
    public void Output_only_stream_cannot_resolve_another_streams_shell_call()
    {
        var first = new State();
        Start(first, "private-command");
        var second = new State();
        var output = """
            {"type":"response.output_item.done","output_index":0,"item":{"id":"same-output","type":"shell_call_output","call_id":"same-call","status":"completed","output":[{"stdout":"private-output"}]}}
            """;
        Assert.DoesNotContain(Map(second, output), evt => evt.Event.Type == "tool-output-available");
        AssertCommand(first, "private-command");
    }

    [Fact]
    public async Task Concurrent_streams_with_colliding_ids_keep_their_own_correlations()
    {
        using var barrier = new Barrier(2);
        await Task.WhenAll(Enumerable.Range(0, 2).Select(index => Task.Run(() =>
        {
            var state = new State();
            Start(state, "command-" + index);
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(20)));
            AssertCommand(state, "command-" + index);
        })));
    }

    [Theory]
    [InlineData("completed", false)]
    [InlineData("incomplete", false)]
    [InlineData("failed", false)]
    [InlineData("error", false)]
    [InlineData("completed", true)]
    [InlineData("incomplete", true)]
    [InlineData("failed", true)]
    [InlineData("error", true)]
    public void Terminal_events_and_early_disposal_clear_only_the_owning_stream(string status, bool disposeEarly)
    {
        var active = new State();
        var ended = new State();
        Start(active, "active-command");
        Start(ended, "ended-command");
        var terminal = status == "error"
            ? Parse("""{"type":"error","message":"test error"}""")
            : Parse($$$"""
                {"type":"response.{{{status}}}","response":{"id":"same-response","model":"test","status":"{{{status}}}","output":[]}}
                """);
        if (disposeEarly)
        {
            using var enumerator = terminal.ToUnifiedStreamEvent(Provider, ended).GetEnumerator();
            Assert.True(enumerator.MoveNext());
        }
        else
            terminal.ToUnifiedStreamEvent(Provider, ended).ToList();

        Assert.Empty(Map(ended, Command("stale-command")));
        AssertCommand(active, "active-command");
    }

    [Fact]
    public void Partially_enumerated_nonterminal_chunk_does_not_contaminate_another_stream()
    {
        var active = new State();
        var abandoned = new State();
        Start(active, "active-command");
        Map(abandoned, ShellAdded);
        using (var enumerator = Parse(Command("abandoned-command")).ToUnifiedStreamEvent(Provider, abandoned).GetEnumerator())
            Assert.True(enumerator.MoveNext());

        Assert.Empty(Map(abandoned, Command("stale-command")));
        AssertCommand(active, "active-command");
    }

    [Fact]
    public void Conversion_exception_releases_only_its_stream_state()
    {
        var active = new State();
        var failed = new State();
        Start(active, "active-command");
        Start(failed, "failed-command");
        Assert.Throws<FormatException>(() => Map(failed, """
            {"type":"response.image_generation_call.partial_image","item_id":"image","partial_image_b64":"!","output_format":"png"}
            """));
        Assert.Empty(Map(failed, Command("stale-command")));
        AssertCommand(active, "active-command");
    }

    [Fact]
    public void Code_interpreter_deduplication_is_per_stream_not_per_async_context()
    {
        var first = new State();
        var second = new State();
        Assert.Single(Map(first, CodeDone), evt => evt.Event.Type == "tool-output-available");
        Assert.Single(Map(second, CodeDone), evt => evt.Event.Type == "tool-output-available");
        Assert.DoesNotContain(Map(first, CodeDone), evt => evt.Event.Type == "tool-output-available");
    }

    [Fact]
    public void Hosted_tool_search_queues_are_owned_by_the_explicit_state()
    {
        var first = new State();
        var second = new State();
        Map(first, SearchCall("first-call"));
        Map(second, SearchCall("second-call"));
        Assert.Equal("second-call", Assert.Single(Map(second, SearchOutput()), evt => evt.Event.Type == "tool-output-available").Event.Id);
        Assert.Equal("first-call", Assert.Single(Map(first, SearchOutput()), evt => evt.Event.Type == "tool-output-available").Event.Id);
    }

    [Fact]
    public void No_state_overloads_do_not_retain_shell_or_deduplication_state_between_calls()
    {
        Parse(ShellAdded).ToUnifiedStreamEvent(Provider).ToList();
        Assert.Empty(Parse(Command("must-not-be-remembered")).ToUnifiedStreamEvent(Provider));
        Assert.Single(Parse(CodeDone).ToUnifiedStreamEvent(Provider), evt => evt.Event.Type == "tool-output-available");
        Assert.Single(Parse(CodeDone).ToUnifiedStreamEvent(Provider), evt => evt.Event.Type == "tool-output-available");
    }

    [Fact]
    public void Reverse_states_with_identical_item_ids_accumulate_independently()
    {
        var first = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        var second = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        Event("text-start", new AITextStartEventData()).ToResponseStreamPart(first);
        Event("text-start", new AITextStartEventData()).ToResponseStreamPart(second);
        Event("text-delta", new AITextDeltaEventData { Delta = "first-private-text" }).ToResponseStreamPart(first);
        Event("text-delta", new AITextDeltaEventData { Delta = "second-private-text" }).ToResponseStreamPart(second);
        Assert.Equal("second-private-text", CompletedText(Event("finish", Finish()).ToResponseStreamPart(second)));
        Assert.Equal("first-private-text", CompletedText(Event("finish", Finish()).ToResponseStreamPart(first)));
    }

    [Fact]
    public async Task Reverse_sequence_allocates_state_for_each_interleaved_enumeration()
    {
        var enumerations = 0;
        var mapped = Source().ToResponseStreamParts();
        await using var first = mapped.GetAsyncEnumerator();
        await using var second = mapped.GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.Equal("1", await ReadCompleted(first));
        Assert.Equal("2", await ReadCompleted(second));

        async IAsyncEnumerable<AIStreamEvent> Source()
        {
            var text = Interlocked.Increment(ref enumerations).ToString();
            yield return Event("text-start", new AITextStartEventData());
            await Task.Yield();
            yield return Event("text-delta", new AITextDeltaEventData { Delta = text });
            yield return Event("finish", Finish());
        }

        static async Task<string> ReadCompleted(IAsyncEnumerator<ResponseStreamPart> enumerator)
        {
            while (await enumerator.MoveNextAsync())
                if (enumerator.Current is ResponseCompleted)
                    return CompletedText(enumerator.Current);
            throw new InvalidOperationException("Expected response.completed.");
        }
    }

    [Fact]
    public async Task Canceling_reverse_sequence_disposes_source_without_touching_an_active_stream()
    {
        var active = new State();
        Start(active, "active-command");
        using var cancellation = new CancellationTokenSource();
        var disposed = false;
        await using var enumerator = Source().ToResponseStreamParts(cancellation.Token).GetAsyncEnumerator();
        Assert.True(await enumerator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            while (await enumerator.MoveNextAsync()) { }
        });
        Assert.True(disposed);
        AssertCommand(active, "active-command");

        async IAsyncEnumerable<AIStreamEvent> Source([EnumeratorCancellation] CancellationToken token = default)
        {
            try
            {
                yield return new AIStreamEvent
                {
                    ProviderId = Provider,
                    Event = new AIEventEnvelope { Type = "text-start", Id = "same-item", Data = new AITextStartEventData() }
                };
                await Task.Delay(Timeout.Infinite, token);
            }
            finally { disposed = true; }
        }
    }

    private static void Start(State state, string command)
    {
        Map(state, ShellAdded);
        Map(state, Command(command));
    }

    private static void AssertCommand(State state, string expected)
    {
        var input = Assert.IsType<AIToolInputAvailableEventData>(
            Assert.Single(Map(state, ShellDone), evt => evt.Event.Type == "tool-input-available").Event.Data);
        Assert.Equal(expected, JsonSerializer.SerializeToElement(input.Input).GetProperty("commands")[0].GetString());
    }

    private static string Command(string command) => $$$"""
        {"type":"response.shell_call_command.added","output_index":0,"command_index":0,"command":{{{JsonSerializer.Serialize(command)}}}}
        """;

    private static string SearchCall(string id) => $$$"""
        {"type":"response.output_item.added","output_index":0,"item":{"id":"{{{id}}}","type":"tool_search_call","execution":"server","arguments":"{}"}}
        """;

    private static string SearchOutput() => """
        {"type":"response.output_item.done","output_index":0,"item":{"id":"same-search-output","type":"tool_search_output","execution":"server","tools":[]}}
        """;

    private static List<AIStreamEvent> Map(State state, string json)
        => Parse(json).ToUnifiedStreamEvent(Provider, state).ToList();

    private static ResponseStreamPart Parse(string json)
        => JsonSerializer.Deserialize<ResponseStreamPart>(json, ResponseJson.Default)!;

    private static AIStreamEvent Event(string type, object data) => new()
    {
        ProviderId = Provider,
        Event = new AIEventEnvelope { Type = type, Id = "same-item", Data = data }
    };

    private static AIFinishEventData Finish() => new() { FinishReason = "stop", Model = "test" };

    private static string CompletedText(ResponseStreamPart part)
    {
        var output = Assert.Single(Assert.IsType<ResponseCompleted>(part).Response.Output);
        return JsonSerializer.SerializeToElement(output, ResponseJson.Default)
            .GetProperty("content")[0].GetProperty("text").GetString()!;
    }
}
