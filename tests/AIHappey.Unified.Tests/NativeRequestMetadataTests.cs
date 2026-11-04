using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Unified.Tests;

public class NativeRequestMetadataTests
{
    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    [InlineData("responses")]
    public void VercelProviderOptionsStillConfigureNativeExecution(string endpoint)
    {
        var source = JsonSerializer.Deserialize<ChatRequest>("""
            {"model":"test/model","maxOutputTokens":100,"messages":[],"providerMetadata":{"test":{
              "service_tier":"priority","future_null":null,"headers":{"x-test":"yes"},
              "tools":[{"type":"function","name":"lookup","function":{"name":"lookup","parameters":{"type":"object"}},"input_schema":{"type":"object"}}]}}}
            """, Json)!;
        var before = JsonSerializer.Serialize(source, Json);
        var request = source.ToUnifiedRequest("test");
        object native = endpoint switch
        {
            "chat" => request.ToChatCompletionOptions("test"),
            "messages" => request.ToMessagesRequest("test"),
            _ => request.ToResponseRequest("test")
        };
        var wire = JsonSerializer.SerializeToElement(native, Json);
        Assert.Equal("priority", wire.GetProperty("service_tier").GetString());
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("future_null").ValueKind);
        Assert.Single(wire.GetProperty("tools").EnumerateArray());
        Assert.False(wire.TryGetProperty("metadata", out _));
        var headers = native switch
        {
            ChatCompletionOptions chat => chat.Headers,
            MessagesRequest messages => messages.Headers,
            ResponseRequest responses => responses.Headers,
            _ => null
        };
        Assert.Equal("yes", headers!["x-test"]);
        Assert.Equal(before, JsonSerializer.Serialize(source, Json));
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    [InlineData("responses")]
    public void NativeRootPropertiesSurviveContractAndSerializedUnifiedRoundtrips(string endpoint)
    {
        var input = endpoint switch
        {
            "chat" => "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}]",
            "messages" => "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"max_tokens\":123",
            _ => "\"input\":\"hello\""
        };
        var source = JsonSerializer.Deserialize<JsonElement>("{" + input + """
            ,"model":"test","metadata":{"user_id":"user-1","openai":"application-value"},
            "future_null":null,"future_object":{"nested":[1,true,null,{"empty":{}}]},
            "future_array":[],"future_empty":{},"future_false":false,"future_zero":0,
            "future_number":9007199254740993,"future_string":"preserve",
            "future_case":"lower","future_CASE":"upper",
            "provider":{"order":["example"],"allow_fallbacks":false},
            "providerMetadata":{"literal":"unknown root field"}
            }
            """);
        var providerId = endpoint == "messages" ? "anthropic" : "openai";
        object native = endpoint switch
        {
            "chat" => source.Deserialize<ChatCompletionOptions>(Json)!,
            "messages" => source.Deserialize<MessagesRequest>(Json)!,
            _ => source.Deserialize<ResponseRequest>(Json)!
        };
        var before = JsonSerializer.SerializeToElement(native, Json);
        AIRequest unified = native switch
        {
            ChatCompletionOptions chat => chat.ToUnifiedRequest(providerId),
            MessagesRequest messages => messages.ToUnifiedRequest(providerId),
            ResponseRequest responses => responses.ToUnifiedRequest(providerId),
            _ => throw new InvalidOperationException()
        };
        Assert.False(unified.Metadata!.ContainsKey("openai") && providerId != "openai");
        var scoped = JsonSerializer.SerializeToElement(unified.Metadata[providerId], Json);
        Assert.False(scoped.TryGetProperty("metadata", out _));
        unified = new AIRequest
        {
            ProviderId = unified.ProviderId, Model = unified.Model, Input = unified.Input,
            MaxOutputTokens = unified.MaxOutputTokens,
            Metadata = JsonSerializer.Deserialize<Dictionary<string, object?>>(
                JsonSerializer.Serialize(unified.Metadata, Json), Json)
        };
        object replay = endpoint switch
        {
            "chat" => unified.ToChatCompletionOptions(providerId),
            "messages" => unified.ToMessagesRequest(providerId),
            _ => unified.ToResponseRequest(providerId)
        };
        var result = JsonSerializer.SerializeToElement(replay, Json);
        foreach (var property in source.EnumerateObject().Where(property => property.Name.StartsWith("future_")
            || property.Name is "metadata" or "provider" or "providerMetadata"))
        {
            Assert.True(JsonElement.DeepEquals(property.Value, before.GetProperty(property.Name)), property.Name);
            Assert.True(JsonElement.DeepEquals(property.Value, result.GetProperty(property.Name)), property.Name);
            Assert.Single(result.EnumerateObject().Where(candidate => candidate.Name == property.Name));
        }
        Assert.True(JsonElement.DeepEquals(before, JsonSerializer.SerializeToElement(native, Json)));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    [InlineData("responses")]
    public void MetadataNeverBecomesProviderExecutionOptions(string endpoint)
    {
        var unified = endpoint switch
        {
            "chat" => JsonSerializer.Deserialize<ChatCompletionOptions>("""
                {"model":"test","messages":[],"metadata":{"openai":"not-options","service_tier":"application"},"service_tier":"priority"}
                """, Json)!.ToUnifiedRequest("openai"),
            "messages" => JsonSerializer.Deserialize<MessagesRequest>("""
                {"model":"test","messages":[],"max_tokens":10,"metadata":{"user_id":"user","anthropic":{"thinking":{"type":"enabled"}}}}
                """, Json)!.ToUnifiedRequest("anthropic"),
            _ => JsonSerializer.Deserialize<ResponseRequest>("""
                {"model":"test","input":"hello","metadata":{"openai":{"reasoning":{"effort":"high"}}}}
                """, Json)!.ToUnifiedRequest("openai")
        };
        if (endpoint == "chat")
            Assert.Equal("priority", unified.ToChatCompletionOptions("openai").ServiceTier);
        else if (endpoint == "messages")
            Assert.Null(unified.ToMessagesRequest("anthropic").Thinking);
        else
            Assert.Null(unified.ToResponseRequest("openai").Reasoning);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    [InlineData("responses")]
    public void UnifiedProviderOptionsAreMaterializedWithoutLeakingIntoMetadata(string endpoint)
    {
        var request = new AIRequest
        {
            ProviderId = "test", Model = "model", MaxOutputTokens = 100, Input = new() { Text = "hello" },
            Metadata = new() { ["test"] = JsonSerializer.Deserialize<JsonElement>("""
                {"service_tier":"priority","future_null":null,"future_empty":{},"future_list":[1,null],"headers":{"x-test":"yes"}}
                """) }
        };
        object native = endpoint switch
        {
            "chat" => request.ToChatCompletionOptions("test"),
            "messages" => request.ToMessagesRequest("test"),
            _ => request.ToResponseRequest("test")
        };
        var result = JsonSerializer.SerializeToElement(native, Json);
        Assert.Equal("priority", result.GetProperty("service_tier").GetString());
        Assert.Equal(JsonValueKind.Null, result.GetProperty("future_null").ValueKind);
        Assert.False(result.TryGetProperty("metadata", out _));
        Assert.False(result.TryGetProperty("headers", out _));
        Assert.False(result.TryGetProperty("test", out _));
        Assert.False(result.TryGetProperty("providerOptions", out _));
        Assert.False(result.TryGetProperty("providerMetadata", out _));
    }
}
