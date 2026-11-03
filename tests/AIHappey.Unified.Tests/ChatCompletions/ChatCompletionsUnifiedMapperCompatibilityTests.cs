using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Tests.ChatCompletions;

public sealed class ChatCompletionsUnifiedMapperCompatibilityTests
{

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Chat_completion_request_roundtrip_preserves_stream_usage_option(bool includeUsage)
    {
        var request = JsonSerializer.SerializeToElement(new
        {
            model = "test-model",
            stream = true,
            stream_options = new
            {
                include_usage = includeUsage
            }
        });

        var options = request
            .ToUnifiedRequest("openai")
            .ToChatCompletionOptions("openai");

        Assert.Equal(includeUsage, options.StreamOptions?.IncludeUsage);
    }


    [Fact]
    public void Chat_completion_requests_without_stream_options_keep_the_existing_usage_default()
    {
        var request = JsonSerializer.Deserialize<JsonElement>("""{"model":"test-model","stream":true}""");

        var options = request
            .ToUnifiedRequest("openai")
            .ToChatCompletionOptions("openai");

        Assert.True(options.StreamOptions?.IncludeUsage);
    }

    [Fact]
    public void Usage_only_stream_chunk_is_preserved_as_a_raw_chat_completion_chunk()
    {
        var chunk = JsonSerializer.Deserialize<ChatCompletionUpdate>(
            """
            {"id":"chatcmpl-1","object":"chat.completion.chunk","created":1720000000,"model":"test-model",
             "choices":[],"usage":{"prompt_tokens":12,"completion_tokens":3,"total_tokens":15}}
            """,
            JsonSerializerOptions.Web)!;

        var mapped = Assert.Single(chunk.ToUnifiedStreamEvents("openai"));

        Assert.Equal("data-chat.completion.chunk", mapped.Event.Type);
        var replay = mapped.ToChatCompletionUpdate();
        Assert.Empty(replay.Choices);
        Assert.Equal(12, replay.Usage?.PromptTokens);
        Assert.Equal(3, replay.Usage?.CompletionTokens);
        Assert.Equal(15, replay.Usage?.TotalTokens);
    }
}
