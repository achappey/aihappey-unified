using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

const string provider = "consumer";
var responseRequest = JsonSerializer.Deserialize<ResponseRequest>(
    """{"model":"test","input":"hello"}""", ResponseJson.Default)!;
Check(responseRequest.ToUnifiedRequest(provider).ToResponseRequest(provider).Model == "test", "Responses request");
var chatRequest = JsonSerializer.Deserialize<ChatCompletionOptions>(
    """{"model":"test","messages":[{"role":"user","content":"hello"}]}""", JsonSerializerOptions.Web)!;
Check(chatRequest.ToUnifiedRequest(provider).ToChatCompletionOptions(provider).Model == "test", "Chat request");
var messagesRequest = JsonSerializer.Deserialize<MessagesRequest>(
    """{"model":"test","max_tokens":32,"messages":[{"role":"user","content":"hello"}]}""", MessagesJson.Default)!;
Check(messagesRequest.ToUnifiedRequest(provider).ToMessagesRequest(provider).Model == "test", "Messages request");
var interactionRequest = JsonSerializer.Deserialize<InteractionRequest>(
    """{"model":"test","input":"hello"}""", InteractionJson.Default)!;
Check(interactionRequest.ToUnifiedRequest(provider).ToInteractionRequest(provider).Model == "test", "Interactions request");

var ui = new UIMessage { Id = "ui", Role = Role.user, Parts = [new TextUIPart { Text = "hello" }] };
var unifiedRequest = new ChatRequest { Model = "test", Messages = [ui] }.ToUnifiedRequest(provider);
Check(unifiedRequest.Input!.Items![0].Content!.OfType<AITextContentPart>().Single().Text == "hello", "Vercel request");
var unifiedResponse = new AIResponse
{
    ProviderId = provider,
    Model = "test",
    Status = "completed",
    Usage = new AIUsage { InputTokens = 3, OutputTokens = 2, TotalTokens = 5 },
    Output = new AIOutput
    {
        Items = [new AIOutputItem { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "answer" }] }]
    }
};
Check(unifiedResponse.ToChatCompletion().ToUnifiedResponse(provider).NormalizedUsage!.TotalTokens == 5, "Chat response");
Check(unifiedResponse.ToResponseResult().ToUnifiedResponse(provider).Output!.Items!.Count > 0, "Responses response");
Check(unifiedResponse.ToMessagesResponse().ToUnifiedResponse(provider).Output!.Items!.Count > 0, "Messages response");
Check(unifiedResponse.ToInteraction().ToUnifiedResponse(provider).Output!.Items!.Count > 0, "Interactions response");
Check(VercelUnifiedMapper.ToUIMessage(unifiedResponse.Output.Items[0]).Parts.OfType<TextUIPart>().Single().Text == "answer", "Vercel response");

var delta = JsonSerializer.Deserialize<ResponseStreamPart>(
    """{"type":"response.output_text.delta","item_id":"text","delta":"hello"}""", ResponseJson.Default)!;
var streamEvent = delta.ToUnifiedStreamEvent(provider, new ResponsesUnifiedMapper.ResponseStreamMappingState()).Single();
Check(streamEvent.Event.ToUIMessagePart(provider).OfType<TextDeltaUIMessageStreamPart>().Single().Delta == "hello", "Vercel stream");
Check(streamEvent.ToChatCompletionUpdate().Choices.Any(), "Chat reverse stream");
var messageParts = 0;
await foreach (var part in OneEvent(streamEvent).ToMessageStreamParts())
    messageParts++;
Check(messageParts > 0, "Messages reverse stream");
Check(streamEvent.ToInteractionStreamEvent() is not null, "Interactions reverse stream");
Check(streamEvent.ToResponseStreamPart(new ResponsesUnifiedMapper.ResponseReverseStreamState()) is ResponseOutputTextDelta, "Responses reverse stream");
Check(typeof(AIRequest).Assembly == typeof(ResponsesUnifiedMapper).Assembly, "assembly ownership");
Console.WriteLine("Packaged Unified consumer: all five protocol surfaces passed without backend or source references.");

static void Check(bool condition, string operation)
{
    if (!condition)
        throw new InvalidOperationException("Package consumer failed: " + operation);
}

static async IAsyncEnumerable<AIStreamEvent> OneEvent(AIStreamEvent streamEvent)
{
    await Task.CompletedTask;
    yield return streamEvent;
}
