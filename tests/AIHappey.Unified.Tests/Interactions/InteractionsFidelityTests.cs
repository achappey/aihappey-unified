using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsFidelityTests
{
    [Fact]
    public void Normalized_controls_override_native_values_without_mutating_configuration()
    {
        var config = new InteractionGenerationConfig
        {
            MaxOutputTokens = 800, ThinkingLevel = "high",
            AdditionalProperties = new() { ["temperature"] = JsonSerializer.SerializeToElement(0.7) }
        };
        var request = new AIRequest
        {
            ProviderId = "google", Model = "test", MaxOutputTokens = 100, Temperature = 0.3,
            Metadata = new() { ["google"] = new Dictionary<string, object?> { ["generation_config"] = config } }
        };
        var mapped = request.ToInteractionRequest("google");
        Assert.Equal(100, mapped.GenerationConfig!.MaxOutputTokens);
        Assert.Equal("high", mapped.GenerationConfig.ThinkingLevel);
        Assert.Equal(0.3, mapped.GenerationConfig.AdditionalProperties!["temperature"].GetDouble());
        Assert.Equal(800, config.MaxOutputTokens);
        Assert.Equal(0.7, config.AdditionalProperties["temperature"].GetDouble());
    }

    [Fact]
    public void Native_request_configuration_and_continuation_fields_survive_roundtrip()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"test","input":"Hi.","system_instruction":"Be helpful.","stream":true,
             "background":true,"previous_interaction_id":"previous","continuation_token":"opaque",
             "environment":"env-1","labels":{"tag":"keep"},"service_tier":"standard",
             "safety_settings":[{"category":"HARM_CATEGORY_HATE_SPEECH","threshold":"BLOCK_ONLY_HIGH"}],
             "generation_config":{"max_output_tokens":800,"seed":42,"stop_sequences":["END"],
               "thinking_level":"high","thinking_summaries":"auto","tool_choice":"auto",
               "temperature":0.3,"top_p":0.7,"speech_config":[{"voice":"Kore"}],"future_generation":{"keep":true}},
             "metadata":{"custom":"keep"},"future_request":{"enabled":true}}
            """);
        var native = source.Deserialize<InteractionRequest>(InteractionJson.Default)!;
        var unified = native.ToUnifiedRequest("google");
        Assert.Equal(0.3, unified.Temperature);
        Assert.Equal(0.7, unified.TopP);
        var replay = JsonSerializer.SerializeToElement(unified.ToInteractionRequest("google"), InteractionJson.Default);
        foreach (var property in source.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, replay.GetProperty(property.Name)), property.Name);
        Assert.False(replay.GetProperty("store").GetBoolean());
        Assert.False(native.Metadata!.ContainsKey("interactions.request.model"));
    }

    [Fact]
    public void Native_agent_requests_and_responses_do_not_become_model_interactions()
    {
        var native = JsonSerializer.Deserialize<InteractionRequest>("""
            {"agent":"deep-research","input":"Research.","agent_config":{"type":"deep-research","thinking_summaries":"auto"},
             "background":true}
            """, InteractionJson.Default)!;
        var replay = native.ToUnifiedRequest("google").ToInteractionRequest("google");
        Assert.Null(replay.Model);
        Assert.Equal("deep-research", replay.Agent);
        Assert.NotNull(replay.AgentConfig);
        Assert.Null(replay.GenerationConfig);
        var response = new Interaction { Id = "agent-response", Agent = "deep-research", Status = "completed" };
        var responseReplay = response.ToUnifiedResponse("google").ToInteraction();
        Assert.Null(responseReplay.Model);
        Assert.Equal("deep-research", responseReplay.Agent);
        Assert.Equal("agent-response", responseReplay.Id);
    }

    [Fact]
    public void Native_response_envelope_and_usage_survive_roundtrip()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"id":"interaction-original","object":"interaction","model":"test","status":"requires_continuation",
             "created":"2026-11-03T00:00:00Z","updated":"2026-11-03T00:00:01Z",
             "steps":[{"type":"model_output","content":[{"type":"text","text":"Partial answer."}]}],
             "previous_interaction_id":"previous","continuation_token":"opaque","environment_id":"env-1",
             "environment":"env-1","labels":{"tag":"keep"},"errors":[{"code":"limit","message":"Continue decoding."}],
             "usage":{"total_input_tokens":12,"total_output_tokens":8,"total_tokens":20,"total_thought_tokens":4,
               "output_tokens_by_modality":[{"modality":"text","tokens":8}],"future_usage":1},
             "metadata":{"custom":"keep"},"future_response":{"enabled":true}}
            """);
        var replay = JsonSerializer.SerializeToElement(source.Deserialize<Interaction>(InteractionJson.Default)!
            .ToUnifiedResponse("google").ToInteraction(), InteractionJson.Default);
        foreach (var property in source.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, replay.GetProperty(property.Name)), property.Name);
    }

    [Theory]
    [InlineData("stop", "completed")]
    [InlineData("tool-calls", "requires_action")]
    [InlineData("length", "incomplete")]
    [InlineData("error", "failed")]
    public void Synthetic_stream_finish_maps_protocol_status_and_usage(string reason, string status)
    {
        var streamEvent = new AIStreamEvent
        {
            ProviderId = "google",
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Data = new AIFinishEventData { Model = "test", FinishReason = reason, InputTokens = 12, OutputTokens = 8 }
            }
        };
        var completed = Assert.IsType<InteractionCompletedEvent>(streamEvent.ToInteractionStreamEvent());
        Assert.Equal(status, completed.Interaction!.Status?.Value);
        Assert.Equal(20, completed.Interaction.Usage?.TotalTokens);
    }

    [Fact]
    public void Normalized_response_usage_is_mapped_to_interaction_counters()
    {
        var replay = new AIResponse
        {
            ProviderId = "google",
            Usage = new AIUsage { InputTokens = 12, OutputTokens = 8, CachedInputTokens = 3, ReasoningTokens = 4 }
        }.ToInteraction();
        Assert.Equal(12, replay.Usage?.TotalInputTokens);
        Assert.Equal(8, replay.Usage?.TotalOutputTokens);
        Assert.Equal(20, replay.Usage?.TotalTokens);
        Assert.Equal(3, replay.Usage?.TotalCachedTokens);
        Assert.Equal(4, replay.Usage?.TotalThoughtTokens);
    }

    [Fact]
    public void Reverse_reasoning_start_preserves_signature()
    {
        var streamEvent = new AIStreamEvent
        {
            ProviderId = "google",
            Event = new AIEventEnvelope { Type = "reasoning-start", Data = new AIReasoningStartEventData { Signature = "opaque" } }
        };
        var start = Assert.IsType<InteractionStepStartEvent>(streamEvent.ToInteractionStreamEvent());
        Assert.Equal("opaque", Assert.IsType<InteractionThoughtStep>(start.Step).Signature);
    }
}
