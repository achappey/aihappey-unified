using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsMultimodalToolResultTests
{
    private static readonly byte[] Bytes = [0, 1, 2, 255];
    private const string Base64 = "AAEC/w==";
    private const string FileUri = "https://generativelanguage.googleapis.com/v1beta/files/report";

    [Theory]
    [InlineData("typed")]
    [InlineData("json")]
    [InlineData("string")]
    [InlineData("json-string")]
    [InlineData("dictionary")]
    public void Mcp_representations_preserve_order_media_structured_content_and_errors(string representation)
    {
        var original = MixedResult();
        var json = JsonSerializer.Serialize(original, InteractionJson.Default);
        object output = representation switch
        {
            "typed" => original,
            "json" => JsonSerializer.Deserialize<JsonElement>(json),
            "string" => json,
            "json-string" => JsonSerializer.SerializeToElement(json),
            _ => JsonSerializer.Deserialize<Dictionary<string, object?>>(json)!
        };

        var result = MapRequest(output);
        var content = Blocks(result);
        Assert.Equal(new[] { "text", "image", "audio", "document", "document", "video", "text", "text" },
            content.Select(Type));
        Assert.Equal("Files ready", content[0].GetProperty("text").GetString());
        foreach (var block in content.Skip(1).Take(4))
        {
            Assert.Equal(Base64, block.GetProperty("data").GetString());
            Assert.False(block.TryGetProperty("uri", out _));
            Assert.False(block.TryGetProperty("mimeType", out _));
        }

        Assert.Equal("image/png", content[1].GetProperty("mime_type").GetString());
        Assert.Equal("audio/wav", content[2].GetProperty("mime_type").GetString());
        Assert.Equal("application/pdf", content[3].GetProperty("mime_type").GetString());
        Assert.Equal(FileUri, content[5].GetProperty("uri").GetString());
        Assert.False(content[5].TryGetProperty("data", out _));
        Assert.False(content[5].TryGetProperty("processing", out _));
        Assert.Equal("héllo 🌳", content[6].GetProperty("text").GetString());
        var structured = JsonSerializer.Deserialize<JsonElement>(content[7].GetProperty("text").GetString()!);
        Assert.Equal(9007199254740993L, structured.GetProperty("count").GetInt64());
        Assert.True(structured.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, structured.GetProperty("missing").ValueKind);
        Assert.True(result.IsError);
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("view_file", result.Name);
        AssertEquivalent(result, MapStream(output));
        Assert.DoesNotContain("private-ui-data", result.Result!.Value.GetRawText());
    }

    public static IEnumerable<object[]> SupportedMimeTypes()
    {
        foreach (var mime in new[] { "image/png", "image/jpeg", "image/webp" })
            yield return [mime, "image"];
        foreach (var mime in new[]
                 {
                     "application/pdf", "text/csv", "application/msword",
                     "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                     "application/vnd.openxmlformats-officedocument.wordprocessingml.template",
                     "application/vnd.oasis.opendocument.text", "application/vnd.ms-powerpoint",
                     "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                     "application/vnd.oasis.opendocument.presentation", "application/vnd.ms-excel",
                     "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                     "application/vnd.oasis.opendocument.spreadsheet"
                 })
            yield return [mime, "document"];
        foreach (var mime in new[]
                 {
                     "audio/wav", "audio/mp3", "audio/mpeg", "audio/aiff", "audio/aac", "audio/ogg",
                     "audio/flac", "audio/m4a", "audio/opus", "audio/webm", "audio/alaw", "audio/mulaw", "audio/l16"
                 })
            yield return [mime, "audio"];
        foreach (var mime in new[]
                 {
                     "video/mp4", "video/mpeg", "video/mov", "video/avi", "video/x-flv",
                     "video/mpg", "video/webm", "video/wmv", "video/3gpp"
                 })
            yield return [mime, "video"];
    }

    [Theory]
    [MemberData(nameof(SupportedMimeTypes))]
    public void Supported_resource_mime_types_map_both_inline_bytes_and_uri_links(string mimeType, string expectedType)
    {
        var output = new
        {
            content = new object[]
            {
                new { type = "resource", resource = new { uri = "mcp://resource/file", mimeType, blob = Base64 } },
                new { type = "resource_link", uri = FileUri, name = "file", mimeType }
            }
        };
        var result = MapRequest(output);
        var content = Blocks(result);
        Assert.Equal(new[] { expectedType, expectedType }, content.Select(Type));
        Assert.All(content, block => Assert.Equal(mimeType, block.GetProperty("mime_type").GetString()));
        Assert.Equal(Base64, content[0].GetProperty("data").GetString());
        Assert.False(content[0].TryGetProperty("uri", out _));
        Assert.Equal(FileUri, content[1].GetProperty("uri").GetString());
        Assert.False(content[1].TryGetProperty("data", out _));
        AssertEquivalent(result, MapStream(output));
    }

    [Fact]
    public void Raw_bytes_that_look_like_base64_text_are_not_decoded_heuristically()
    {
        var bytes = Encoding.UTF8.GetBytes("YWJj");
        var output = new CallToolResult
        {
            Content =
            [
                ImageContentBlock.FromBytes(bytes, "image/png"),
                AudioContentBlock.FromBytes(bytes, "audio/wav"),
                new EmbeddedResourceBlock
                {
                    Resource = BlobResourceContents.FromBytes(bytes, "mcp://file", "application/pdf")
                }
            ]
        };
        Assert.All(Blocks(MapRequest(output)), block =>
            Assert.Equal(bytes, Convert.FromBase64String(block.GetProperty("data").GetString()!)));
    }

    [Fact]
    public void Mime_alias_fields_case_parameters_and_data_url_prefixes_are_normalized()
    {
        var output = JsonSerializer.Deserialize<JsonElement>("""
            {"content":[
              {"type":"image","mime_type":" IMAGE/PNG ","data":"data:image/png;base64,AAEC/w=="},
              {"type":"resource","resource":{"uri":"mcp://csv","mimeType":"text/csv; charset=utf-8","text":"name\nCafé"}}
            ]}
            """);
        var content = Blocks(MapRequest(output));
        Assert.Equal(Base64, content[0].GetProperty("data").GetString());
        Assert.Equal("image/png", content[0].GetProperty("mime_type").GetString());
        Assert.Equal("document", Type(content[1]));
        Assert.Equal("text/csv", content[1].GetProperty("mime_type").GetString());
        Assert.Equal("name\nCafé", Encoding.UTF8.GetString(Convert.FromBase64String(content[1].GetProperty("data").GetString()!)));
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/gif")]
    [InlineData("application/octet-stream")]
    [InlineData("application/zip")]
    [InlineData("audio/not-supported")]
    [InlineData("video/not-supported")]
    [InlineData("")]
    public void Unsupported_media_becomes_descriptive_text_without_binary_or_private_metadata(string mimeType)
    {
        var output = new
        {
            content = new object[]
            {
                new
                {
                    type = "resource", resource = new
                    {
                        uri = "mcp://unsupported", name = "report", description = "Tool attachment", mimeType,
                        blob = "BINARY-PAYLOAD-DO-NOT-PRINT", _meta = new { secret = "private-ui-data" }
                    }
                },
                new { type = "image", mimeType = "image/png", data = Base64 }
            },
            structuredContent = new { status = "ready" }
        };
        var result = MapRequest(output);
        var content = Blocks(result);
        Assert.Equal(new[] { "text", "image", "text" }, content.Select(Type));
        var text = content[0].GetProperty("text").GetString()!;
        Assert.Contains("unsupported or missing MIME type", text);
        Assert.Contains("report", text);
        Assert.Contains("mcp://unsupported", text);
        Assert.Contains("Tool attachment", text);
        Assert.DoesNotContain("BINARY-PAYLOAD-DO-NOT-PRINT", result.Result!.Value.GetRawText());
        Assert.DoesNotContain("private-ui-data", result.Result.Value.GetRawText());
    }

    [Theory]
    [InlineData("file:///report.pdf")]
    [InlineData("mcp://report")]
    [InlineData("audio://generated/interactions/id")]
    [InlineData("javascript:alert(1)")]
    [InlineData("report.pdf")]
    [InlineData("")]
    public void Uri_only_resources_with_unusable_schemes_fall_back_to_text(string uri)
    {
        var result = MapRequest(new { content = new[] { new { type = "resource_link", name = "report", mimeType = "application/pdf", uri } } });
        var block = Assert.Single(Blocks(result));
        Assert.Equal("text", Type(block));
        Assert.Contains("usable HTTP(S) URI", block.GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("{\"type\":\"image\",\"mimeType\":\"image/png\"}")]
    [InlineData("{\"type\":\"audio\",\"mimeType\":\"audio/wav\",\"data\":\"not base64!\"}")]
    [InlineData("{\"type\":\"image\",\"mimeType\":\"audio/wav\",\"data\":\"AAEC/w==\"}")]
    [InlineData("{\"type\":\"resource\"}")]
    [InlineData("{\"type\":\"text\",\"text\":7}")]
    [InlineData("{\"type\":\"future\",\"data\":\"DO-NOT-PRINT\",\"_meta\":{\"secret\":true}}")]
    [InlineData("null")]
    [InlineData("7")]
    public void Malformed_or_unknown_blocks_do_not_throw_or_hide_adjacent_media(string block)
    {
        var output = JsonSerializer.Deserialize<JsonElement>($$"""
            {"content":[{{block}},{"type":"image","mimeType":"image/png","data":"AAEC/w=="}]}
            """);
        var result = MapRequest(output);
        Assert.Equal(new[] { "text", "image" }, Blocks(result).Select(Type));
        Assert.DoesNotContain("DO-NOT-PRINT", result.Result!.Value.GetRawText());
        AssertEquivalent(result, MapStream(output));
    }

    [Fact]
    public void Unusable_inline_data_can_use_a_valid_uri_instead()
    {
        var result = MapRequest(new { content = new[] { new { type = "resource", resource = new { mimeType = "video/mp4", blob = "bad!", uri = FileUri } } } });
        var block = Assert.Single(Blocks(result));
        Assert.Equal("video", Type(block));
        Assert.Equal(FileUri, block.GetProperty("uri").GetString());
        Assert.False(block.TryGetProperty("data", out _));
    }

    [Fact]
    public void Data_uri_fallback_does_not_dump_its_binary_payload_as_text()
    {
        var result = MapRequest(new { content = new[] { new { type = "resource_link", name = "report", mimeType = "application/pdf", uri = "data:application/pdf;base64," + Base64 } } });
        var block = Assert.Single(Blocks(result));
        Assert.Equal("text", Type(block));
        Assert.Contains("inline payload omitted", block.GetProperty("text").GetString());
        Assert.DoesNotContain(Base64, result.Result!.Value.GetRawText());
    }

    [Theory]
    [InlineData("{\"content\":[]}", "{}")]
    [InlineData("{\"content\":[],\"structuredContent\":{\"count\":0}}", "{\"count\":0}")]
    [InlineData("{\"structured_content\":false,\"is_error\":true}", "false")]
    [InlineData("{\"content\":[{\"type\":\"text\",\"text\":\"\"}]}", "")]
    public void Empty_results_structured_only_results_and_empty_text_have_defined_output(string json, string expectedText)
    {
        var result = MapRequest(JsonSerializer.Deserialize<JsonElement>(json));
        Assert.Equal(expectedText, Assert.Single(Blocks(result)).GetProperty("text").GetString());
        AssertEquivalent(result, MapStream(json));
    }

    [Fact]
    public void Malformed_content_array_keeps_structured_content_and_error_status()
    {
        var result = MapRequest(new { content = 7, structuredContent = new { status = "failed" }, isError = true });
        Assert.True(result.IsError);
        Assert.Equal(2, Blocks(result).Length);
        Assert.Contains("content must be an array", Blocks(result)[0].GetProperty("text").GetString());
        Assert.Contains("failed", Blocks(result)[1].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_error_metadata_overrides_mcp_status_without_losing_signature(bool isError)
    {
        var output = MixedResult();
        var metadata = new Dictionary<string, object?> { ["google"] = new Dictionary<string, object?> { ["is_error"] = isError, ["signature"] = "opaque" } };
        var result = MapRequest(output, metadata);
        Assert.Equal(isError, result.IsError);
        Assert.Equal("opaque", result.Signature);
        var streamResult = MapStream(output, new() { ["google"] = new() { ["is_error"] = isError, ["signature"] = "opaque" } });
        AssertEquivalent(result, streamResult);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("{broken json")]
    [InlineData("{\"ok\":true}")]
    public void Non_mcp_strings_remain_text_in_requests_and_unchanged_in_reverse_streams(string output)
    {
        var result = MapRequest(output);
        Assert.Equal(output, Assert.Single(Blocks(result)).GetProperty("text").GetString());
        Assert.Equal(output, MapStream(output).Result!.Value.GetString());
        Assert.Null(result.IsError);
    }

    [Fact]
    public void Non_mcp_objects_and_null_keep_the_existing_request_fallback()
    {
        Assert.Equal("{}", Assert.Single(Blocks(MapRequest(null))).GetProperty("text").GetString());
        var text = Assert.Single(Blocks(MapRequest(new { answer = 42 }))).GetProperty("text").GetString()!;
        Assert.Equal(42, JsonSerializer.Deserialize<JsonElement>(text).GetProperty("answer").GetInt32());
    }

    [Theory]
    [InlineData("{\"content\":[\"legacy text\"]}", "content")]
    [InlineData("{\"content\":[{\"text\":\"legacy text\"}]}", "content")]
    [InlineData("{\"result\":[{\"type\":\"text\",\"text\":\"legacy text\"}]}", "result")]
    public void Untyped_content_and_legacy_result_wrappers_keep_their_previous_fallback(string json, string key)
    {
        var output = JsonSerializer.Deserialize<JsonElement>(json);
        Assert.Equal("legacy text", Assert.Single(Blocks(MapRequest(output))).GetProperty("text").GetString());
        Assert.True(JsonElement.DeepEquals(output.GetProperty(key), MapStream(output).Result!.Value));
    }

    [Fact]
    public void An_error_property_alone_does_not_turn_an_ordinary_object_into_an_empty_mcp_result()
    {
        var output = new { isError = true, message = "legacy error details" };
        var result = MapRequest(output);
        Assert.Contains("legacy error details", Assert.Single(Blocks(result)).GetProperty("text").GetString());
        Assert.Null(result.IsError);
    }

    [Fact]
    public void Native_multimodal_results_replay_exactly_without_the_raw_input_shortcut()
    {
        var native = JsonSerializer.Deserialize<InteractionRequest>("""
            {"input":[{"type":"function_result","call_id":"native","name":"view_file","signature":"opaque","is_error":false,
              "result":[{"type":"text","text":"ready"},
                {"type":"image","mime_type":"image/png","data":"AAEC/w=="},
                {"type":"document","mime_type":"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet","uri":"https://example.com/sheet"},
                {"type":"audio","mime_type":"audio/wav","data":"AAEC/w==","sample_rate":24000},
                {"type":"video","mime_type":"video/mp4","uri":"https://example.com/video","processing":"agentic","future_media":true}],
              "future_result":{"keep":true}}]}
            """, InteractionJson.Default)!;
        var unified = native.ToUnifiedRequest("google");
        var part = Assert.IsType<AIToolCallContentPart>(unified.Input!.Items![0].Content![0]);
        // Inbound results stay native, rather than being normalized into MCP.
        Assert.Equal(JsonValueKind.Array, Assert.IsType<JsonElement>(part.Output).ValueKind);
        var replay = new AIRequest { ProviderId = "google", Input = new AIInput { Items = unified.Input.Items } }.ToInteractionRequest("google");
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(native.Input, InteractionJson.Default),
            JsonSerializer.SerializeToElement(replay.Input, InteractionJson.Default)));
    }

    [Fact]
    public void Native_remote_mcp_steps_are_not_rewritten_as_function_results()
    {
        var native = JsonSerializer.Deserialize<InteractionRequest>("""
            {"input":[{"type":"mcp_server_tool_result","call_id":"native","name":"view_file","server_name":"files",
              "result":{"content":[{"type":"image","mimeType":"image/png","data":"AAEC/w=="}]}}]}
            """, InteractionJson.Default)!;
        var unified = native.ToUnifiedRequest("google");
        var replay = new AIRequest { ProviderId = "google", Input = new AIInput { Items = unified.Input!.Items } }.ToInteractionRequest("google");
        Assert.IsType<InteractionMcpServerToolResultStep>(Assert.Single(replay.Input!.Steps!));
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(native.Input, InteractionJson.Default),
            JsonSerializer.SerializeToElement(replay.Input, InteractionJson.Default)));
    }

    [Fact]
    public void Combined_ui_tools_keep_parallel_call_batch_before_multimodal_results()
    {
        var tools = new[] { "call-a", "call-b" }.Select(id => new AIToolCallContentPart
        {
            Type = "tool-view_file", ToolCallId = id, ToolName = "view_file",
            Input = new { id }, Output = MixedResult(), ProviderExecuted = false, State = "output-available"
        }).ToList<AIContentPart>();
        var request = new AIRequest { ProviderId = "google", Input = new AIInput { Items = [new AIInputItem { Role = "assistant", Content = tools }] } };
        var steps = request.ToInteractionRequest("google").Input!.Steps!;
        Assert.Equal(new[] { "function_call", "function_call", "function_result", "function_result" }, steps.Select(s => s.Type));
        Assert.Equal(new[] { "call-a", "call-b" }, steps.OfType<InteractionFunctionResultStep>().Select(s => s.CallId));
        Assert.All(steps.OfType<InteractionFunctionResultStep>(), result => Assert.Equal("image", Type(Blocks(result)[1])));
    }

    [Fact]
    public void Result_name_can_still_be_resolved_from_a_preceding_call()
    {
        var request = new AIRequest
        {
            ProviderId = "google",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem { Role = "assistant", Content = [new AIToolCallContentPart { Type = "function_call", ToolCallId = "call-1", ToolName = "view_file", Input = new { } }] },
                    new AIInputItem { Role = "tool", Content = [new AIToolCallContentPart { Type = "function_call_output", ToolCallId = "call-1", Output = MixedResult() }] }
                ]
            }
        };
        var result = Assert.IsType<InteractionFunctionResultStep>(request.ToInteractionRequest("google").Input!.Steps![1]);
        Assert.Equal("view_file", result.Name);
        Assert.Equal("image", Type(Blocks(result)[1]));
    }

    [Fact]
    public void Unified_response_mapping_uses_the_same_mcp_converter_as_requests_and_streams()
    {
        var output = MixedResult();
        var response = new AIResponse
        {
            ProviderId = "google",
            Output = new AIOutput
            {
                Items = [new AIOutputItem
                {
                    Role = "assistant",
                    Content = [new AIToolCallContentPart
                    {
                        Type = "function_result", ToolCallId = "call-1", ToolName = "view_file", Output = output
                    }]
                }]
            }
        };
        var result = Assert.IsType<InteractionFunctionResultStep>(Assert.Single(response.ToInteraction().Steps!));
        AssertEquivalent(MapRequest(output), result);
    }

    [Fact]
    public void Generated_audio_and_video_replay_remain_model_media_not_function_results()
    {
        var native = new InteractionRequest
        {
            Input = new InteractionsInput(new List<InteractionStep>
            {
                new InteractionModelOutputStep
                {
                    Content =
                    [
                        new InteractionAudioContent { MimeType = "audio/wav", Data = Base64, SampleRate = 24000, Channels = 1 },
                        new InteractionVideoContent { MimeType = "video/mp4", Data = Base64, Resolution = "720p" }
                    ]
                }
            })
        };
        var unified = native.ToUnifiedRequest("google");
        var replay = new AIRequest { ProviderId = "google", Input = new AIInput { Items = unified.Input!.Items } }.ToInteractionRequest("google");
        var step = Assert.IsType<InteractionModelOutputStep>(Assert.Single(replay.Input!.Steps!));
        Assert.Equal(new[] { "audio", "video" }, step.Content!.Select(part => part.Type));
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(native.Input, InteractionJson.Default),
            JsonSerializer.SerializeToElement(replay.Input, InteractionJson.Default)));
    }

    private static CallToolResult MixedResult() => new()
    {
        IsError = true,
        Content =
        [
            new TextContentBlock { Text = "Files ready" },
            new ImageContentBlock { MimeType = "image/png", Data = Encoding.UTF8.GetBytes(Base64) },
            AudioContentBlock.FromBytes(Bytes, "audio/wav"),
            new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(Bytes, "mcp://report", "application/pdf") },
            new EmbeddedResourceBlock { Resource = new BlobResourceContents { Uri = "file:///sheet.xlsx", MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", Blob = Encoding.UTF8.GetBytes(Base64) } },
            new ResourceLinkBlock { Uri = FileUri, Name = "walkthrough.mp4", MimeType = "video/mp4" },
            new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "mcp://note", MimeType = "text/plain", Text = "héllo 🌳" } }
        ],
        StructuredContent = JsonSerializer.SerializeToElement(new { count = 9007199254740993L, ok = true, missing = (string?)null }),
        Meta = new JsonObject { ["secret"] = "private-ui-data" }
    };

    private static InteractionFunctionResultStep MapRequest(object? output, Dictionary<string, object?>? metadata = null)
    {
        var request = new AIRequest
        {
            ProviderId = "google",
            Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "tool",
                    Content = [new AIToolCallContentPart
                    {
                        Type = "function_result", ToolCallId = "call-1", ToolName = "view_file",
                        Output = output, Metadata = metadata
                    }]
                }]
            }
        };
        return Assert.IsType<InteractionFunctionResultStep>(Assert.Single(request.ToInteractionRequest("google").Input!.Steps!));
    }

    private static InteractionFunctionResultStep MapStream(
        object? output, Dictionary<string, Dictionary<string, object>>? providerMetadata = null)
    {
        var streamEvent = new AIStreamEvent
        {
            ProviderId = "google",
            Event = new AIEventEnvelope
            {
                Type = "tool-output-available", Id = "call-1",
                Data = new AIToolOutputAvailableEventData
                {
                    ToolName = "view_file", Output = output!, ProviderExecuted = false, ProviderMetadata = providerMetadata
                }
            }
        };
        return Assert.IsType<InteractionFunctionResultStep>(Assert.IsType<InteractionStepStartEvent>(streamEvent.ToInteractionStreamEvent()).Step);
    }

    private static JsonElement[] Blocks(InteractionFunctionResultStep result) => result.Result!.Value.EnumerateArray().ToArray();
    private static string? Type(JsonElement block) => block.GetProperty("type").GetString();
    private static void AssertEquivalent(InteractionFunctionResultStep expected, InteractionFunctionResultStep actual)
        => Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement<InteractionStep>(expected, InteractionJson.Default),
            JsonSerializer.SerializeToElement<InteractionStep>(actual, InteractionJson.Default)));
}
