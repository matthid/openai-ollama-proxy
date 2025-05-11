namespace http_proxy;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Top-level object for a streamed chat completion chunk.
/// </summary>
public class ChatCompletionChunkDto
{
    /// <summary>
    /// A unique identifier for this chat completion (same on all chunks).
    /// </summary>
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    /// <summary>
    /// The object type, always "chat.completion.chunk".
    /// </summary>
    [JsonPropertyName("object")]
    public string? Object { get; set; }

    /// <summary>
    /// Unix timestamp (in seconds) when this chunk was created.
    /// </summary>
    [JsonPropertyName("created")]
    public long Created { get; set; }

    /// <summary>
    /// Model name used to generate the completion.
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// Service tier used to process the request (default, flex, etc.).
    /// </summary>
    [JsonPropertyName("service_tier"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ServiceTier { get; set; }

    /// <summary>
    /// Fingerprint of the backend configuration that served this chunk.
    /// </summary>
    [JsonPropertyName("system_fingerprint"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SystemFingerprint { get; set; }

    /// <summary>
    /// The list of choices in this chunk. Generally length=1 unless you requested n>1.
    /// </summary>
    [JsonPropertyName("choices")]
    public List<ChoiceDto?>? Choices { get; set; }

    /// <summary>
    /// Usage stats for the entire request, included on the final chunk if requested.
    /// </summary>
    [JsonPropertyName("usage"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public UsageDto? Usage { get; set; }
}

/// <summary>
/// One choice within the streamed response.
/// </summary>
public class ChoiceDto
{
    /// <summary>
    /// The zero-based index of this choice.
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Why the model stopped generating tokens for this chunk.
    /// </summary>
    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; set; }

    /// <summary>
    /// The delta of newly-streamed content / tool calls.
    /// </summary>
    [JsonPropertyName("delta")]
    public DeltaDto? Delta { get; set; }

    /// <summary>
    /// Log probability info (if requested). Can be null or omitted in streaming.
    /// We capture it as a raw JsonElement for flexibility.
    /// </summary>
    [JsonPropertyName("logprobs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Logprobs { get; set; }
}

/// <summary>
/// The incremental update payload.
/// </summary>
public class DeltaDto
{
    /// <summary>
    /// Role ("assistant") when first sent; afterwards typically omitted.
    /// </summary>
    [JsonPropertyName("role"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Role { get; set; }

    /// <summary>
    /// Content text for this chunk (or null if none).
    /// </summary>
    [JsonPropertyName("content"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; set; }

    /// <summary>
    /// Deprecated single-function call object.
    /// </summary>
    [JsonPropertyName("function_call"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FunctionCallDto? FunctionCall { get; set; }

    /// <summary>
    /// Array of tool calls (replacement for deprecated function_call).
    /// </summary>
    [JsonPropertyName("tool_calls"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ToolCallDto>? ToolCalls { get; set; }

    /// <summary>
    /// Refusal message (if the model refused to comply).
    /// </summary>
    [JsonPropertyName("refusal"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Refusal { get; set; }
}

/// <summary>
/// Deprecated: single function call shape.
/// </summary>
public class FunctionCallDto
{
    /// <summary>
    /// Name of the function to call.
    /// </summary>
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>
    /// JSON-string arguments for the call.
    /// </summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

/// <summary>
/// Newer style of tool call that the model may emit.
/// </summary>
public class ToolCallDto
{
    /// <summary>
    /// The index within the choices array (always matches parent ChoiceDto.Index).
    /// </summary>
    [JsonPropertyName("index")]
    public int Index { get; set; }

    /// <summary>
    /// Unique ID for this tool call.
    /// </summary>
    [JsonPropertyName("id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; }

    /// <summary>
    /// Must be "function" for now.
    /// </summary>
    [JsonPropertyName("type"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Type { get; set; }

    /// <summary>
    /// The actual function being invoked.
    /// </summary>
    [JsonPropertyName("function")]
    public FunctionDto? Function { get; set; }
}

/// <summary>
/// Details of a function invoked as a tool call.
/// </summary>
public class FunctionDto
{
    /// <summary>
    /// Name of the function.
    /// </summary>
    [JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; set; }

    /// <summary>
    /// JSON-string arguments to pass (often needs re-parsing & validation).
    /// </summary>
    [JsonPropertyName("arguments")]
    public string? Arguments { get; set; }
}

/// <summary>
/// Usage breakdown, typically only present on the last chunk if you requested include_usage.
/// </summary>
public class UsageDto
{
    [JsonPropertyName("prompt_tokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }

    [JsonPropertyName("completion_tokens_details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CompletionTokensDetailsDto? CompletionTokensDetails { get; set; }

    [JsonPropertyName("prompt_tokens_details"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PromptTokensDetailsDto? PromptTokensDetails { get; set; }
}

/// <summary>
/// Breakdown of the tokens used in the completion.
/// </summary>
public class CompletionTokensDetailsDto
{
    [JsonPropertyName("accepted_prediction_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AcceptedPredictionTokens { get; set; }

    [JsonPropertyName("rejected_prediction_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RejectedPredictionTokens { get; set; }

    [JsonPropertyName("reasoning_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ReasoningTokens { get; set; }

    [JsonPropertyName("audio_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? AudioTokens { get; set; }
}

/// <summary>
/// Breakdown of the tokens used in the prompt.
/// </summary>
public class PromptTokensDetailsDto
{
    [JsonPropertyName("audio_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? AudioTokens { get; set; }

    [JsonPropertyName("cached_tokens"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CachedTokens { get; set; }

}