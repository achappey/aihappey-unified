using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Unified.Tests.Interactions;

public class GoogleEndpointCompatibilityTests
{
    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static AIRequest Map(string endpoint, string fields)
    {
        var json = Parse("{\"model\":\"gemini-3.8-flash\"," + fields + "}");
        return endpoint == "messages"
            ? json.Deserialize<MessagesRequest>(MessagesJson.Default)!.ToUnifiedRequest("google")
            : json.Deserialize<ChatCompletionOptions>()!.ToUnifiedRequest("google");
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("chat")]
    public void ScreenshotPayloadPreservesNativeToolsAndConsumesEndpointOptions(string endpoint)
    {
        var system = endpoint == "messages" ? "\"system\":\"Be honest\"," : "";
        var request = Map(endpoint, system + """
            "messages":[{"role":"user","content":"hello"}],"max_tokens":1024,
            "temperature":1,"stream":true,"store":false,"service_tier":"standard",
            "response_modalities":["text"],
            "generation_config":{"thinking_level":"low","thinking_summaries":"auto"},
            "tools":[{"type":"url_context"},{"type":"google_search","search_types":["web_search"]}]
            """);
        var before = JsonSerializer.Serialize(request);
        var mapped = request.ToInteractionRequest("google");
        var wire = JsonSerializer.SerializeToElement(mapped, InteractionJson.Default);

        Assert.False(wire.TryGetProperty("system", out _));
        Assert.False(wire.TryGetProperty("max_tokens", out _));
        Assert.Equal(1024, mapped.GenerationConfig!.MaxOutputTokens);
        Assert.Equal("low", mapped.GenerationConfig.ThinkingLevel);
        Assert.Equal("standard", mapped.ServiceTier);
        Assert.Equal("text", wire.GetProperty("response_modalities")[0].GetString());
        Assert.Collection(mapped.Tools!, tool => Assert.IsType<InteractionUrlContextTool>(tool),
            tool => Assert.Equal("web_search", Assert.Single(Assert.IsType<InteractionGoogleSearchTool>(tool).SearchTypes!)));
        if (endpoint == "messages") Assert.Equal("Be honest", mapped.SystemInstruction);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        // A later provider-options merge must not reintroduce already consumed fields.
        Assert.False(mapped.Metadata?.ContainsKey("google") == true);
    }

