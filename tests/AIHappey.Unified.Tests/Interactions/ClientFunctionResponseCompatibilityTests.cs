using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Unified.Tests.Interactions;

public class ClientFunctionResponseCompatibilityTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public void InteractionClientCallsBecomeOneChatChoiceAndMessagesToolUse(bool includeText, int callCount)
    {
        var steps = new List<InteractionStep>();
        if (includeText)
            steps.Add(new InteractionModelOutputStep { Content = [new InteractionTextContent { Text = "Looking up" }] });
        for (var i = 1; i <= callCount; i++)
            steps.Add(new InteractionFunctionCallStep
            {
                Id = $"call-{i}", Name = "lookup", Arguments = JsonSerializer.SerializeToElement(new { id = i })
            });
        var response = new Interaction { Model = "gemini", Status = "requires_action", Steps = steps }
            .ToUnifiedResponse("google");
        var before = JsonSerializer.Serialize(response);

        var chat = JsonSerializer.SerializeToElement(response.ToChatCompletion(), JsonSerializerOptions.Web);
        var choice = Assert.Single(chat.GetProperty("choices").EnumerateArray());
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        var message = choice.GetProperty("message");
        if (includeText)
            Assert.Equal("Looking up", message.GetProperty("content").GetString());
        else
            Assert.Equal(JsonValueKind.Null, message.GetProperty("content").ValueKind);
        var calls = message.GetProperty("tool_calls").EnumerateArray().ToList();
        Assert.Equal(callCount, calls.Count);
        for (var i = 0; i < callCount; i++)
        {
            Assert.Equal($"call-{i + 1}", calls[i].GetProperty("id").GetString());
            Assert.Equal("lookup", calls[i].GetProperty("function").GetProperty("name").GetString());
            Assert.Equal(i + 1, JsonSerializer.Deserialize<JsonElement>(
                calls[i].GetProperty("function").GetProperty("arguments").GetString()!).GetProperty("id").GetInt32());
        }

        var messages = response.ToMessagesResponse();
        Assert.Equal("tool_use", messages.StopReason);
        var blocks = messages.Content!.Where(block => block.Type == "tool_use").ToList();
        Assert.Equal(callCount, blocks.Count);
        for (var i = 0; i < callCount; i++)
        {
            Assert.Equal($"call-{i + 1}", blocks[i].Id);
            Assert.Equal("lookup", blocks[i].Name);
            Assert.Equal(i + 1, blocks[i].Input!.Value.GetProperty("id").GetInt32());
        }
        Assert.Equal(before, JsonSerializer.Serialize(response));
    }

    [Theory]
    [InlineData("completed", null, null, "tool_calls", "tool_use")]
    [InlineData("requires_action", null, null, "tool_calls", "tool_use")]
    [InlineData("completed", "length", "max_tokens", "length", "max_tokens")]
    [InlineData("completed", "stop", "end_turn", "stop", "end_turn")]
    public void PendingClientToolsInferReasonsButPreserveNativeReasons(string status,
        string? nativeChatReason, string? nativeMessagesReason, string chatReason, string messagesReason)
    {
        var response = Response(status, providerExecuted: false, completed: false);
        response.Metadata!["messages.response.stop_reason"] = nativeMessagesReason;
        response.Output!.Items![0].Metadata!["chatcompletions.choice.finish_reason"] = nativeChatReason;

        Assert.Equal(chatReason, Assert.Single(response.ToChatCompletion().Choices).FinishReason);
        Assert.Equal(messagesReason, response.ToMessagesResponse().StopReason);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public void CompletedOrProviderExecutedToolsDoNotRequestClientAction(bool providerExecuted, bool completed)
    {
        var response = Response("completed", providerExecuted, completed);

        Assert.Equal("stop", Assert.Single(response.ToChatCompletion().Choices).FinishReason);
        Assert.Equal("end_turn", response.ToMessagesResponse().StopReason);
    }

    private static AIResponse Response(string status, bool providerExecuted, bool completed) => new()
    {
        ProviderId = "google", Model = "gemini", Status = status, Metadata = new(),
        Output = new AIOutput { Items = [new AIOutputItem
        {
            Type = "message", Role = "assistant", Metadata = new(), Content = [new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = "call-1", ToolName = "lookup", ProviderExecuted = providerExecuted,
                State = completed ? "output-available" : "input-available", Input = new { id = 1 },
                Output = completed ? new { found = true } : null
            }]
        }] }
    };
}
