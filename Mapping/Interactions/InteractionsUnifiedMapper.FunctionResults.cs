using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    private static InteractionFunctionResultStep CreateFunctionResultStep(
        object? output,
        string? callId,
        string? name,
        string? signature,
        bool? isError,
        bool preserveNativeResult = false,
        bool preserveNonMcpResult = false)
    {
        bool? mcpIsError = null;
        JsonElement? result;
        if (preserveNativeResult)
        {
            result = ToInteractionJson(output);
        }
        else if (TryConvertMcpFunctionResult(output, out var content, out mcpIsError))
        {
            result = ToInteractionJson(content);
        }
        else
        {
            // Keep each outbound path's historical handling of non-MCP payloads.
            result = preserveNonMcpResult
                ? ToInteractionJson(TryGetStructuredContentResult(output) ?? output)
                : ToInteractionJson(ToFunctionResultContentBlocks(output));
        }

        return new InteractionFunctionResultStep
        {
            CallId = callId,
            Name = name,
            Signature = signature,
            IsError = isError ?? mcpIsError,
            Result = result
        };
    }

    private static bool TryConvertMcpFunctionResult(
        object? output,
        out List<InteractionContent> content,
        out bool? isError)
    {
        content = [];
        isError = null;
        if (!TryReadMcpFunctionResult(output, out var result))
            return false;

        if ((TryGetJsonProperty(result, "isError", out var error)
             || TryGetJsonProperty(result, "is_error", out error))
            && error.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            isError = error.GetBoolean();
        }

        if (TryGetJsonProperty(result, "content", out var blocks)
            && blocks.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            if (blocks.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in blocks.EnumerateArray())
                    content.Add(ToInteractionMcpContent(block));
            }
            else
            {
                content.Add(McpContentFallback(blocks, "content must be an array"));
            }
        }

        if ((TryGetJsonProperty(result, "structuredContent", out var structured)
             || TryGetJsonProperty(result, "structured_content", out structured))
            && structured.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            // Structured output supplements the content array; it must not hide media.
            content.Add(new InteractionTextContent { Text = structured.GetRawText() });
        }

        if (content.Count == 0)
            content.Add(new InteractionTextContent { Text = "{}" });

        return true;
    }

    private static bool TryReadMcpFunctionResult(object? output, out JsonElement result)
    {
        result = default;
        if (output is null)
            return false;

        try
        {
            // MCP Data/Blob contain base64 UTF-8; FromBytes accepts raw bytes and
            // the SDK encodes those lazily. Use that wire representation for both
            // typed and saved results, without heuristic decoding or double encoding.
            result = output is JsonElement json
                ? json
                : JsonSerializer.SerializeToElement(output, Json);
            if (result.ValueKind == JsonValueKind.String)
            {
                using var document = JsonDocument.Parse(result.GetString()!);
                result = document.RootElement.Clone();
            }

            if (result.ValueKind != JsonValueKind.Object)
                return false;

            if (TryGetJsonProperty(result, "structuredContent", out _)
                || TryGetJsonProperty(result, "structured_content", out _))
            {
                return true;
            }

            // A business object can also have content or isError properties.
            // Require an MCP-style block discriminator or an envelope-level error
            // marker alongside content; untyped legacy content keeps its old path.
            return TryGetJsonProperty(result, "content", out var content)
                   && (content.ValueKind == JsonValueKind.Array
                       && (content.GetArrayLength() == 0
                           || content.EnumerateArray().Any(block => McpContentString(block, "type") is not null))
                       || TryGetJsonProperty(result, "isError", out _)
                       || TryGetJsonProperty(result, "is_error", out _));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            result = default;
            return false;
        }
    }

    private static InteractionContent ToInteractionMcpContent(JsonElement block)
    {
        var type = McpContentString(block, "type");
        switch (type)
        {
            case "text":
                return McpContentString(block, "text") is { } text
                    ? new InteractionTextContent { Text = text }
                    : McpContentFallback(block, "missing text");
            case "image":
            case "audio":
                return ToInteractionMcpMedia(block, type);
            case "resource_link":
                return ToInteractionMcpMedia(block);
            case "resource" when TryGetJsonProperty(block, "resource", out var resource)
                                 && resource.ValueKind == JsonValueKind.Object:
                if (McpContentString(resource, "text") is { } resourceText)
                {
                    // CSV is also a supported document, even when MCP embeds its
                    // UTF-8 contents as text rather than a binary resource.
                    return NormalizeMcpMimeType(resource) == "text/csv"
                        ? ToInteractionMcpMedia(resource, inlineData: Convert.ToBase64String(Encoding.UTF8.GetBytes(resourceText)))
                        : new InteractionTextContent { Text = resourceText };
                }

                return ToInteractionMcpMedia(resource);
            default:
                return McpContentFallback(block, "unsupported or malformed MCP content block");
        }
    }

    private static InteractionContent ToInteractionMcpMedia(
        JsonElement resource,
        string? expectedType = null,
        string? inlineData = null)
    {
        var mimeType = NormalizeMcpMimeType(resource);
        var type = McpMediaContentType(mimeType);
        if (type is null || expectedType is not null && type != expectedType)
            return McpContentFallback(resource, "unsupported or missing MIME type");

        var data = inlineData ?? McpContentString(resource, "data") ?? McpContentString(resource, "blob");
        if (data?.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
        {
            var marker = data.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            data = marker >= 0 ? data[(marker + 8)..] : null;
        }

        var uri = McpContentString(resource, "uri");
        if (!string.IsNullOrWhiteSpace(data) && Base64.IsValid(data))
        {
            // A resource URI often identifies a local/MCP resource, not a Google
            // file. Inline bytes take precedence; never send both data and uri.
            uri = null;
        }
        else
        {
            data = null;
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsedUri)
                || parsedUri.Scheme is not ("http" or "https"))
            {
                return McpContentFallback(resource, "missing valid inline data or a usable HTTP(S) URI");
            }
        }

        return type switch
        {
            "image" => new InteractionImageContent { MimeType = mimeType, Data = data, Uri = uri },
            "audio" => new InteractionAudioContent { MimeType = mimeType, Data = data, Uri = uri },
            "document" => new InteractionDocumentContent { MimeType = mimeType, Data = data, Uri = uri },
            _ => new InteractionVideoContent { MimeType = mimeType, Data = data, Uri = uri }
        };
    }

    private static string? McpMediaContentType(string? mimeType)
        => mimeType switch
        {
            "image/png" or "image/jpeg" or "image/webp" => "image",
            "application/pdf" or "text/csv"
                or "application/msword"
                or "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                or "application/vnd.openxmlformats-officedocument.wordprocessingml.template"
                or "application/vnd.oasis.opendocument.text"
                or "application/vnd.ms-powerpoint"
                or "application/vnd.openxmlformats-officedocument.presentationml.presentation"
                or "application/vnd.oasis.opendocument.presentation"
                or "application/vnd.ms-excel"
                or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                or "application/vnd.oasis.opendocument.spreadsheet" => "document",
            "audio/wav" or "audio/mp3" or "audio/mpeg" or "audio/aiff" or "audio/aac"
                or "audio/ogg" or "audio/flac" or "audio/m4a" or "audio/opus" or "audio/webm"
                or "audio/alaw" or "audio/mulaw" or "audio/l16" => "audio",
            "video/mp4" or "video/mpeg" or "video/mov" or "video/avi" or "video/x-flv"
                or "video/mpg" or "video/webm" or "video/wmv" or "video/3gpp" => "video",
            _ => null
        };

    private static string? NormalizeMcpMimeType(JsonElement resource)
        => (McpContentString(resource, "mimeType") ?? McpContentString(resource, "mime_type"))?
            .Split(';', 2)[0].Trim().ToLowerInvariant();

    private static string? McpContentString(JsonElement element, string name)
        => TryGetJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static InteractionTextContent McpContentFallback(JsonElement block, string reason)
    {
        // Only descriptive fields cross this fallback boundary. In particular,
        // don't stringify a block's data/blob or expose MCP's private _meta.
        var details = new List<string>();
        foreach (var key in new[] { "type", "name", "title", "description", "uri", "mimeType", "mime_type", "text" })
        {
            if (McpContentString(block, key) is { Length: > 0 } value)
            {
                if (key == "uri" && value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    value = "data: [inline payload omitted]";
                details.Add($"{key}: {value}");
            }
        }

        return new InteractionTextContent
        {
            Text = $"MCP content could not be converted ({reason})."
                   + (details.Count == 0 ? string.Empty : " " + string.Join("; ", details))
        };
    }
}