    [Fact]
    public void SystemAndDeveloperMessagesBecomeInstructionsNotConversationTurns()
    {
        var request = Map("chat", """
            "messages":[{"role":"system","content":"First"},
            {"role":"developer","content":[{"type":"text","text":"Second"}]},
            {"role":"user","content":"hello"}]
            """);
        var before = JsonSerializer.Serialize(request);
        var mapped = request.ToInteractionRequest("google");
        Assert.Equal("First\n\nSecond", mapped.SystemInstruction);
        Assert.IsType<InteractionUserInputStep>(Assert.Single(mapped.Input!.Steps!));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Theory]
    [InlineData("chat", "\"auto\"", "\"auto\"")]
    [InlineData("chat", "\"required\"", "\"any\"")]
    [InlineData("chat", "\"none\"", "\"none\"")]
    [InlineData("chat", "{\"type\":\"function\",\"function\":{\"name\":\"lookup\"}}", "{\"allowed_tools\":{\"mode\":\"any\",\"tools\":[\"lookup\"]}}")]
    [InlineData("messages", "{\"type\":\"auto\"}", "\"auto\"")]
    [InlineData("messages", "{\"type\":\"any\"}", "\"any\"")]
    [InlineData("messages", "{\"type\":\"none\"}", "\"none\"")]
    [InlineData("messages", "{\"type\":\"tool\",\"name\":\"lookup\"}", "{\"allowed_tools\":{\"mode\":\"any\",\"tools\":[\"lookup\"]}}")]
    public void ToolChoiceUsesInteractionUnion(string endpoint, string choice, string expected)
    {
        var request = Map(endpoint, "\"messages\":[{\"role\":\"user\",\"content\":\"hello\"}],\"max_tokens\":10,\"tool_choice\":" + choice);
        var mapped = request.ToInteractionRequest("google");
        Assert.True(JsonElement.DeepEquals(Parse(expected), JsonSerializer.SerializeToElement(mapped.GenerationConfig!.ToolChoice, InteractionJson.Default)));
        var wire = JsonSerializer.SerializeToElement(mapped, InteractionJson.Default);
        Assert.False(wire.TryGetProperty("tool_choice", out _));
        Assert.False(wire.TryGetProperty("parallel_tool_calls", out _));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    public void ClientFunctionContinuationRetainsCallsNamesAndJsonArguments(string endpoint)
    {
        var fields = endpoint == "chat" ? """
            "tools":[{"type":"function","function":{"name":"lookup","parameters":{"type":"object","properties":{"id":{"type":"integer"}}}}}],
            "messages":[{"role":"assistant","content":null,"tool_calls":[
                {"id":"call-a","type":"function","function":{"name":"lookup","arguments":"{\"id\":1}"}},
                {"id":"call-b","type":"function","function":{"name":"lookup","arguments":"{\"id\":2}"}}]},
                {"role":"tool","tool_call_id":"call-a","content":"one"},
                {"role":"tool","tool_call_id":"call-b","content":"two"}]
            """ : """
            "max_tokens":100,"tools":[{"name":"lookup","input_schema":{"type":"object","properties":{"id":{"type":"integer"}}}}],
            "messages":[{"role":"assistant","content":[
                {"type":"tool_use","id":"call-a","name":"lookup","input":{"id":1}},
                {"type":"tool_use","id":"call-b","name":"lookup","input":{"id":2}}]},
                {"role":"user","content":[{"type":"tool_result","tool_use_id":"call-a","content":"one"},
                {"type":"tool_result","tool_use_id":"call-b","content":"two"}]}]
            """;
        var mapped = Map(endpoint, fields).ToInteractionRequest("google");
        Assert.Equal("integer", Assert.IsType<InteractionFunctionTool>(Assert.Single(mapped.Tools!))
            .Parameters!.Value.GetProperty("properties").GetProperty("id").GetProperty("type").GetString());
        Assert.Collection(mapped.Input!.Steps!,
            step => AssertCall(step, "call-a", 1), step => AssertCall(step, "call-b", 2),
            step => AssertResult(step, "call-a"), step => AssertResult(step, "call-b"));
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("messages")]
    public void DuplicateNativeToolsAndFunctionsAreMergedButConflictsFail(string endpoint)
    {
        var function = endpoint == "chat"
            ? "{\"type\":\"function\",\"function\":{\"name\":\"lookup\",\"parameters\":{\"type\":\"object\"}}}"
            : "{\"name\":\"lookup\",\"input_schema\":{\"type\":\"object\"}}";
        var request = Map(endpoint, "\"messages\":[],\"max_tokens\":100,\"tools\":[{\"type\":\"url_context\"}," + function + "," + function + "]");
        request.Metadata!["google"] = Parse("""
            {"tools":[{"type":"url_context"},{"type":"function","name":"lookup","parameters":{"type":"object"}}]}
            """);
        Assert.Equal(2, request.ToInteractionRequest("google").Tools!.Count);

        request.Metadata["google"] = Parse("""
            {"tools":[{"type":"function","name":"lookup","parameters":{"type":"string"}}]}
            """);
        Assert.Contains("Conflicting", Assert.Throws<ArgumentException>(() => request.ToInteractionRequest("google")).Message);
    }

    [Theory]
    [InlineData("chat", "{\"type\":\"function\",\"function\":{\"parameters\":{\"type\":\"object\"}}}")]
    [InlineData("messages", "{\"input_schema\":{\"type\":\"object\"}}")]
    public void MissingFunctionNamesAreNotInvented(string endpoint, string tool)
    {
        var request = Map(endpoint, "\"messages\":[],\"max_tokens\":100,\"tools\":[" + tool + "]");
        Assert.Throws<ArgumentException>(() => request.ToInteractionRequest("google"));
    }

    [Theory]
    [InlineData("chat", "\"parallel_tool_calls\":false")]
    [InlineData("messages", "\"tool_choice\":{\"type\":\"auto\",\"disable_parallel_tool_use\":true}")]
    [InlineData("chat", "\"tool_choice\":{\"type\":\"custom\",\"custom\":{\"name\":\"x\"}}")]
    public void UnrepresentableToolChoiceFailsBeforeTransport(string endpoint, string choice)
    {
        var request = Map(endpoint, "\"messages\":[],\"max_tokens\":100," + choice);
        Assert.Throws<NotSupportedException>(() => request.ToInteractionRequest("google"));
    }

    [Theory]
    [InlineData("messages")]
    [InlineData("chat")]
    public void SourceRoundtripsRemainIntactAfterInteractionMapping(string endpoint)
    {
        var request = Map(endpoint, """
            "messages":[{"role":"user","content":"hello"}],"system":"Keep me",
            "max_tokens":123,"tool_choice":{"type":"auto"},
            "future_google_option":{"list":[true,null,1]},
            "generation_config":{"max_output_tokens":999,"thinking_level":"low"}
            """);
        var before = JsonSerializer.Serialize(request);
        var interaction = request.ToInteractionRequest("google");
        Assert.Equal(123, interaction.GenerationConfig!.MaxOutputTokens);
        Assert.Equal(JsonValueKind.Object, interaction.AdditionalProperties!["future_google_option"].ValueKind);
        Assert.Equal(before, JsonSerializer.Serialize(request));
        var replay = endpoint == "messages"
            ? JsonSerializer.SerializeToElement(request.ToMessagesRequest("google"), MessagesJson.Default)
            : JsonSerializer.SerializeToElement(request.ToChatCompletionOptions("google"));
        Assert.Equal("Keep me", replay.GetProperty("system").GetString());
        Assert.True(replay.TryGetProperty("tool_choice", out _));
        Assert.True(replay.TryGetProperty("future_google_option", out _));
    }

    private static void AssertCall(InteractionStep step, string id, int argument)
    {
        var call = Assert.IsType<InteractionFunctionCallStep>(step);
        Assert.Equal(id, call.Id);
        Assert.Equal("lookup", call.Name);
        Assert.Equal(argument, call.Arguments!.Value.GetProperty("id").GetInt32());
    }

    private static void AssertResult(InteractionStep step, string id)
    {
        var result = Assert.IsType<InteractionFunctionResultStep>(step);
        Assert.Equal(id, result.CallId);
        Assert.Equal("lookup", result.Name);
    }
}
