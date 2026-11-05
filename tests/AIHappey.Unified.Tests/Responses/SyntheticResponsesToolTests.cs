using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Output;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Responses;

public sealed class SyntheticResponsesToolTests
{
    private const string Result = """
        {"content":[{"type":"text","text":"Saved"},{"type":"resource","resource":{"uri":"file:///report.docx","mimeType":"application/octet-stream","blob":"AAEC/w=="}}],
         "structuredContent":{"file_id":"file_1","filename":"report.docx","media_type":"application/octet-stream","data_url":"data:application/octet-stream;base64,AAEC/w=="},
         "isError":false,"_meta":{"existing":{"keep":true}},"future":{"large":9007199254740993}}
        """;

    [Fact]
    public void Projection_uses_only_standard_fields_and_preserves_the_entire_MCP_result_without_mutation()
    {
        var original = JsonSerializer.Deserialize<JsonElement>(Result);
        var call = SyntheticResponsesTool.Create("download_1", "download_file", new { file_id = "file_1" }, original,
            "openai", new { openai = new { container_id = "cntr_1" } });
        var wire = JsonSerializer.SerializeToElement(call, ResponseJson.Default);
        Assert.Equal("mcp_call", wire.GetProperty("type").GetString());
        Assert.Equal("aihappey", wire.GetProperty("server_label").GetString());
        Assert.Empty(wire.EnumerateObject().Select(p => p.Name).Except(
            ["type", "id", "name", "server_label", "arguments", "output", "status", "approval_request_id", "error"]));
        var decoded = SyntheticResponsesTool.ParseResult(call.Output!);
        foreach (var property in original.EnumerateObject().Where(p => p.Name != "_meta"))
            Assert.True(JsonElement.DeepEquals(property.Value, decoded.GetProperty(property.Name)), property.Name);
        Assert.True(decoded.GetProperty("_meta").GetProperty("existing").GetProperty("keep").GetBoolean());
        Assert.Equal(Result.Replace("\r", ""), original.GetRawText().Replace("\r", ""));
        var typed = decoded.Deserialize<CallToolResult>();
        Assert.IsType<EmbeddedResourceBlock>(typed!.Content[1]);
        Assert.False(typed.IsError);
    }

    [Theory]
    [InlineData("download_file")]
    [InlineData("upload_files")]
    [InlineData("upload_generated_image")]
    [InlineData("create_agent37_session")]
    public void Completed_and_streaming_results_round_trip_without_becoming_client_tools(string name)
    {
        var call = SyntheticResponsesTool.Create("call_1", name, new { test = true }, JsonSerializer.Deserialize<JsonElement>(Result), "openai");
        var response = new ResponseResult { Id = "resp_1", Model = "test", Status = "completed", Output = [call] };
        var unified = response.ToUnifiedResponse("openai");
        var part = Assert.Single(unified.Output!.Items!.SelectMany(i => i.Content ?? []).OfType<AIToolCallContentPart>());
        Assert.True(part.ProviderExecuted);
        Assert.Equal(name, part.ToolName);
        Assert.True(JsonElement.DeepEquals(SyntheticResponsesTool.ParseResult(call.Output!), (JsonElement)part.Output!));

        var state = new ResponsesUnifiedMapper.ResponseStreamMappingState();
        var done = new ResponseOutputItemDone { Item = call.ToStreamItem(), OutputIndex = 0 };
        var events = done.ToUnifiedStreamEvent("openai", state).ToList();
        Assert.Equal(new[] { "tool-input-start", "tool-input-available", "tool-output-available" }, events.Select(e => e.Event.Type));
        Assert.True(Assert.IsType<AIToolOutputAvailableEventData>(events[2].Event.Data).ProviderExecuted);
        Assert.Empty(done.ToUnifiedStreamEvent("openai", state));
        var terminal = new ResponseCompleted { Response = response }.ToUnifiedStreamEvent("openai", state).ToList();
        Assert.Single(terminal);
        Assert.Equal("finish", terminal[0].Event.Type);
        var recovered = new ResponseCompleted { Response = response }.ToUnifiedStreamEvent("openai", new()).ToList();
        Assert.Equal(4, recovered.Count);
    }

