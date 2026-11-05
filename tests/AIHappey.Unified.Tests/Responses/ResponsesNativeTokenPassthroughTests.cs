using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Unified.Tests.Responses;

public class ResponsesNativeTokenPassthroughTests
{
    public static IEnumerable<object[]> NativeTokenValues()
    {
        foreach (var name in new[] { "max_tokens", "max_completion_tokens", "future_token_option" })
            foreach (var value in new[] { "128000", "0", "-1", "1.5", "9007199254740993", "null", "\"native\"", "true", "{}", "[]", "{\"items\":[1,null,{}]}" })
                yield return [name, value];
    }

    [Theory]
    [MemberData(nameof(NativeTokenValues))]
    public void Provider_token_options_pass_through_without_promotion_or_extra_validation(string name, string value)
    {
        var native = JsonSerializer.Deserialize<ResponseRequest>(
            "{\"model\":\"anthropic/test\",\"input\":\"hello\",\"" + name + "\":" + value + "}", ResponseJson.Default)!;
        var before = JsonSerializer.SerializeToElement(native, ResponseJson.Default);
        var unified = native.ToUnifiedRequest("anthropic");
        Assert.Null(unified.MaxOutputTokens);
        var provider = JsonSerializer.SerializeToElement(unified.Metadata!["anthropic"], ResponseJson.Default);
        Assert.True(JsonElement.DeepEquals(before.GetProperty(name), provider.GetProperty(name)));
        var replay = JsonSerializer.SerializeToElement(unified.ToResponseRequest("anthropic"), ResponseJson.Default);
        Assert.True(JsonElement.DeepEquals(before.GetProperty(name), replay.GetProperty(name)));
        Assert.False(replay.TryGetProperty("max_output_tokens", out _));
        Assert.Single(replay.EnumerateObject(), property => property.Name == name);
        Assert.True(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(native, ResponseJson.Default)));
    }

    [Theory]
    [InlineData(null, 128000)]
    [InlineData(321, 321)]
    public void Responses_native_max_tokens_reaches_existing_Messages_fallback_but_primary_limit_wins(int? canonical, int expected)
    {
        var native = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"anthropic/test","input":"hello","max_tokens":128000,
             "thinking":{"type":"adaptive"},"future":{"items":[1,null,{}]},
             "metadata":{"tag":"keep","anthropic":{"max_tokens":7}}}
            """, ResponseJson.Default)!;
        native.MaxOutputTokens = canonical;
        var before = JsonSerializer.SerializeToElement(native, ResponseJson.Default);
        var unified = native.ToUnifiedRequest("anthropic");
        Assert.Equal(canonical, unified.MaxOutputTokens);
        var provider = JsonSerializer.SerializeToElement(unified.Metadata!["anthropic"], ResponseJson.Default);
        Assert.Equal(128000, provider.GetProperty("max_tokens").GetInt32());
        Assert.False(provider.TryGetProperty("max_output_tokens", out _));

        // Provider snapshots must also work after serialization without depending
        // on serialization of the unified abstract content hierarchy.
        unified = new AIRequest
        {
            ProviderId = unified.ProviderId, Model = unified.Model, Input = unified.Input,
            MaxOutputTokens = unified.MaxOutputTokens,
            Metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(unified.Metadata, ResponseJson.Default), ResponseJson.Default)
        };
        var messages = unified.ToMessagesRequest("anthropic");
        Assert.Equal(expected, messages.MaxTokens);
        Assert.Equal("adaptive", messages.Thinking!.Type);
        var body = JsonSerializer.SerializeToElement(messages, MessagesJson.Default);
        Assert.Equal(expected, body.GetProperty("max_tokens").GetInt32());
        Assert.Single(body.EnumerateObject(), property => property.Name == "max_tokens");
        Assert.False(body.TryGetProperty("max_output_tokens", out _));

        // Replaying Responses retains the original raw option, not a promoted alias.
        var responses = JsonSerializer.SerializeToElement(unified.ToResponseRequest("anthropic"), ResponseJson.Default);
        Assert.Equal(128000, responses.GetProperty("max_tokens").GetInt32());
        Assert.True(JsonElement.DeepEquals(before.GetProperty("metadata"), responses.GetProperty("metadata")));
        Assert.True(JsonElement.DeepEquals(before.GetProperty("future"), responses.GetProperty("future")));
        if (canonical is not null) Assert.Equal(canonical, responses.GetProperty("max_output_tokens").GetInt32());
        else Assert.False(responses.TryGetProperty("max_output_tokens", out _));
        Assert.True(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(native, ResponseJson.Default)));
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("messages")]
    [InlineData("chat")]
    public void Capture_excludes_only_token_fields_mapped_by_the_source_endpoint(string endpoint)
    {
        var input = endpoint == "responses" ? "\"input\":\"hello\"" : "\"messages\":[]";
        var limits = endpoint == "responses" ? "\"max_output_tokens\":321,\"max_tokens\":128000"
            : endpoint == "messages" ? "\"max_tokens\":321,\"max_output_tokens\":128000"
            : "\"max_completion_tokens\":321,\"max_tokens\":123,\"max_output_tokens\":128000";
        var json = "{\"model\":\"test\"," + input + "," + limits + "}";
        var unified = endpoint switch
        {
            "responses" => JsonSerializer.Deserialize<ResponseRequest>(json, ResponseJson.Default)!.ToUnifiedRequest("test"),
            "messages" => JsonSerializer.Deserialize<MessagesRequest>(json, MessagesJson.Default)!.ToUnifiedRequest("test"),
            _ => JsonSerializer.Deserialize<ChatCompletionOptions>(json, ResponseJson.Default)!.ToUnifiedRequest("test")
        };
        Assert.Equal(321, unified.MaxOutputTokens);
        var provider = JsonSerializer.SerializeToElement(unified.Metadata!["test"], ResponseJson.Default);
        var foreign = endpoint == "responses" ? "max_tokens" : "max_output_tokens";
        Assert.Equal(128000, provider.GetProperty(foreign).GetInt32());
        Assert.False(provider.TryGetProperty(endpoint == "responses" ? "max_output_tokens" : "max_tokens", out _));
        if (endpoint == "chat") Assert.False(provider.TryGetProperty("max_completion_tokens", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"metadata\":{\"anthropic\":{\"max_tokens\":128000}}")]
    public void Missing_limit_does_not_promote_application_metadata_or_invent_a_budget(string extra)
    {
        var native = JsonSerializer.Deserialize<ResponseRequest>(
            "{\"model\":\"anthropic/test\",\"input\":\"hello\"" + extra + "}", ResponseJson.Default)!;
        var unified = native.ToUnifiedRequest("anthropic");
        Assert.Null(unified.MaxOutputTokens);
        Assert.Throws<ArgumentException>(() => unified.ToMessagesRequest("anthropic"));
    }
}
