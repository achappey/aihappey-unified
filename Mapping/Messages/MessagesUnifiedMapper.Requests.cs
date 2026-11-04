using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{
    public static AIRequest ToUnifiedRequest(this MessagesRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model,
            Instructions = FlattenContentText(request.System),
            Input = new AIInput
            {
                Items = [.. request.Messages.Select(message => ToUnifiedInputItem(message, providerId))],
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.input.raw"] = request.Messages.Count == 0
                        ? null
                        : JsonSerializer.SerializeToElement(request.Messages, Json)
                }
            },
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxTokens,
            Stream = request.Stream,
            ParallelToolCalls = request.ToolChoice?.DisableParallelToolUse is bool disable ? !disable : null,
            ToolChoice = request.ToolChoice is null ? null : JsonSerializer.SerializeToElement(request.ToolChoice, Json),
            Tools = request.Tools?.Select(ToUnifiedTool).ToList(),
            Headers = request.Headers,
            Metadata = BuildUnifiedRequestMetadata(request, providerId)
        };
    }

    private static readonly string[] UnsupportedSchemaProperties =
    [
        "minimum",
    "maximum"
    ];

    private static void RemoveUnsupportedSchemaProperties(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in UnsupportedSchemaProperties)
                obj.Remove(property);

            foreach (var child in obj)
                RemoveUnsupportedSchemaProperties(child.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                RemoveUnsupportedSchemaProperties(child);
        }
    }

    private static MessagesOutputFormat? ToMessagesOutputFormat(object? responseFormat)
    {
        if (responseFormat is null)
            return null;

        var element = JsonSerializer.SerializeToElement(responseFormat);

        if (!element.TryGetProperty("type", out var type) ||
            type.GetString() != "json_schema" ||
            !element.TryGetProperty("json_schema", out var jsonSchema) ||
            !jsonSchema.TryGetProperty("schema", out var schema))
            return null;

        var node = JsonNode.Parse(schema.GetRawText());

        RemoveUnsupportedSchemaProperties(node);

        return new MessagesOutputFormat
        {
            Type = "json_schema",
            Schema = JsonSerializer.SerializeToElement(node)
        };
    }


    public static MessagesRequest ToMessagesRequest(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        var metadata = request.Metadata ?? [];
        var inputItems = request.Input?.Items ?? [];

        if (inputItems.Count == 0 && !string.IsNullOrEmpty(request.Input?.Text))
            inputItems = [new AIInputItem() {
            Role = "user",
            Content = [new AITextContentPart() {
                Text  =request.Input.Text,
                Type = "text"
            }]
        }];

        var systemFromInput = ToSystemContent(inputItems, providerId);
        var storedSystem = ExtractObject<MessagesContent>(metadata, "messages.request.system");
        var system = systemFromInput
                     ?? (storedSystem is not null && request.Instructions == FlattenContentText(storedSystem)
                         ? storedSystem
                         : !string.IsNullOrWhiteSpace(request.Instructions) ? new MessagesContent(request.Instructions) : null);

        var metadataObj = ExtractObject<MessagesRequestMetadata>(metadata, "messages.request.metadata");

        var providerTools = request.Metadata?
                   .GetMessageToolDefinitions(providerId)
                   ?? [];

        var codeExecutionType = providerTools
            .Select(tool => tool.Type)
            .FirstOrDefault(type =>
                type?.StartsWith(
                    "code_execution_",
                    StringComparison.Ordinal) == true);

        List<MessageToolDefinition>? tools = [.. request.Tools?.Select(a => ToMessageTool(a, codeExecutionType)).ToList() ?? [],
            .. request.Metadata?.GetMessageToolDefinitions(providerId) ?? []];

        var container = request.Metadata?
            .GetProviderOption<JsonElement?>(providerId, "container")
            ?? ExtractObject<JsonElement?>(metadata, "messages.request.container");

        var sourceOutputConfig = request.Metadata?.GetProviderOption<MessagesOutputConfig>(providerId, "output_config")
            ?? ExtractObject<MessagesOutputConfig>(metadata, "output_config")
            ?? ExtractObject<MessagesOutputConfig>(metadata, "messages.request.output_config");
        var outputConfig = sourceOutputConfig is null ? null
            : JsonSerializer.SerializeToElement(sourceOutputConfig, Json).Deserialize<MessagesOutputConfig>(Json);
        if (request.ResponseFormat is not null)
        {
            outputConfig ??= new();
            outputConfig.Format = ToMessagesOutputFormat(request.ResponseFormat);
        }

        if (string.IsNullOrEmpty(outputConfig?.Effort)
        && !string.IsNullOrEmpty(request.Verbosity))
        {
            outputConfig ??= new();
            outputConfig.Effort = request.Verbosity;
        }

        var messages = ToMessageParams(
            inputItems.Where(item => !IsSystemRole(item.Role)),
            providerId).ToList();
        NormalizeOnDemandCompactionMessages(messages);

        var result = new MessagesRequest
        {
            Model = request.Model,
            Headers = request.Headers,
            MaxTokens = request.MaxOutputTokens ?? request.Metadata?
                .GetProviderOption<int?>(providerId, "max_tokens")
                ?? throw new ArgumentException("Messages requires max_tokens. Set MaxOutputTokens or the provider's max_tokens option.", nameof(request)),
            Messages = messages,
            CacheControl = request.Metadata?
                .GetProviderOption<CacheControlEphemeral>(providerId, "cache_control")
                ?? ExtractObject<CacheControlEphemeral>(metadata, "messages.request.cache_control"),
            Container = container is JsonElement je
                    ? HasSkills(je)
                        ? je.Clone()
                        : ToContainerId(je)
                    : null,
            InferenceGeo = request.Metadata?
                .GetProviderOption<string>(providerId, "inference_geo")
                ?? ExtractValue<string>(metadata, "messages.request.inference_geo"),
            Metadata = metadataObj,
            ContextManagement = request.Metadata?
                .GetProviderOption<MessagesContextManagementConfig>(providerId, "context_management")
                ?? ExtractObject<MessagesContextManagementConfig>(metadata, "messages.request.context_management"),
            Compaction = request.Metadata?
                .GetProviderOption<MessagesCompactionConfig>(providerId, "compaction")
                ?? ExtractObject<MessagesCompactionConfig>(metadata, "messages.request.compaction"),
            OutputConfig = outputConfig,
            ServiceTier = request.Metadata?
                .GetProviderOption<string>(providerId, "service_tier")
                ?? ExtractValue<string>(metadata, "messages.request.service_tier"),
            StopSequences = ExtractObject<List<string>>(metadata, "messages.request.stop_sequences"),
            Stream = request.Stream,
            System = system,
            Temperature = request.Temperature,
            Thinking = request.Metadata?
                .GetProviderOption<MessagesThinkingConfig>(providerId, "thinking")
                ?? ExtractObject<MessagesThinkingConfig>(metadata, "messages.request.thinking"),
            ToolChoice = ToMessageToolChoice(request.ToolChoice, request.ParallelToolCalls),
            Tools = tools,
            TopK = ExtractValue<int?>(metadata, "messages.request.top_k"),
            TopP = request.TopP,
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "messages.request.unmapped")
        };

        if (result.Container != null)
            result.AdditionalProperties?.Remove("container");

        result = NativeRequestOptions.Apply(result, metadata, providerId);
        result.Headers = NativeRequestOptions.Headers(request.Metadata, providerId, request.Headers);

        return result;
    }

    private static MessageToolChoice ToMessageToolChoice(object? choice, bool? parallelToolCalls)
    {
        var value = JsonSerializer.SerializeToElement(choice, Json);
        var type = value.ValueKind == JsonValueKind.String ? value.GetString()
            : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("type", out var kind)
                ? kind.GetString() : "auto";
        var mappedType = type switch { "required" => "any", "function" => "tool", _ => type ?? "auto" };
        if (mappedType is not ("auto" or "any" or "tool" or "none"))
            throw new ArgumentException($"Unsupported Messages tool choice '{mappedType}'.", nameof(choice));

        // none is its own native union variant: it accepts ONLY type, not even
        // disable_parallel_tool_use=false. Null is omitted by Messages serialization.
        if (mappedType == "none") return new MessageToolChoice { Type = "none" };
        var result = new MessageToolChoice
        {
            Type = mappedType,
            DisableParallelToolUse = parallelToolCalls is bool parallel ? !parallel : null
        };
        if (value.ValueKind == JsonValueKind.Object)
        {
            if (result.DisableParallelToolUse is null && value.TryGetProperty("disable_parallel_tool_use", out var disable)
                && disable.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result.DisableParallelToolUse = disable.GetBoolean();
            if (mappedType == "tool")
            {
                var source = value.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object
                    ? function : value;
                if (!source.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(name.GetString()))
                    throw new ArgumentException("A specific Messages tool choice requires a tool name.", nameof(choice));
                result.Name = name.GetString();
            }
        }
        return result;
    }

    private static void NormalizeOnDemandCompactionMessages(List<MessageParam> messages)
    {
        var compactionMessageIndex = -1;
        var compactionBlockIndex = -1;

        // On-demand blocks are signed and replace every message summarized by
        // the request that produced them. UI clients commonly post their full
        // visible history back, so retain only the newest signed block and the
        // turns that followed it. Threshold compaction blocks are unsigned and
        // intentionally keep their append-in-history behavior.
        for (var messageIndex = messages.Count - 1; messageIndex >= 0; messageIndex--)
        {
            var blocks = messages[messageIndex].Content.Blocks;
            if (blocks is null)
                continue;

            for (var blockIndex = blocks.Count - 1; blockIndex >= 0; blockIndex--)
            {
                var block = blocks[blockIndex];
                if (!string.Equals(block.Type, "compaction", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(block.Signature))
                {
                    continue;
                }

                compactionMessageIndex = messageIndex;
                compactionBlockIndex = blockIndex;
                break;
            }

            if (compactionMessageIndex >= 0)
                break;
        }

        if (compactionMessageIndex < 0)
            return;

        var compactionMessage = messages[compactionMessageIndex];
        var compactionBlocks = compactionMessage.Content.Blocks!;
        if (compactionBlockIndex > 0)
        {
            compactionMessage.Content = CreateMessagesContentFromBlocks(
                [.. compactionBlocks.Skip(compactionBlockIndex)]);
        }

        if (compactionMessageIndex > 0)
            messages.RemoveRange(0, compactionMessageIndex);
    }

    private static bool HasSkills(JsonElement container)
    {
        return container.ValueKind == JsonValueKind.Object
            && container.TryGetProperty("skills", out var skills)
            && skills.ValueKind == JsonValueKind.Array
            && skills.GetArrayLength() > 0;
    }

    private static JsonElement? ToContainerId(JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.String)
            return container;

        if (container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return id.Clone();
        }

        return null;
    }

    private static bool IsSystemRole(string? role)
        => string.Equals(role?.Trim(), "system", StringComparison.OrdinalIgnoreCase);

    private static MessagesContent? ToSystemContent(IEnumerable<AIInputItem> items, string providerId)
    {
        var blocks = new List<MessageContentBlock>();

        foreach (var item in items.Where(item => IsSystemRole(item.Role)))
        {
            foreach (var part in item.Content ?? [])
                AppendMessageBlock(blocks, part, providerId);
        }

        return blocks.Count == 0 ? null : CreateMessagesContentFromBlocks(blocks);
    }

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(MessagesRequest request, string providerId)
    {
        var raw = JsonSerializer.SerializeToElement(request, Json);
        var metadata = new Dictionary<string, object?>
        {
            ["messages.request.raw"] = raw,
            ["messages.request.cache_control"] = request.CacheControl,
            ["messages.request.compaction"] = request.Compaction,
            ["messages.request.container"] = request.Container,
            ["messages.request.context_management"] = request.ContextManagement,
            ["messages.request.inference_geo"] = request.InferenceGeo,
            ["messages.request.metadata"] = request.Metadata,
            ["messages.request.output_config"] = request.OutputConfig,
            ["messages.request.service_tier"] = request.ServiceTier,
            ["messages.request.stop_sequences"] = request.StopSequences,
            ["messages.request.system"] = request.System,
            ["messages.request.thinking"] = request.Thinking,
            ["messages.request.tool_choice"] = request.ToolChoice,
            ["messages.request.top_k"] = request.TopK,
            ["messages.request.top_p"] = request.TopP
        };

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in raw.EnumerateObject())
        {
            metadata[$"messages.request.{prop.Name}"] = prop.Value.Clone();
            if (!MappedRequestFields.Contains(prop.Name))
                unmapped[prop.Name] = prop.Value.Clone();
        }

        metadata["messages.request.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        metadata[providerId] = NativeRequestOptions.Capture(JsonSerializer.SerializeToElement(request, MessagesJson.Default));
        return metadata;
    }

    private static AIInputItem ToUnifiedInputItem(MessageParam message, string providerId)
        => new()
        {
            Type = "message",
            Role = message.Role,
            Content = [.. ToUnifiedContentParts(message.Content, providerId)],
            Metadata = new Dictionary<string, object?>
            {
                ["messages.message.raw"] = JsonSerializer.SerializeToElement(message, Json)
            }
        };

    private static IEnumerable<MessageParam> ToMessageParams(IEnumerable<AIInputItem> items, string providerId)
    {
        var yielded = new List<MessageParam>();
        var pendingBlocks = new List<MessageContentBlock>();
        string pendingRole = "assistant";
        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);

        void FlushPending()
        {
            if (pendingBlocks.Count == 0)
                return;

            yielded.Add(new MessageParam
            {
                Role = pendingRole,
                Content = CreateMessagesContentFromBlocks([.. pendingBlocks])
            });

            pendingBlocks.Clear();
        }

        foreach (var item in items)
        {
            var itemRole = ResolveMessageRole(item);

            if (pendingBlocks.Count > 0 && !string.Equals(pendingRole, itemRole, StringComparison.Ordinal))
                FlushPending();

            pendingRole = itemRole;

            foreach (var part in item.Content ?? [])
            {
                if (part is AIToolCallContentPart toolPart)
                {
                    if (!IsToolOutputOnlyPart(toolPart) && !string.IsNullOrWhiteSpace(toolPart.ToolName)
                        && !string.IsNullOrWhiteSpace(toolPart.ToolCallId))
                    {
                        if (toolNamesByCallId.TryGetValue(toolPart.ToolCallId, out var previousName)
                            && previousName != toolPart.ToolName)
                            throw new InvalidOperationException("Conflicting tool names for the same call ID.");
                        toolNamesByCallId[toolPart.ToolCallId] = toolPart.ToolName;
                    }
                    string? correlatedName = null;
                    if (!string.IsNullOrWhiteSpace(toolPart.ToolCallId))
                        toolNamesByCallId.TryGetValue(toolPart.ToolCallId, out correlatedName);
                    foreach (var (assistantBlock, userBlock) in ToMessageToolBlocks(toolPart, providerId, correlatedName))
                    {
                        if (assistantBlock is not null)
                        {
                            if (!string.Equals(pendingRole, "assistant", StringComparison.Ordinal))
                                FlushPending();

                            pendingRole = "assistant";
                            pendingBlocks.Add(assistantBlock);
                        }

                        if (userBlock is not null)
                        {
                            FlushPending();
                            yielded.Add(new MessageParam
                            {
                                Role = "user",
                                Content = CreateMessagesContentFromBlocks([userBlock])
                            });
                        }
                    }

                    continue;
                }

                if (part is not AIFileContentPart || item.Role == "user")
                    AppendMessageBlock(pendingBlocks, part, providerId);
            }
        }

        FlushPending();

        foreach (var message in yielded)
            yield return message;
    }

    private static string ResolveMessageRole(AIInputItem item)
        => IsReasoningOnlyInputItem(item) ? "assistant" : NormalizeRole(item.Role);

    private static bool IsReasoningOnlyInputItem(AIInputItem item)
    {
        if (string.Equals(item.Type?.Trim(), "reasoning", StringComparison.OrdinalIgnoreCase))
            return true;

        return item.Content is { Count: > 0 }
               && item.Content.All(static part => part is AIReasoningContentPart);
    }

    private static void AppendMessageBlock(List<MessageContentBlock> target, AIContentPart part, string providerId)
    {
        var raw = ExtractRawBlock(part.Metadata);
        if (raw is not null)
        {
            target.Add(raw);
            return;
        }

        switch (part)
        {
            case AITextContentPart text:
                target.Add(TryCreateCompactionBlock(text, providerId, out var compactionBlock)
                    ? compactionBlock
                    : new MessageContentBlock { Type = "text", Text = text.Text });
                break;
            case AIReasoningContentPart reasoning:
                var signature = reasoning.Signature
                                ?? reasoning.Metadata?.GetProviderOption<string?>(providerId, "signature");

                if (!string.IsNullOrWhiteSpace(signature))
                {
                    target.Add(new MessageContentBlock
                    {
                        Type = "thinking",
                        Thinking = reasoning.Text,
                        Signature = signature
                    });
                }

                break;
            case AIFileContentPart file:
                var fileBlock = ToMessageFileBlock(file);
                if (fileBlock != null)
                    target.Add(fileBlock);
                break;
        }
    }


}
