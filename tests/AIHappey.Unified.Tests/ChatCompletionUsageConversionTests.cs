using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Unified.Tests;

public sealed class ChatCompletionUsageConversionTests
{
    [Fact]
    public void Normalized_usage_retains_cache_and_reasoning_details()
    {
        var usage = new AIUsage
        {
            InputTokens = 12, OutputTokens = 3, CachedInputTokens = 4,
            CacheWriteInputTokens = 2, ReasoningTokens = 1
        }.ToChatCompletionUsage();
        Assert.Equal(12, usage.PromptTokens);
        Assert.Equal(3, usage.CompletionTokens);
        Assert.Equal(15, usage.TotalTokens);
        Assert.Equal(4, usage.PromptTokensDetails!.CachedTokens);
        Assert.Equal(2, usage.PromptTokensDetails.CacheWriteTokens);
        Assert.Equal(1, usage.CompletionTokensDetails!.ReasoningTokens);
    }

    [Fact]
    public void Normalized_counts_override_raw_counts_without_discarding_provider_extensions()
    {
        var usage = new AIUsage
        {
            InputTokens = 8,
            AdditionalProperties = new()
            {
                ["prompt_tokens"] = JsonSerializer.SerializeToElement(5),
                ["completion_tokens"] = JsonSerializer.SerializeToElement(2),
                ["future_usage"] = JsonSerializer.SerializeToElement(new { cost = 0.01 })
            }
        }.ToChatCompletionUsage();
        Assert.Equal(8, usage.PromptTokens);
        Assert.Equal(2, usage.CompletionTokens);
        Assert.Equal(0.01, usage.AdditionalProperties!["future_usage"].GetProperty("cost").GetDouble());
    }
}
