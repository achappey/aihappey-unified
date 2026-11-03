using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Responses.Mapping;

public static partial class ResponsesUnifiedMapper
{
    private static IEnumerable<AIContentPart> ToUnifiedContentParts(ResponseMessageContent content, string providerId)
    {
        if (content.IsText && !string.IsNullOrWhiteSpace(content.Text))
        {
            yield return new AITextContentPart { Type = "text", Text = content.Text! };
            yield break;
        }

        if (!content.IsParts || content.Parts is null)
            yield break;

        foreach (var part in content.Parts)
        {
            switch (part)
            {
                case InputTextPart inputText:
                    yield return new AITextContentPart { Type = "text", Text = inputText.Text, Metadata = new Dictionary<string, object?> { ["responses.type"] = "input_text", ["responses.prompt_cache_breakpoint"] = inputText.PromptCacheBreakpoint, ["responses.additional_properties"] = inputText.AdditionalProperties } };
                    break;
                case OutputTextPart outputText:
                    yield return new AITextContentPart { Type = "text", Text = outputText.Text, Metadata = new Dictionary<string, object?> { ["responses.type"] = "output_text", ["responses.annotations"] = outputText.Annotations, ["responses.logprobs"] = outputText.Logprobs, ["responses.additional_properties"] = outputText.AdditionalProperties } };
                    break;
                case RefusalContentPart refusal:
                    yield return new AITextContentPart { Type = "text", Text = refusal.Refusal, Metadata = new Dictionary<string, object?> { ["responses.type"] = "refusal", ["responses.additional_properties"] = refusal.AdditionalProperties } };
                    break;
                case InputImagePart image:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = GuessMediaType(image.ImageUrl) ?? "image/*",
                        Filename = image.FileId,
                        Data = image.ImageUrl,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["responses.type"] = "input_image",
                            ["responses.detail"] = image.Detail,
                            ["responses.file_id"] = image.FileId,
                            ["responses.prompt_cache_breakpoint"] = image.PromptCacheBreakpoint,
                            ["responses.additional_properties"] = image.AdditionalProperties
                        }
                    };
                    break;
                case InputFilePart file:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = GuessMediaType(file.FileData ?? file.FileUrl),
                        Filename = file.Filename,
                        Data = file.FileData ?? file.FileUrl ?? file.FileId,
                        Metadata = new Dictionary<string, object?>
                        {
                            ["responses.type"] = "input_file",
                            ["responses.file_id"] = file.FileId,
                            ["responses.file_url"] = file.FileUrl,
                            ["responses.detail"] = file.Detail,
                            ["responses.prompt_cache_breakpoint"] = file.PromptCacheBreakpoint,
                            ["responses.additional_properties"] = file.AdditionalProperties
                        }
                    };
                    break;
                case ResponseUnknownContentPart unknown:
                    yield return new AIFileContentPart
                    {
                        Type = "file",
                        MediaType = "application/json",
                        Data = JsonSerializer.SerializeToElement(unknown, Json),
                        Metadata = new Dictionary<string, object?>
                        {
                            ["responses.raw_part"] = JsonSerializer.SerializeToElement(unknown, Json),
                            ["responses.provider_id"] = providerId
                        }
                    };
                    break;
            }
        }
    }

    private static IEnumerable<ResponseContentPart> ToResponsesContentParts(IEnumerable<AIContentPart>? parts, string? role, string providerId)
    {
        foreach (var part in parts ?? [])
        {
            switch (part)
            {
                case AITextContentPart text:
                    {
                        if (ExtractValue<string>(text.Metadata, "responses.type") == "refusal")
                        {
                            yield return new RefusalContentPart
                            {
                                Refusal = text.Text,
                                AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(text.Metadata, "responses.additional_properties")
                            };
                        }
                        else if (role == "assistant")
                        {
                            yield return new OutputTextPart
                            {
                                Text = text.Text,
                                Annotations = ExtractObject<object[]>(text.Metadata, "responses.annotations") ?? [],
                                Logprobs = ExtractObject<JsonElement[]>(text.Metadata, "responses.logprobs"),
                                AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(text.Metadata, "responses.additional_properties")
                            };
                        }
                        else
                        {
                            yield return new InputTextPart(text.Text)
                            {
                                PromptCacheBreakpoint = ExtractObject<object>(text.Metadata, "responses.prompt_cache_breakpoint"),
                                AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(text.Metadata, "responses.additional_properties")
                            };
                        }

                        break;
                    }

                case AIFileContentPart file:
                    {
                        if (ExtractObject<JsonElement?>(file.Metadata, "responses.raw_part") is { ValueKind: JsonValueKind.Object } rawPart)
                        {
                            if (ExtractValue<string>(file.Metadata, "responses.provider_id") == providerId)
                                yield return rawPart.Deserialize<ResponseContentPart>(ResponseJson.Default)
                                    ?? throw new JsonException("Could not deserialize preserved Responses content part.");
                            break;
                        }

                        if (role == "user")
                        {
                            if (file.MediaType?.StartsWith("image/") == true)
                            {
                                yield return new InputImagePart
                                {
                                    ImageUrl = file.Data?.ToString(),
                                    FileId = ExtractValue<string>(file.Metadata, "responses.file_id"),
                                    Detail = ExtractValue<string>(file.Metadata, "responses.detail"),
                                    PromptCacheBreakpoint = ExtractObject<object>(file.Metadata, "responses.prompt_cache_breakpoint"),
                                    AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(file.Metadata, "responses.additional_properties")
                                };
                            }
                            else
                            {
                                var dataText = file.Data?.ToString();
                                yield return new InputFilePart
                                {
                                    Filename = file.Filename,
                                    FileId = ExtractValue<string>(file.Metadata, "responses.file_id"),
                                    FileData = dataText is not null && dataText.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? dataText : null,
                                    Detail = ExtractValue<string>(file.Metadata, "responses.detail"),
                                    PromptCacheBreakpoint = ExtractObject<object>(file.Metadata, "responses.prompt_cache_breakpoint"),
                                    FileUrl = dataText is not null && dataText.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? dataText : ExtractValue<string>(file.Metadata, "responses.file_url"),
                                    AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(file.Metadata, "responses.additional_properties")
                                };
                            }
                        }
                        break;
                    }
            }
        }
    }

    private static AIToolDefinition ToUnifiedTool(ResponseToolDefinition tool)
        => new()
        {
            Name = tool.Extra is not null && tool.Extra.TryGetValue("name", out var n) ? n.GetString() ?? tool.Type : tool.Type,
            Description = tool.Extra is not null && tool.Extra.TryGetValue("description", out var d) ? d.GetString() : null,
            InputSchema = tool.Extra is not null && tool.Extra.TryGetValue("parameters", out var p) ? p : null,
            DeferLoading = tool.Extra is not null && tool.Extra.TryGetValue("defer_loading", out var defer) && defer.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? defer.GetBoolean()
                : null,
            AllowedCallers = tool.Extra is not null && tool.Extra.TryGetValue("allowed_callers", out var callers) && callers.ValueKind == JsonValueKind.Array
                ? callers.EnumerateArray().Where(static value => value.ValueKind == JsonValueKind.String).Select(static value => value.GetString()!).ToArray()
                : null,
            Metadata = new Dictionary<string, object?>
            {
                ["responses.type"] = tool.Type,
                ["responses.extra"] = tool.Extra
            }
        };

    private static ResponseToolDefinition ToResponsesTool(AIToolDefinition tool)
    {
        var extra = ExtractObject<Dictionary<string, JsonElement>>(tool.Metadata, "responses.extra")
                    is { } preserved
            ? new Dictionary<string, JsonElement>(preserved, StringComparer.Ordinal)
            : [];

        var type = ExtractValue<string>(tool.Metadata, "responses.type") ?? "function";
        if (type is "function" or "namespace" || extra.ContainsKey("name"))
            extra["name"] = JsonSerializer.SerializeToElement(tool.Name, Json);

        if (!string.IsNullOrWhiteSpace(tool.Description))
            extra["description"] = JsonSerializer.SerializeToElement(tool.Description, Json);

        if (tool.InputSchema is not null)
            extra["parameters"] = JsonSerializer.SerializeToElement(tool.InputSchema, Json);

        if (tool.OutputSchema is not null)
            extra["output_schema"] = JsonSerializer.SerializeToElement(tool.OutputSchema, Json);

        if (tool.DeferLoading is not null)
            extra["defer_loading"] = JsonSerializer.SerializeToElement(tool.DeferLoading, Json);

        if (tool.AllowedCallers is not null)
            extra["allowed_callers"] = JsonSerializer.SerializeToElement(tool.AllowedCallers, Json);

        return new ResponseToolDefinition
        {
            Type = type,
            Extra = extra
        };
    }

    private static string? GuessMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var end = value.IndexOf(';');
            if (end > 5)
                return value[5..end];
        }

        return null;
    }
}
