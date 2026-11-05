using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.ChatCompletions.Mapping;

public static partial class ChatCompletionsUnifiedMapper
{
    public static AIRequest ToUnifiedRequest(this ChatCompletionOptions request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var raw = JsonSerializer.SerializeToElement(request, Json);

        return ToUnifiedRequest(raw, providerId, request.Headers);
    }

    public static AIRequest ToUnifiedRequest(this JsonElement request, string providerId)
        => ToUnifiedRequest(request, providerId, null);

    private static AIRequest ToUnifiedRequest(JsonElement request, string providerId, Dictionary<string, string>? headers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        if (request.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Chat completions request JSON must be an object.", nameof(request));

        var model = request.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
            ? modelEl.GetString()
            : null;

        var messages = request.TryGetProperty("messages", out var msgEl) && msgEl.ValueKind == JsonValueKind.Array
            ? ParseRequestMessages(msgEl).ToList()
            : [];

        var tools = request.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == JsonValueKind.Array
            ? ParseTools(toolsEl).ToList()
            : [];

        var metadata = BuildUnifiedRequestMetadata(request);
        metadata[providerId] = NativeRequestOptions.Capture(request, "max_completion_tokens", "max_tokens");

        return new AIRequest
        {
            ProviderId = providerId,
            Model = model,
            Input = new AIInput
            {
                Items = messages,
                Metadata = new Dictionary<string, object?>
                {
                    ["chatcompletions.input.raw_messages"] = msgEl.ValueKind == JsonValueKind.Array ? msgEl.Clone() : null
                }
            },
            Temperature = ExtractValue<double?>(request, "temperature"),
            TopP = ExtractValue<double?>(request, "top_p"),
            MaxOutputTokens = ExtractValue<int?>(request, "max_completion_tokens") ?? ExtractValue<int?>(request, "max_tokens"),
            Stream = ExtractValue<bool?>(request, "stream"),
            ParallelToolCalls = ExtractValue<bool?>(request, "parallel_tool_calls"),
            ToolChoice = request.TryGetProperty("tool_choice", out var toolChoiceEl) ? toolChoiceEl.Clone() : null,
            ResponseFormat = request.TryGetProperty("response_format", out var responseFormatEl) ? responseFormatEl.Clone() : null,
            Tools = tools,
            Verbosity = ExtractValue<string>(request, "verbosity"),
            Headers = headers,
            Metadata = metadata
        };
    }

    public static ChatCompletionOptions ToChatCompletionOptions(this AIRequest request, 
        string providerId, 
        bool enforceFlatContent = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        var normalizedToolChoice = NormalizeToolChoice(request.ToolChoice, request.Tools);
        var additionalProperties = BuildChatCompletionRequestAdditionalProperties(request);

        IEnumerable<ChatCompletionToolDefinition> tools = [.. ToChatTools(request.Tools),
            .. request.Metadata.GetChatCompletionToolDefinitions(providerId) ?? []];

        var messages = ToChatMessages(request.Input, enforceFlatContent).ToList();
        var streamOptions = ExtractMetadataElement(request.Metadata, "chatcompletions.request.stream_options");
        var native = ExtractMetadataElement(request.Metadata, "chatcompletions.request.raw")
            is { ValueKind: JsonValueKind.Object } raw
                ? raw.ReadChatContract<ChatCompletionOptions>()
                : null;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            messages.Insert(0, new ChatMessage()
            {
                Role = "system",
                Content = enforceFlatContent
                    ? JsonSerializer.SerializeToElement(request.Instructions, Json)
                    : JsonSerializer.SerializeToElement(new[]
                    {
                        new
                        {
                            type = "text",
                            text = request.Instructions
                        }
                    }, Json)
            });

        var options = new ChatCompletionOptions
        {
            Model = request.Model ?? string.Empty,
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxCompletionTokens = native?.MaxCompletionTokens is not null ? request.MaxOutputTokens : null,
            MaxTokens = native?.MaxCompletionTokens is null ? request.MaxOutputTokens : native.MaxTokens,
            NumberOfChoices = native?.NumberOfChoices,
            Stop = native?.Stop,
            FrequencyPenalty = native?.FrequencyPenalty,
            PresencePenalty = native?.PresencePenalty,
            LogitBias = native?.LogitBias,
            Logprobs = native?.Logprobs,
            TopLogprobs = native?.TopLogprobs,
            Seed = native?.Seed,
            ServiceTier = native?.ServiceTier,
            Modalities = native?.Modalities,
            Verbosity = request.Verbosity ?? native?.Verbosity,
            Audio = native?.Audio,
            Prediction = native?.Prediction,
            ReasoningEffort = native?.ReasoningEffort,
            WebSearchOptions = native?.WebSearchOptions,
            SafetyIdentifier = native?.SafetyIdentifier,
            PromptCacheKey = native?.PromptCacheKey,
            PromptCacheRetention = native?.PromptCacheRetention,
            PromptCacheOptions = native?.PromptCacheOptions,
            Moderation = native?.Moderation,
            User = native?.User,
            Functions = native?.Functions,
            FunctionCall = native?.FunctionCall,
            ParallelToolCalls = request.ParallelToolCalls,
            Stream = request.Stream,
            Messages = messages,
            Tools = tools,
            ToolChoiceOption = request.ToolChoice is ChatCompletionToolChoice typedChoice ? typedChoice
                : request.ToolChoice is not null
                    && request.ToolChoice is not JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }
                    && TryGetString(request.ToolChoice) is null
                    ? ToJsonElement(request.ToolChoice).ReadChatContract<ChatCompletionToolChoice>()
                    : new ChatCompletionToolChoiceMode { Mode = normalizedToolChoice ?? "auto" },
            StreamOptions = streamOptions is { ValueKind: JsonValueKind.Object }
                ? streamOptions.Value.ReadChatContract<StreamOptions>()
                : new StreamOptions { IncludeUsage = true },
            ResponseFormat = request.ResponseFormat is ChatCompletionResponseFormat format ? format
                : request.ResponseFormat is null ? null
                : ToJsonElement(request.ResponseFormat).ReadChatContract<ChatCompletionResponseFormat>(),
            Headers = request.Headers,
            Metadata = ExtractMetadataElement(request.Metadata, "chatcompletions.request.metadata")
                is { ValueKind: JsonValueKind.Object } nativeMetadata
                    ? nativeMetadata.ReadChatContract<Dictionary<string, string>>() : null,
            Store = false,
            AdditionalProperties = additionalProperties
        };

        var result = NativeRequestOptions.Apply(options, request.Metadata, providerId);
        result.Headers = NativeRequestOptions.Headers(request.Metadata, providerId, request.Headers);
        return result;
    }

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(JsonElement request)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["chatcompletions.request.raw"] = request.Clone()
        };

        foreach (var prop in request.EnumerateObject())
            metadata[$"chatcompletions.request.{prop.Name}"] = prop.Value.Clone();

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in request.EnumerateObject())
        {
            if (!MappedRequestFields.Contains(prop.Name))
                unmapped[prop.Name] = prop.Value.Clone();
        }

        metadata["chatcompletions.request.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        return metadata;
    }
}
