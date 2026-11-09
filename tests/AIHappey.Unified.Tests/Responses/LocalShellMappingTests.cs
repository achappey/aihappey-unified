using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Responses;

public sealed class LocalShellMappingTests
{
    private const string Provider = "compatible-provider";
    private const string Call = """{"type":"shell_call","id":"sh-native","call_id":"call-native","status":"completed","action":{"commands":["echo first","exit 6"],"timeout_ms":12000,"max_output_length":512},"environment":{"type":"local"}}""";
    private const string Output = """{"type":"shell_call_output","call_id":"call-native","max_output_length":512,"output":[{"stdout":"first","stderr":"","outcome":{"type":"exit","exit_code":0}},{"stdout":"partial","stderr":"","outcome":{"type":"timeout"}}]}""";

    [Fact]
    public void Native_request_pair_survives_repeated_stateless_replay_without_output_item_id()
    {
        var request = JsonSerializer.Deserialize<ResponseRequest>("{\"model\":\"test\",\"input\":[" + Call + "," + Output + "]}", ResponseJson.Default)!;
        for (var turn = 0; turn < 3; turn++)
        {
            request = request.ToUnifiedRequest(Provider).ToResponseRequest(Provider);
            Assert.Equal(2, request.Input!.Items!.Count);
            var call = Assert.IsType<ResponseShellCallItem>(request.Input.Items[0]);
            Assert.Equal("sh-native", call.Id); Assert.Equal("call-native", call.CallId);
            Assert.Equal(new[] { "echo first", "exit 6" }, call.Action.Commands);
            Assert.Equal(12000, call.Action.TimeoutMs); Assert.Equal(512, call.Action.MaxOutputLength);
            var result = Assert.IsType<ResponseShellCallOutputItem>(request.Input.Items[1]);
            Assert.Null(result.Id); Assert.Equal("call-native", result.CallId); Assert.Equal(512, result.MaxOutputLength);
            Assert.Equal("first", result.Output[0].Stdout); Assert.IsType<ResponseShellTimeoutOutcome>(result.Output[1].Outcome);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Streaming_is_client_owned_retains_full_action_and_reverses_natively(bool omitEnvironment)
    {
        var json = omitEnvironment ? Call.Replace(",\"environment\":{\"type\":\"local\"}", "") : Call;
        var call = JsonSerializer.Deserialize<ResponseStreamItem>(json, ResponseJson.Default)!;
        var state = new ResponsesUnifiedMapper.ResponseStreamMappingState { LocalShell = omitEnvironment };
        var added = new ResponseOutputItemAdded { Item = call, OutputIndex = 0 }.ToUnifiedStreamEvent(Provider, state).ToList();
        Assert.False(Assert.IsType<AIToolInputStartEventData>(added.Single().Event.Data).ProviderExecuted);
        var done = new ResponseOutputItemDone { Item = call, OutputIndex = 0 };
        var events = done.ToUnifiedStreamEvent(Provider, state).ToList();
        var available = Assert.IsType<AIToolInputAvailableEventData>(events.Last().Event.Data);
        Assert.False(available.ProviderExecuted); Assert.Equal("shell_call", available.ToolName);
        var action = JsonSerializer.SerializeToElement(available.Input);
        Assert.Equal(12000, action.GetProperty("timeout_ms").GetInt32()); Assert.Equal(512, action.GetProperty("max_output_length").GetInt32());
        Assert.Empty(done.ToUnifiedStreamEvent(Provider, state));
        var reverse = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        var reversed = Assert.IsType<ResponseOutputItemDone>(events.Last().ToResponseStreamPart(reverse));
        Assert.Equal("shell_call", reversed.Item.Type); Assert.Equal("sh-native", reversed.Item.Id); Assert.Equal("call-native", reversed.Item.CallId);
        var serialized = JsonSerializer.SerializeToElement(reversed.Item, ResponseJson.Default);
        Assert.Equal(action, serialized.GetProperty("action"), JsonElementComparer.Instance);
        var response = JsonSerializer.Deserialize<ResponseResult>("{\"id\":\"r\",\"model\":\"test\",\"status\":\"completed\",\"output\":[" + json + "]}", ResponseJson.Default)!;
        var finish = new ResponseCompleted { Response = response }.ToUnifiedStreamEvent(Provider, state).Select(e => e.Event.Data).OfType<AIFinishEventData>().Single();
        Assert.Equal("tool-calls", finish.FinishReason);
    }

    [Fact]
    public void Completion_only_recovers_local_call_and_hosted_environment_overrides_local_request_fallback()
    {
        var response = JsonSerializer.Deserialize<ResponseResult>("{\"id\":\"r\",\"model\":\"test\",\"status\":\"completed\",\"output\":[" + Call + "]}", ResponseJson.Default)!;
        var events = new ResponseCompleted { Response = response }.ToUnifiedStreamEvent(Provider, new()).ToList();
        Assert.Single(events.Select(e => e.Event.Data).OfType<AIToolInputAvailableEventData>());
        var hosted = JsonSerializer.Deserialize<ResponseStreamItem>(Call.Replace("\"local\"", "\"container_reference\",\"container_id\":\"cntr-hosted\""), ResponseJson.Default)!;
        var state = new ResponsesUnifiedMapper.ResponseStreamMappingState { LocalShell = true };
        var input = new ResponseOutputItemDone { Item = hosted }.ToUnifiedStreamEvent(Provider, state)
            .Select(e => e.Event.Data).OfType<AIToolInputAvailableEventData>().Single();
        Assert.True(input.ProviderExecuted);
    }

    [Fact]
    public void Client_chat_history_returns_native_structured_output_not_function_output()
    {
        var json = "{\"model\":\"test\",\"messages\":[{\"role\":\"assistant\",\"parts\":[{\"type\":\"tool-shell_call\",\"toolCallId\":\"sh-native\",\"providerExecuted\":false,\"state\":\"output-available\",\"callProviderMetadata\":{\"" + Provider + "\":" + Call
            + "},\"input\":{\"commands\":[\"echo first\",\"exit 6\"],\"timeout_ms\":12000,\"max_output_length\":512},\"output\":{\"isError\":false,\"content\":[],\"structuredContent\":" + Output + "}}]}]}";
        var chat = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)!;
        var request = chat.ToUnifiedRequest(Provider).ToResponseRequest(Provider);
        Assert.Collection(request.Input!.Items!,
            item => Assert.Equal("call-native", Assert.IsType<ResponseShellCallItem>(item).CallId),
            item => {
                var output = Assert.IsType<ResponseShellCallOutputItem>(item);
                Assert.Equal("call-native", output.CallId); Assert.Null(output.Id); Assert.Equal(512, output.MaxOutputLength);
                Assert.Equal("first", output.Output[0].Stdout); Assert.IsType<ResponseShellTimeoutOutcome>(output.Output[1].Outcome);
            });
    }

    [Fact]
    public void Denied_result_does_not_release_private_output()
    {
        var part = new AIToolCallContentPart
        {
            Type = "shell_call", ToolCallId = "call-native", ToolName = "shell_call", ProviderExecuted = false,
            Input = new { commands = new[] { "echo secret" }, max_output_length = 512 },
            Output = new { output = new[] { new { stdout = "private-secret", stderr = "", outcome = new { type = "exit", exit_code = 0 } } } },
            State = "approval-responded", Approval = new AIToolCallApproval { Approved = false, Reason = "Withheld result" }
        };
        var request = new AIRequest { ProviderId = Provider, Model = "test", Input = new AIInput { Items = [new AIInputItem { Type = "message", Role = "assistant", Content = [part] }] } };
        var native = request.ToResponseRequest(Provider);
        var result = Assert.IsType<ResponseShellCallOutputItem>(native.Input!.Items![1]);
        Assert.Equal("Withheld result", result.Output[0].Stderr);
        Assert.DoesNotContain("private-secret", JsonSerializer.Serialize(native, ResponseJson.Default));
    }

    [Fact]
    public void Nonstream_response_preserves_native_call_and_client_ownership()
    {
        var response = JsonSerializer.Deserialize<ResponseResult>("{\"id\":\"r\",\"model\":\"test\",\"status\":\"completed\",\"output\":[" + Call + "]}", ResponseJson.Default)!;
        var unified = response.ToUnifiedResponse(Provider);
        var part = unified.Output!.Items!.SelectMany(i => i.Content ?? []).OfType<AIToolCallContentPart>().Single();
        Assert.False(part.ProviderExecuted);
        var roundtrip = JsonSerializer.SerializeToElement(unified.ToResponseResult().Output.Single(), ResponseJson.Default);
        Assert.Equal(JsonSerializer.Deserialize<JsonElement>(Call), roundtrip, JsonElementComparer.Instance);
    }

    private sealed class JsonElementComparer : IEqualityComparer<JsonElement>
    {
        public static readonly JsonElementComparer Instance = new();
        public bool Equals(JsonElement x, JsonElement y) => JsonElement.DeepEquals(x, y);
        public int GetHashCode(JsonElement obj) => 0;
    }
}
