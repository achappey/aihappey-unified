using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;

namespace AIHappey.Unified.Tests.Interactions;

public class GoogleResponsesToolMappingTests
{
    [Fact]
    public void ReferenceAgentBuiltinsRemainNativeAcrossTheResponsesBridge()
    {
        var source = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"google/gemini-4-carbon","input":"Run some Python code","tools":[
              {"type":"google_maps"},
              {"type":"url_context"},
              {"type":"google_search","search_types":["web_search"]},
              {"type":"code_execution"}]}
            """, ResponseJson.Default)!;
        var original = JsonSerializer.Serialize(source, ResponseJson.Default);
        var unified = source.ToUnifiedRequest("google");
        var before = JsonSerializer.Serialize(unified);

        var mapped = unified.ToInteractionRequest("google");

        Assert.Collection(mapped.Tools!,
            tool => Assert.IsType<InteractionGoogleMapsTool>(tool),
            tool => Assert.IsType<InteractionUrlContextTool>(tool),
            tool => Assert.Equal("web_search", Assert.Single(Assert.IsType<InteractionGoogleSearchTool>(tool).SearchTypes!)),
            tool => Assert.IsType<InteractionCodeExecutionTool>(tool));
        var wire = JsonSerializer.SerializeToElement(mapped, InteractionJson.Default);
        var expected = JsonSerializer.Deserialize<JsonElement>(original).GetProperty("tools");
        Assert.True(JsonElement.DeepEquals(expected, wire.GetProperty("tools")));
        Assert.DoesNotContain(mapped.Tools!, tool => tool is InteractionFunctionTool);
        Assert.Equal(original, JsonSerializer.Serialize(source, ResponseJson.Default));
        Assert.Equal(before, JsonSerializer.Serialize(unified));
        Assert.True(JsonElement.DeepEquals(expected, JsonSerializer.SerializeToElement(
            unified.ToResponseRequest("google"), ResponseJson.Default).GetProperty("tools")));
    }

    [Theory]
    [InlineData("code_execution")]
    [InlineData("url_context")]
    [InlineData("google_search")]
    [InlineData("google_maps")]
    [InlineData("computer_use")]
    [InlineData("mcp_server")]
    [InlineData("file_search")]
    [InlineData("retrieval")]
    public void NativeToolExtensionsArePreservedWithoutInventingAFunction(string type)
    {
        var definition = JsonSerializer.SerializeToElement(new
        {
            type,
            future_google_setting = new { enabled = true, values = new object?[] { 1, null, "two" } }
        });
        var request = new ResponseRequest
        {
            Model = "google/gemini-4-carbon",
            Tools = [definition.Deserialize<ResponseToolDefinition>(ResponseJson.Default)!]
        };
        var unified = request.ToUnifiedRequest("google");
        var before = JsonSerializer.Serialize(unified);

        var mapped = Assert.Single(unified.ToInteractionRequest("google").Tools!);

        Assert.IsNotType<InteractionFunctionTool>(mapped);
        Assert.True(JsonElement.DeepEquals(definition, JsonSerializer.SerializeToElement(mapped, InteractionJson.Default)));
        Assert.Equal(before, JsonSerializer.Serialize(unified));
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("code_execution")]
    public void ExplicitClientFunctionsKeepTheirNameDescriptionAndSchema(string name)
    {
        var definition = JsonSerializer.SerializeToElement(new
        {
            type = "function", name, description = "A caller-executed function",
            parameters = new { type = "object", properties = new { code = new { type = "string" } }, required = new[] { "code" } }
        });
        var request = new ResponseRequest
        {
            Model = "google/gemini-4-carbon",
            Tools = [definition.Deserialize<ResponseToolDefinition>(ResponseJson.Default)!]
        };

        var mapped = Assert.IsType<InteractionFunctionTool>(Assert.Single(
            request.ToUnifiedRequest("google").ToInteractionRequest("google").Tools!));

        Assert.Equal(name, mapped.Name);
        Assert.Equal("A caller-executed function", mapped.Description);
        Assert.True(JsonElement.DeepEquals(definition.GetProperty("parameters"), mapped.Parameters!.Value));
    }

    [Theory]
    [InlineData("{\"type\":\"function\"}")]
    [InlineData("{\"type\":\"function\",\"name\":null}")]
    [InlineData("{\"type\":\"function\",\"name\":\" \"}")]
    public void MissingClientFunctionNamesAreRejected(string definition)
    {
        var request = new ResponseRequest
        {
            Tools = [JsonSerializer.Deserialize<ResponseToolDefinition>(definition, ResponseJson.Default)!]
        }.ToUnifiedRequest("google");

        Assert.Throws<ArgumentException>(() => request.ToInteractionRequest("google"));
    }

    [Theory]
    [InlineData("future_native_tool")]
    [InlineData("code_interpreter")]
    [InlineData("web_search")]
    public void UnsupportedNativeToolTypesFailInsteadOfBecomingClientFunctions(string type)
    {
        var request = new ResponseRequest
        {
            Tools = [new ResponseToolDefinition { Type = type }]
        }.ToUnifiedRequest("google");

        Assert.Throws<NotSupportedException>(() => request.ToInteractionRequest("google"));
    }
}