    [Fact]
    public void Session_history_restores_ownership_metadata_and_does_not_replay_an_upstream_MCP_call()
    {
        var call = SyntheticResponsesTool.Create("session_1", "create_agent37_session", new { },
            new { content = Array.Empty<object>(), structuredContent = new { session_id = "session_saved" } },
            "agent37", new { agent37 = new { session_id = "session_saved", instance_id = "instance" } });
        var wire = JsonSerializer.SerializeToElement(call, ResponseJson.Default);
        var request = new ResponseRequest { Model = "agent37/model", Input = new ResponseInput([wire.Deserialize<ResponseMcpCallItem>(ResponseJson.Default)!]) };
        var restored = request.ToUnifiedRequest("agent37");
        var tool = Assert.Single(restored.Input!.Items!.SelectMany(i => i.Content ?? []).OfType<AIToolCallContentPart>());
        Assert.True(tool.ProviderExecuted);
        Assert.Equal("create_agent37_session", tool.ToolName);
        Assert.Equal("session_saved", ((JsonElement)tool.Output!).GetProperty("structuredContent").GetProperty("session_id").GetString());
        Assert.Empty(restored.ToResponseRequest("agent37").Input!.Items!);
        Assert.Empty(request.ToUnifiedRequest("other").Input!.Items!.SelectMany(i => i.Content ?? []));
    }

    [Fact]
    public void Shared_fallback_uses_standard_MCP_envelopes_in_stream_and_final_snapshot()
    {
        var state = new ResponsesUnifiedMapper.ResponseReverseStreamState();
        AIStreamEvent Event(string type, object data) => new() { ProviderId = "test", Event = new() { Type = type, Id = "call_1", Data = data } };
        var start = Assert.IsType<ResponseOutputItemAdded>(Event("tool-input-start", new AIToolInputStartEventData
            { ToolName = "create_session", ProviderExecuted = true }).ToResponseStreamPart(state));
        Assert.Equal("mcp_call", start.Item.Type);
        Event("tool-input-available", new AIToolInputAvailableEventData
            { ToolName = "create_session", Input = new { }, ProviderExecuted = true }).ToResponseStreamPart(state);
        var done = Assert.IsType<ResponseOutputItemDone>(Event("tool-output-available", new AIToolOutputAvailableEventData
            { ToolName = "create_session", Output = JsonSerializer.Deserialize<JsonElement>(Result), ProviderExecuted = true }).ToResponseStreamPart(state));
        var finish = Assert.IsType<ResponseCompleted>(Event("finish", new AIFinishEventData { FinishReason = "stop" }).ToResponseStreamPart(state));
        Assert.IsType<ResponseOutputMcpCall>(Assert.Single(finish.Response.Output));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(done.Item, ResponseJson.Default),
            JsonSerializer.SerializeToElement(finish.Response.Output.First(), ResponseJson.Default)));
    }

    [Theory]
    [InlineData("""{"content":[{"type":"image","mimeType":"image/png","data":"AAEC/w=="}],"isError":false}""")]
    [InlineData("""{"content":[{"type":"resource","resource":{"uri":"file:///note.txt","mimeType":"text/plain","text":"héllo 🌳"}}],"isError":false}""")]
    [InlineData("""{"content":[{"type":"text","text":"Download failed"}],"isError":true,"_meta":{"reason":"retained"}}""")]
    public void Image_text_and_error_results_remain_complete_across_repeated_projection(string json)
    {
        var original = JsonSerializer.Deserialize<JsonElement>(json);
        var first = SyntheticResponsesTool.Create("call", "download_file", new { }, original, "test");
        var result = SyntheticResponsesTool.ParseResult(first.Output!);
        var second = SyntheticResponsesTool.Create("call", "download_file", new { }, result, "test", title: "Changed fallback title");
        Assert.Equal(first.Output, second.Output);
        Assert.True(JsonElement.DeepEquals(original.GetProperty("content"), result.GetProperty("content")));
        Assert.Equal(original.GetProperty("isError").GetBoolean() ? "failed" : "completed", first.Status);
        Assert.Equal(json, original.GetRawText());
    }
}
