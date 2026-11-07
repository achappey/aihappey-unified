using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;
using static AIHappey.Tests.ChatCompletions.ChatCompletionsStreamTestHelper;

namespace AIHappey.Tests.ChatCompletions;

public sealed class ChatCompletionsFidelityTests
{
    [Fact]
    public void Captured_response_choices_keep_completed_provider_tool_finish_semantics()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"id":"chatcmpl-1","object":"chat.completion","created":1720000000,"model":"test","choices":[
              {"index":0,"message":{"role":"assistant","content":null,"tool_calls":[
                {"id":"provider-1","type":"function","provider_executed":true,
                 "function":{"name":"lookup","arguments":"{}","output":{"answer":42}}}]},
               "finish_reason":"tool_calls"}]}
            """);
        var replay = source.ToUnifiedResponse("provider").ToChatCompletion();
        Assert.Equal("stop", Assert.Single(replay.Choices).FinishReason);
        var request = JsonSerializer.SerializeToElement(new
        {
            model = "next", messages = replay.Choices.Select(choice => choice.Message)
        }).ToUnifiedRequest("provider").ToChatCompletionOptions("provider");
        Assert.DoesNotContain("provider-1", JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
    }

    [Theory]
    [InlineData("SELECT * FROM records")]
    [InlineData("{\"looks\":\"like JSON but is text\"}")]
    public void Custom_tool_calls_keep_names_and_verbatim_text_input(string input)
    {
        var source = JsonSerializer.SerializeToElement(new
        {
            model = "test",
            messages = new[]
            {
                new
                {
                    role = "assistant",
                    tool_calls = new[] { new { id = "call-custom", type = "custom", custom = new { name = "grammar", input } } }
                }
            }
        });
        var unified = source.ToUnifiedRequest("openai");
        var call = Assert.IsType<AIToolCallContentPart>(Assert.Single(Assert.Single(unified.Input!.Items!).Content!));
        Assert.Equal("grammar", call.ToolName);
        Assert.Equal(input, Assert.IsType<string>(call.Input));
        Assert.False(call.ProviderExecuted);
        var replay = JsonSerializer.SerializeToElement(unified.ToChatCompletionOptions("openai"), JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(source.GetProperty("messages")[0].GetProperty("tool_calls"),
            replay.GetProperty("messages")[0].GetProperty("tool_calls")));
    }

    [Fact]
    public void Native_message_fields_and_text_part_extensions_survive_request_replay()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"test","messages":[
              {"role":"user","name":"speaker","content":[{"type":"text","text":"Hi.","future_text":true}],"future_message":1},
              {"role":"assistant","content":"","audio":{"id":"audio-1"},"refusal":"No."},
              {"role":"assistant","content":"","function_call":{"name":"lookup","arguments":"{}"}},
              {"role":"function","name":"lookup","content":"done"}
            ]}
            """);
        var replay = JsonSerializer.SerializeToElement(source.ToUnifiedRequest("openai").ToChatCompletionOptions("openai"),
            JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(source.GetProperty("messages"), replay.GetProperty("messages")));
    }

    [Fact]
    public void Native_request_options_survive_without_overriding_normalized_controls()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"test","messages":[],"temperature":0.123456789012345,"top_p":0.7,
             "max_completion_tokens":800,"n":2,"stop":["END"],"frequency_penalty":0.2,"presence_penalty":0.3,
             "logit_bias":{"42":1},"logprobs":true,"top_logprobs":2,"seed":9876543210,
             "reasoning_effort":"high","verbosity":"low","service_tier":"auto",
             "modalities":["text","audio"],"audio":{"voice":"alloy","format":"wav"},
             "prediction":{"type":"content","content":"expected"},
             "web_search_options":{"search_context_size":"low"},"safety_identifier":"safe",
             "prompt_cache_key":"cache","prompt_cache_retention":"24h","user":"user",
             "metadata":{"tag":"value"},"future_option":{"keep":[1,true]}}
            """);
        var unified = source.ToUnifiedRequest("openai");
        Assert.Equal(0.123456789012345, unified.Temperature);
        Assert.Equal("low", unified.Verbosity);
        var replay = JsonSerializer.SerializeToElement(unified.ToChatCompletionOptions("openai"), JsonSerializerOptions.Web);

        foreach (var property in source.EnumerateObject().Where(p => p.Name != "messages"))
            Assert.True(JsonElement.DeepEquals(property.Value, replay.GetProperty(property.Name)), property.Name);
        Assert.False(replay.TryGetProperty("max_tokens", out _));
        Assert.False(replay.GetProperty("store").GetBoolean());
    }

    [Fact]
    public void Typed_headers_and_legacy_token_limit_are_preserved()
    {
        var native = new ChatCompletionOptions
        {
            Model = "test", MaxTokens = 80, TopP = 0.6,
            Headers = new() { ["x-test"] = "value" }
        };
        var replay = native.ToUnifiedRequest("openai").ToChatCompletionOptions("openai");
        Assert.Equal(80, replay.MaxTokens);
        Assert.Null(replay.MaxCompletionTokens);
        Assert.Equal(0.6, replay.TopP);
        Assert.Equal("value", replay.Headers!["x-test"]);
    }

    [Fact]
    public void Multimodal_parts_are_usable_across_protocols_and_preserved_natively()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"test","messages":[{"role":"user","content":[
              {"type":"image_url","image_url":{"url":"https://example.com/image","detail":"high"},"future":1},
              {"type":"input_audio","input_audio":{"data":"YQ==","format":"mp3"}},
              {"type":"file","file":{"file_id":"file-1","filename":"input.pdf"}},
              {"type":"future_media","payload":{"keep":true}}
            ]}]}
            """);
        var unified = source.ToUnifiedRequest("openai");
        var parts = unified.Input!.Items![0].Content!;
        Assert.Equal("https://example.com/image", Assert.IsType<AIFileContentPart>(parts[0]).Data);
        Assert.Equal("image/*", Assert.IsType<AIFileContentPart>(parts[0]).MediaType);
        Assert.Equal("audio/mpeg", Assert.IsType<AIFileContentPart>(parts[1]).MediaType);
        Assert.Equal("file-1", Assert.IsType<AIFileContentPart>(parts[2]).Data);
        var replay = JsonSerializer.SerializeToElement(unified.ToChatCompletionOptions("openai"), JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(source.GetProperty("messages")[0].GetProperty("content"),
            replay.GetProperty("messages")[0].GetProperty("content")));
    }

    [Fact]
    public void Unknown_tool_definitions_do_not_throw_or_lose_their_payload()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"test","tools":[{"type":"future_tool","config":{"enabled":true}}]}
            """);
        var replay = source.ToUnifiedRequest("openai").ToChatCompletionOptions("openai");
        Assert.True(JsonElement.DeepEquals(source.GetProperty("tools"),
            JsonSerializer.SerializeToElement(replay, JsonSerializerOptions.Web).GetProperty("tools")));
    }

    [Fact]
    public void Response_envelope_choices_and_usage_details_survive_roundtrip()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"id":"chatcmpl-original","object":"chat.completion","created":1720000000,"model":"test",
             "service_tier":"default","system_fingerprint":"fp-original","metadata":{"tag":"value"},"future":{"keep":true},
             "choices":[
               {"index":0,"message":{"role":"assistant","content":null,"refusal":"No."},"finish_reason":"content_filter","logprobs":null},
               {"index":1,"message":{"role":"assistant","content":"Yes.","audio":{"id":"audio-1","data":"YQ==","expires_at":99,"transcript":"Yes."}},
                "finish_reason":"stop","logprobs":{"content":[],"refusal":null}}],
             "usage":{"prompt_tokens":12,"completion_tokens":8,"total_tokens":20,
               "prompt_tokens_details":{"cached_tokens":3,"audio_tokens":2},
               "completion_tokens_details":{"reasoning_tokens":4,"accepted_prediction_tokens":1},"future_usage":7}}
            """);
        var unified = source.ToUnifiedResponse("openai");
        Assert.Equal("value", unified.Metadata!["tag"]);
        var replay = JsonSerializer.SerializeToElement(unified.ToChatCompletion(), JsonSerializerOptions.Web);
        foreach (var property in source.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, replay.GetProperty(property.Name)),
                $"{property.Name}: expected {property.Value}; actual {replay.GetProperty(property.Name)}");
    }

    [Fact]
    public async Task Stream_finish_preserves_native_usage_details()
    {
        var chunk = JsonSerializer.Deserialize<ChatCompletionUpdate>("""
            {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1720000000,"model":"test",
             "choices":[{"index":0,"delta":{},"finish_reason":"stop"}],
             "usage":{"prompt_tokens":12,"completion_tokens":8,"total_tokens":20,
               "completion_tokens_details":{"reasoning_tokens":4}}}
            """, JsonSerializerOptions.Web)!;
        var finish = chunk.ToUnifiedStreamEvents("openai").Single(e => e.Event.Type == "finish");
        var replay = await MapSingleUpdateAsync(finish);
        Assert.Equal(20, replay.Usage?.TotalTokens);
        Assert.Equal(4, replay.Usage?.CompletionTokensDetails?.ReasoningTokens);
    }

    [Fact]
    public async Task Synthetic_stream_finish_keeps_legacy_usage_and_filter_reason()
    {
        var replay = await MapSingleUpdateAsync(new AIStreamEvent
        {
            ProviderId = "openai",
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Data = new AIFinishEventData { FinishReason = "content-filter", InputTokens = 12, OutputTokens = 3 }
            }
        });
        Assert.Equal("content-filter", Assert.Single(replay.Choices).FinishReason);
        Assert.Null(replay.Usage);
    }
}
