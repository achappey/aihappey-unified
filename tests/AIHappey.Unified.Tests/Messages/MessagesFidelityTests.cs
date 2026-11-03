using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Messages;

public sealed class MessagesFidelityTests
{
    [Fact]
    public void Deferred_tool_controls_are_available_to_other_protocol_mappers()
    {
        var native = JsonSerializer.Deserialize<MessagesRequest>("""
            {"model":"test","max_tokens":800,"messages":[],
             "tools":[{"name":"lookup","input_schema":{"type":"object"},"defer_loading":true,
               "allowed_callers":["direct","code_execution_20250825"],"strict":true}]}
            """, MessagesJson.Default)!;
        var tool = Assert.Single(native.ToUnifiedRequest("anthropic").Tools!);
        Assert.True(tool.DeferLoading);
        Assert.Equal(new[] { "direct", "code_execution_20250825" }, tool.AllowedCallers);
    }

    [Fact]
    public void Native_request_configuration_and_structured_system_survive_roundtrip()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"test","max_tokens":800,"messages":[{"role":"user","content":"Hi."}],
             "system":[{"type":"text","text":"Be helpful.","cache_control":{"type":"ephemeral"}}],
             "cache_control":{"type":"ephemeral"},"container":"container-1","inference_geo":"us",
             "metadata":{"user_id":"user-1","custom":"keep"},"service_tier":"standard_only",
             "stop_sequences":["END"],"top_k":5,"top_p":0.7,"temperature":0.5,
             "thinking":{"type":"enabled","budget_tokens":1024},
             "output_config":{"effort":"high","format":{"type":"json_schema","schema":{"type":"object","properties":{}}}},
             "speed":"fast","future_option":{"keep":true}}
            """);
        var native = source.Deserialize<MessagesRequest>(MessagesJson.Default)!;
        native.Headers = new() { ["anthropic-beta"] = "test-beta" };
        var unified = native.ToUnifiedRequest("anthropic");
        var mapped = unified.ToMessagesRequest("anthropic");
        var replay = JsonSerializer.SerializeToElement(mapped, MessagesJson.Default);
        foreach (var property in source.EnumerateObject().Where(p => p.Name != "messages"))
            Assert.True(JsonElement.DeepEquals(property.Value, replay.GetProperty(property.Name)), property.Name);
        Assert.Equal("test-beta", mapped.Headers!["anthropic-beta"]);
        Assert.Equal("Be helpful.", unified.Instructions);
    }

    [Fact]
    public void Output_configuration_is_not_mutated_by_mapping()
    {
        var outputConfig = new MessagesOutputConfig { Effort = "high" };
        var request = new AIRequest
        {
            ProviderId = "anthropic", Model = "test", MaxOutputTokens = 800, Verbosity = "low",
            ResponseFormat = new { type = "json_schema", json_schema = new { schema = new { type = "object" } } },
            Metadata = new() { ["output_config"] = outputConfig }
        };
        var mapped = request.ToMessagesRequest("anthropic");
        Assert.NotNull(mapped.OutputConfig?.Format);
        Assert.Equal("high", outputConfig.Effort);
        Assert.Null(outputConfig.Format);
    }

    [Fact]
    public void Response_preserves_envelope_and_original_text_reasoning_tool_order()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"id":"msg-original","type":"message","role":"assistant","model":"test",
             "content":[
               {"type":"text","text":"Before."},
               {"type":"thinking","thinking":"Checking.","signature":"opaque"},
               {"type":"tool_use","id":"call-1","name":"lookup","input":{"q":"test"}},
               {"type":"text","text":"After."}],
             "stop_reason":"tool_use","stop_sequence":"END","container":{"id":"container-1","expires_at":"2026-11-04T00:00:00Z"},
             "metadata":{"tag":"keep"},"future_response":{"enabled":true},
             "usage":{"input_tokens":10,"output_tokens":5,"cache_read_input_tokens":3}}
            """);
        var unified = source.Deserialize<MessagesResponse>(MessagesJson.Default)!.ToUnifiedResponse("anthropic");
        var tool = Assert.IsType<AIToolCallContentPart>(unified.Output!.Items![1].Content![0]);
        Assert.False(tool.ProviderExecuted);
        var replay = JsonSerializer.SerializeToElement(unified.ToMessagesResponse(), MessagesJson.Default);
        foreach (var property in source.EnumerateObject().Where(p => p.Name != "metadata"))
            Assert.True(JsonElement.DeepEquals(property.Value, replay.GetProperty(property.Name)), property.Name);
        Assert.Equal("keep", replay.GetProperty("metadata").GetProperty("tag").GetString());
        Assert.Equal(10, replay.GetProperty("metadata").GetProperty("anthropic").GetProperty("usage")
            .GetProperty("input_tokens").GetInt32());
    }

    [Fact]
    public void Reasoning_end_keeps_initial_signature_when_no_signature_delta_arrives()
    {
        var state = new MessagesUnifiedMapper.MessagesStreamMappingState();
        new MessageStreamPart
        {
            Type = "content_block_start", Index = 0,
            ContentBlock = new MessageContentBlock { Type = "thinking", Signature = "opaque" }
        }.ToUnifiedStreamEvents("anthropic", state).ToList();
        var end = Assert.Single(new MessageStreamPart { Type = "content_block_stop", Index = 0 }
            .ToUnifiedStreamEvents("anthropic", state));
        var reasoning = Assert.IsType<AIReasoningEndEventData>(end.Event.Data);
        Assert.Equal("opaque", reasoning.Signature);
        Assert.Equal("opaque", reasoning.ProviderMetadata!["anthropic"]["signature"]);
    }
}
