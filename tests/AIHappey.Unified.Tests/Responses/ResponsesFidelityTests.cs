using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesFidelityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Normalized_usage_aliases_do_not_create_duplicate_json_fields(bool raw)
    {
        var source = new ResponseResult
        {
            Usage = raw ? JsonSerializer.SerializeToElement(new { inputTokens = 12, outputTokens = 8, totalTokens = 20, cachedInputTokens = 3, reasoning_tokens = 4 })
                : new AIUsage { InputTokens = 12, OutputTokens = 8, TotalTokens = 20, CachedInputTokens = 3, ReasoningTokens = 4 }
        };
        var unified = source.ToUnifiedResponse("openai");
        var usage = JsonSerializer.SerializeToElement(unified.Usage, JsonSerializerOptions.Web);
        Assert.Single(usage.EnumerateObject(), property => property.Name == "inputTokens");
        Assert.Single(usage.EnumerateObject(), property => property.Name == "reasoningTokens");
        var provider = JsonSerializer.SerializeToElement(unified.Metadata!["openai"]);
        Assert.Equal(12, provider.GetProperty("usage").GetProperty("inputTokens").GetInt32());
    }

    [Fact]
    public void Absent_native_options_do_not_erase_existing_metadata_configuration()
    {
        var native = new ResponseRequest
        {
            Model = "test",
            Metadata = new() { ["responses.reasoning"] = new Reasoning { Effort = "high" } }
        };
        Assert.Equal("high", native.ToUnifiedRequest("openai").ToResponseRequest("openai").Reasoning?.Effort);
    }

    [Fact]
    public void Unknown_native_content_is_preserved_without_inventing_cross_provider_semantics()
    {
        var native = JsonSerializer.Deserialize<ResponseRequest>("""
            {"input":[{"type":"message","role":"user","content":[
              {"type":"future_media","payload":{"keep":[1,true]}}]}]}
            """, ResponseJson.Default)!;
        var unified = native.ToUnifiedRequest("openai");
        var replay = unified.ToResponseRequest("openai");
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(native.Input, ResponseJson.Default),
            JsonSerializer.SerializeToElement(replay.Input, ResponseJson.Default)));
        var foreign = unified.ToResponseRequest("another-provider");
        Assert.Empty(Assert.IsType<ResponseInputMessage>(Assert.Single(foreign.Input!.Items!)).Content.Parts!);
    }

    [Theory]
    [InlineData("""{"type":"future_item","id":"future-1","payload":{"keep":[1,true]}}""")]
    [InlineData("""{"type":"file_search_call","id":"search-1","status":"completed","queries":["test"],"results":[]}""")]
    public void Unnormalized_native_input_items_replay_only_to_the_original_provider(string item)
    {
        var native = JsonSerializer.Deserialize<ResponseRequest>("{\"input\":[" + item + "]}", ResponseJson.Default)!;
        var unified = native.ToUnifiedRequest("openai");
        var replay = unified.ToResponseRequest("openai");
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(native.Input, ResponseJson.Default),
            JsonSerializer.SerializeToElement(replay.Input, ResponseJson.Default)));
        var foreign = unified.ToResponseRequest("another-provider");
        Assert.DoesNotContain(foreign.Input!.Items!, part => part.Type == native.Input!.Items![0].Type);
    }

    [Fact]
    public void Native_request_controls_reasoning_and_extensions_survive_roundtrip()
    {
        var source = JsonSerializer.Deserialize<JsonElement>("""
            {"model":"test","input":"Hi.","instructions":"Be helpful.","temperature":0.123456789012345,"top_p":0.7,
             "max_output_tokens":800,"max_tool_calls":4,"parallel_tool_calls":false,"stream":true,
             "reasoning":{"effort":"high","summary":"auto","future_reasoning":1},
             "text":{"format":{"type":"json_object"},"verbosity":"low","future_text":true},
             "include":["reasoning.encrypted_content","web_search_call.action.sources"],
             "truncation":"auto","top_logprobs":3,"background":true,"previous_response_id":"resp-previous",
             "prompt_cache_key":"cache-1","prompt_cache_retention":"24h","service_tier":"auto",
             "safety_identifier":"safe","user":"user-1","metadata":{"tag":"keep"},
             "stream_options":{"include_obfuscation":false},"prompt":{"id":"prompt-1"},
             "context_management":[{"type":"compaction","compact_threshold":1000}],"future_option":{"keep":true}}
            """);
        var native = source.Deserialize<ResponseRequest>(ResponseJson.Default)!;
        var unified = native.ToUnifiedRequest("openai");
        Assert.Equal(4, unified.MaxToolCalls);
        Assert.Equal("cache-1", unified.Id);
        var replay = JsonSerializer.SerializeToElement(unified.ToResponseRequest("openai"), ResponseJson.Default);
        foreach (var property in source.EnumerateObject().Where(p => p.Name != "input"))
            Assert.True(JsonElement.DeepEquals(property.Value, replay.GetProperty(property.Name)), property.Name);
        Assert.False(replay.GetProperty("store").GetBoolean());
        Assert.False(native.Metadata!.ContainsKey("responses.model"));
    }

    [Fact]
    public void Native_input_images_keep_url_or_file_id_detail_and_cache_breakpoints()
    {
        var source = JsonSerializer.Deserialize<ResponseRequest>("""
            {"model":"test","input":[{"type":"message","role":"user","content":[
              {"type":"input_image","image_url":"https://example.com/image","detail":"original",
               "prompt_cache_breakpoint":{"mode":"explicit"},"future_image":true},
              {"type":"input_image","file_id":"file-image","detail":"low"},
              {"type":"input_file","file_id":"file-pdf","filename":"input.pdf","detail":"high",
               "prompt_cache_breakpoint":{"mode":"explicit"}}]}]}
            """, ResponseJson.Default)!;
        var replay = source.ToUnifiedRequest("openai").ToResponseRequest("openai");
        var content = Assert.IsType<ResponseInputMessage>(Assert.Single(replay.Input!.Items!)).Content;
        var parts = content.Parts!;
        var urlImage = Assert.IsType<InputImagePart>(parts[0]);
        Assert.Equal("https://example.com/image", urlImage.ImageUrl);
        Assert.Equal("original", urlImage.Detail);
        Assert.NotNull(urlImage.PromptCacheBreakpoint);
        Assert.True(urlImage.AdditionalProperties!["future_image"].GetBoolean());
        var fileImage = Assert.IsType<InputImagePart>(parts[1]);
        Assert.Equal("file-image", fileImage.FileId);
        Assert.Null(fileImage.ImageUrl);
        Assert.Equal("low", fileImage.Detail);
        var file = Assert.IsType<InputFilePart>(parts[2]);
        Assert.Equal("file-pdf", file.FileId);
        Assert.Equal("high", file.Detail);
        Assert.NotNull(file.PromptCacheBreakpoint);
    }

    [Fact]
    public void Native_text_logprobs_and_refusals_are_not_dropped_from_history()
    {
        var source = JsonSerializer.Deserialize<ResponseRequest>("""
            {"input":[{"type":"message","role":"assistant","content":[
              {"type":"output_text","text":"Answer.","annotations":[],"logprobs":[{"token":"Answer","logprob":-0.1}],"future_text":1},
              {"type":"refusal","refusal":"No.","future_refusal":true}]}]}
            """, ResponseJson.Default)!;
        var replay = source.ToUnifiedRequest("openai").ToResponseRequest("openai");
        var parts = Assert.IsType<ResponseInputMessage>(Assert.Single(replay.Input!.Items!)).Content.Parts!;
        Assert.NotNull(Assert.IsType<OutputTextPart>(parts[0]).Logprobs);
        Assert.Equal("No.", Assert.IsType<RefusalContentPart>(parts[1]).Refusal);
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(source.Input, ResponseJson.Default),
            JsonSerializer.SerializeToElement(replay.Input, ResponseJson.Default)));
    }

    [Fact]
    public void Response_envelope_and_nested_usage_extensions_survive_roundtrip()
    {
        var source = JsonSerializer.Deserialize<ResponseResult>("""
            {"id":"resp-original","object":"response","created_at":1720000000,"completed_at":1720000001,
             "model":"test","status":"incomplete","output":[],"temperature":0.123456789012345,"top_p":0.7,
             "incomplete_details":{"reason":"max_output_tokens"},"max_output_tokens":800,"max_tool_calls":4,
             "previous_response_id":"resp-previous","reasoning":{"effort":"high"},"metadata":{"tag":"keep"},
             "prompt_cache_diagnostics":{"hit":true},"tool_usage":{"search":1},"future_response":{"enabled":true},
             "usage":{"input_tokens":12,"output_tokens":8,"total_tokens":20,"future_usage":7,
               "input_tokens_details":{"cached_tokens":3,"future_input":2},
               "output_tokens_details":{"reasoning_tokens":4,"future_output":1}}}
            """, ResponseJson.Default)!;
        var unified = source.ToUnifiedResponse("openai");
        var usage = Assert.IsType<AIUsage>(unified.Usage);
        Assert.Equal(3, usage.CachedInputTokens);
        Assert.Equal(4, usage.ReasoningTokens);
        var chatUsage = usage.ToChatCompletionUsage();
        Assert.Equal(3, chatUsage.PromptTokensDetails?.CachedTokens);
        Assert.Equal(4, chatUsage.CompletionTokensDetails?.ReasoningTokens);
        var replay = JsonSerializer.SerializeToElement(unified.ToResponseResult(), ResponseJson.Default);
        var original = JsonSerializer.SerializeToElement(source, ResponseJson.Default);
        foreach (var name in new[] { "id", "created_at", "completed_at", "temperature", "top_p", "incomplete_details",
                     "max_tool_calls", "max_output_tokens", "previous_response_id", "reasoning", "prompt_cache_diagnostics",
                     "tool_usage", "future_response" })
            Assert.True(JsonElement.DeepEquals(original.GetProperty(name), replay.GetProperty(name)), name);
        var replayUsage = replay.GetProperty("usage");
        Assert.Equal(7, replayUsage.GetProperty("future_usage").GetInt32());
        Assert.Equal(2, replayUsage.GetProperty("input_tokens_details").GetProperty("future_input").GetInt32());
        Assert.Equal(1, replayUsage.GetProperty("output_tokens_details").GetProperty("future_output").GetInt32());
    }
}
