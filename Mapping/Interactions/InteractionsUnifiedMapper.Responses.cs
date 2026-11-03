using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    public static AIResponse ToUnifiedResponse(this Interaction response, string providerId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var outputItems = ToUnifiedOutputItems(response, providerId).ToList();

        return new AIResponse
        {
            ProviderId = providerId,
            Model = response.Model ?? response.Agent,
            Status = response.Status?.Value,
            Usage = response.Usage,
            Output = outputItems.Count == 0 ? null : new AIOutput { Items = outputItems },
            Metadata = CaptureInteractionMetadata(response, response.Metadata, response.AdditionalProperties, "interactions.response")
        };
    }

    public static Interaction ToInteraction(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata ?? [];
        var outputSteps = CoalesceContentSteps(ToInteractionOutputSteps(response.Output)).ToList();
        var storedModel = ExtractValue<string>(metadata, "interactions.response.model");
        var storedAgent = ExtractValue<string>(metadata, "interactions.response.agent");

        return new Interaction
        {
            Id = ExtractValue<string>(metadata, "interactions.response.id") ?? Guid.NewGuid().ToString("N"),
            Object = ExtractValue<string>(metadata, "interactions.response.object") ?? "interaction",
            Model = string.IsNullOrWhiteSpace(storedAgent) || !string.IsNullOrWhiteSpace(storedModel)
                ? response.Model ?? storedModel : storedModel,
            Agent = storedAgent,
            Status = response.Status is null ? (InteractionStatus?)null : new InteractionStatus(response.Status),
            Created = ExtractValue<string>(metadata, "interactions.response.created"),
            Updated = ExtractValue<string>(metadata, "interactions.response.updated"),
            Steps = outputSteps.Count == 0 ? null : outputSteps,
            Usage = response.Usage is AIUsage usage ? new InteractionUsage
            {
                TotalInputTokens = usage.InputTokens,
                TotalOutputTokens = usage.OutputTokens,
                TotalTokens = usage.TotalTokens ?? (usage.InputTokens is not null && usage.OutputTokens is not null
                    ? usage.InputTokens + usage.OutputTokens : null),
                TotalCachedTokens = usage.CachedInputTokens,
                TotalThoughtTokens = usage.ReasoningTokens
            } : DeserializeFromObject<InteractionUsage>(response.Usage),
            SystemInstruction = ExtractValue<string>(metadata, "interactions.response.system_instruction"),
            Tools = ExtractObject<List<InteractionTool>>(metadata, "interactions.response.tools"),
            ResponseFormat = ExtractObject<InteractionResponseFormats>(metadata, "interactions.response.response_format"),
            PreviousInteractionId = ExtractValue<string>(metadata, "interactions.response.previous_interaction_id"),
            ContinuationToken = ExtractValue<string>(metadata, "interactions.response.continuation_token"),
            Environment = ExtractObject<InteractionEnvironmentValue>(metadata, "interactions.response.environment"),
            EnvironmentId = ExtractValue<string>(metadata, "interactions.response.environment_id"),
            Errors = ExtractObject<List<InteractionErrorInfo>>(metadata, "interactions.response.errors"),
            Labels = ExtractObject<Dictionary<string, string>>(metadata, "interactions.response.labels"),
            SafetySettings = ExtractObject<List<InteractionSafetySetting>>(metadata, "interactions.response.safety_settings"),
            WebhookConfig = ExtractObject<InteractionWebhookConfig>(metadata, "interactions.response.webhook_config"),
            ServiceTier = ExtractValue<string>(metadata, "interactions.response.service_tier"),
            Input = ExtractObject<InteractionsInput>(metadata, "interactions.response.input"),
            GenerationConfig = ExtractObject<InteractionGenerationConfig>(metadata, "interactions.response.generation_config"),
            AgentConfig = ExtractObject<InteractionAgentConfig>(metadata, "interactions.response.agent_config"),
            Metadata = metadata.ContainsKey("interactions.response.metadata")
                ? ExtractObject<Dictionary<string, object?>>(metadata, "interactions.response.metadata")
                : response.Metadata,
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "interactions.response.additional_properties")
        };
    }

    private static IEnumerable<AIOutputItem> ToUnifiedOutputItems(Interaction response, string providerId)
    {
        foreach (var step in response.Steps ?? [])
        {
            switch (step)
            {
                case InteractionModelOutputStep modelOutput:
                    var parts = ToUnifiedContentParts(modelOutput.Content, providerId, mapGeneratedMediaAsTools: true).ToList();
                    if (parts.Count != 0)
                    {
                        yield return new AIOutputItem
                        {
                            Type = "message",
                            Role = "assistant",
                            Content = parts,
                            Metadata = new Dictionary<string, object?>
                            {
                                ["interactions.step.raw"] = JsonSerializer.SerializeToElement(step, Json),
                                ["interactions.steps.raw"] = JsonSerializer.SerializeToElement(response.Steps, Json)
                            }
                        };
                    }
                    break;
                default:
                    var stepParts = ToUnifiedStepParts(step, providerId).ToList();
                    if (stepParts.Count != 0)
                    {
                        yield return new AIOutputItem
                        {
                            Type = step.Type,
                            Role = "assistant",
                            Content = stepParts,
                            Metadata = new Dictionary<string, object?>
                            {
                                ["interactions.step.raw"] = JsonSerializer.SerializeToElement(step, Json),
                                ["interactions.steps.raw"] = JsonSerializer.SerializeToElement(response.Steps, Json)
                            }
                        };
                    }
                    break;
            }
        }
    }

    private static IEnumerable<InteractionStep> ToInteractionOutputSteps(AIOutput? output)
    {
        foreach (var item in output?.Items ?? [])
        {
            foreach (var part in item.Content ?? [])
            {
                if (part is AIToolCallContentPart generatedMedia
                    && generatedMedia.IsSyntheticProviderExecutedGeneratedMedia())
                {
                    var mediaContent = TryCreateInteractionGeneratedMediaContent(generatedMedia);
                    if (mediaContent is not null)
                        yield return WrapInteractionContent(mediaContent);
                    continue;
                }

                var mapped = ToInteractionStep(part);
                if (mapped is not null)
                    yield return mapped;
            }
        }
    }
}
