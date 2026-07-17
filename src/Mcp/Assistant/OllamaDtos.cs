using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexus.Service.Mcp.Assistant;

// Ollama's own HTTP API DTOs. Kept out of AppJsonContext (Nexus's camelCase wire
// contract) because Ollama's fields are snake_case; a dedicated context with the
// matching naming policy avoids per-property JsonPropertyName attributes.

public sealed class OllamaVersionResponse
{
    public string Version { get; set; } = "";
}

public sealed class OllamaTagsResponse
{
    public List<OllamaModelInfo> Models { get; set; } = new();
}

public sealed class OllamaModelInfo
{
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public long Size { get; set; }
    public string Digest { get; set; } = "";
    public OllamaModelDetails? Details { get; set; }
    /// <summary>Set only by GET /api/ps (loaded models); null from /api/tags.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>VRAM-resident bytes; set only by GET /api/ps.</summary>
    public long SizeVram { get; set; }
}

public sealed class OllamaModelDetails
{
    public string Format { get; set; } = "";
    public string Family { get; set; } = "";
    public string ParameterSize { get; set; } = "";
    public string QuantizationLevel { get; set; } = "";
}

public sealed class OllamaPullRequest
{
    public string Model { get; set; } = "";
    public bool Stream { get; set; } = true;
}

/// <summary>One NDJSON line from POST /api/pull.</summary>
public sealed class OllamaPullStatus
{
    public string Status { get; set; } = "";
    public string? Digest { get; set; }
    public long? Total { get; set; }
    public long? Completed { get; set; }
    /// <summary>Present instead of Status when the pull fails mid-stream.</summary>
    public string? Error { get; set; }
}

public sealed class OllamaDeleteRequest
{
    public string Model { get; set; } = "";
}

public sealed class OllamaChatRequest
{
    public string Model { get; set; } = "";
    public List<OllamaChatMessage> Messages { get; set; } = new();
    public List<OllamaToolDef>? Tools { get; set; }
    public bool Stream { get; set; }
}

public sealed class OllamaChatMessage
{
    public string Role { get; set; } = "";
    public string? Content { get; set; }
    public List<OllamaToolCall>? ToolCalls { get; set; }
    /// <summary>Set on a role:"tool" message: the name of the tool this result came from.</summary>
    public string? ToolName { get; set; }
}

public sealed class OllamaToolCall
{
    public OllamaFunctionCall Function { get; set; } = new();
}

public sealed class OllamaFunctionCall
{
    public string Name { get; set; } = "";
    public JsonElement? Arguments { get; set; }
}

/// <summary>OpenAI-format tool definition Ollama's /api/chat expects.</summary>
public sealed class OllamaToolDef
{
    public string Type { get; set; } = "function";
    public OllamaFunctionDef Function { get; set; } = new();
}

public sealed class OllamaFunctionDef
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>Raw JSON Schema object, written verbatim.</summary>
    public JsonElement Parameters { get; set; }
}

public sealed class OllamaChatResponse
{
    public string Model { get; set; } = "";
    public OllamaChatMessage Message { get; set; } = new();
    public bool Done { get; set; }
}

[JsonSerializable(typeof(OllamaVersionResponse))]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaModelInfo))]
[JsonSerializable(typeof(List<OllamaModelInfo>))]
[JsonSerializable(typeof(OllamaModelDetails))]
[JsonSerializable(typeof(OllamaPullRequest))]
[JsonSerializable(typeof(OllamaPullStatus))]
[JsonSerializable(typeof(OllamaDeleteRequest))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaChatMessage))]
[JsonSerializable(typeof(List<OllamaChatMessage>))]
[JsonSerializable(typeof(OllamaToolCall))]
[JsonSerializable(typeof(List<OllamaToolCall>))]
[JsonSerializable(typeof(OllamaFunctionCall))]
[JsonSerializable(typeof(OllamaToolDef))]
[JsonSerializable(typeof(List<OllamaToolDef>))]
[JsonSerializable(typeof(OllamaFunctionDef))]
[JsonSerializable(typeof(OllamaChatResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
internal partial class OllamaJsonContext : JsonSerializerContext;
