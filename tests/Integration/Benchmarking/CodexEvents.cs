using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoslynKit.Benchmarking;

/// <summary>
/// Represents the supported fields in one Codex JSON Lines event.
/// </summary>
internal sealed record CodexEvent
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("thread_id")]
    public string? ThreadId { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("item")]
    public CodexEventItem? Item { get; init; }

    [JsonPropertyName("usage")]
    public CodexUsage? Usage { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement? Payload { get; init; }

    [JsonPropertyName("error")]
    public JsonElement? Error { get; init; }
}

/// <summary>
/// Represents the supported fields of an item carried by a Codex event.
/// </summary>
internal sealed record CodexEventItem
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }

    [JsonPropertyName("command")]
    public string? Command { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("exit_code")]
    public int? ExitCode { get; init; }

    [JsonPropertyName("aggregated_output")]
    public string? AggregatedOutput { get; init; }

    [JsonPropertyName("server")]
    public string? Server { get; init; }

    [JsonPropertyName("tool")]
    public string? Tool { get; init; }

    [JsonPropertyName("arguments")]
    public JsonElement? Arguments { get; init; }

    [JsonPropertyName("result")]
    public JsonElement? Result { get; init; }
}

/// <summary>
/// Represents token fields emitted by the terminal Codex turn event.
/// </summary>
internal sealed record CodexUsage
{
    [JsonPropertyName("input_tokens")]
    public long? InputTokens { get; init; }

    [JsonPropertyName("cached_input_tokens")]
    public long? CachedInputTokens { get; init; }

    [JsonPropertyName("cache_write_input_tokens")]
    public long? CacheWriteInputTokens { get; init; }

    [JsonPropertyName("cache_write_tokens")]
    public long? CacheWriteTokens { get; init; }

    [JsonPropertyName("output_tokens")]
    public long? OutputTokens { get; init; }

    [JsonPropertyName("reasoning_output_tokens")]
    public long? ReasoningOutputTokens { get; init; }
}

/// <summary>
/// Contains parsed terminal accounting and tool-use findings from a JSON Lines stream.
/// </summary>
internal sealed record CodexEventLog(TokenUsage? Usage, int ToolCallCount, string[] Issues);

/// <summary>
/// Parses strict Codex JSON Lines events and validates terminal token accounting.
/// </summary>
internal static class CodexEventParser
{
    private static readonly HashSet<string> NonToolItemTypes = new(StringComparer.Ordinal)
    {
        "agent_message",
        "error",
        "reasoning",
    };

    public static CodexEventLog Parse(string jsonLines)
    {
        var issues = new List<string>();
        var events = new List<CodexEvent>();
        var toolIds = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new StringReader(jsonLines);
        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            DetectToolCall(line, lineNumber, toolIds);
            try
            {
                var parsed = JsonSerializer.Deserialize<CodexEvent>(line, BenchmarkJson.Options);
                if (parsed is null || string.IsNullOrWhiteSpace(parsed.Type))
                {
                    issues.Add($"event log line {lineNumber} did not contain an event type");
                }
                else
                {
                    events.Add(parsed);
                }
            }
            catch (JsonException)
            {
                issues.Add($"event log line {lineNumber} was not a supported JSON object");
            }
        }

        var terminalEvents = events.Where(candidate => candidate.Type == "turn.completed").ToArray();
        if (terminalEvents.Length != 1)
        {
            issues.Add(terminalEvents.Length == 0
                ? "event log did not contain terminal token accounting"
                : $"event log contained {terminalEvents.Length} terminal usage events for one ephemeral Codex exec turn");
            return new CodexEventLog(null, toolIds.Count, [.. issues]);
        }

        var terminalUsage = terminalEvents[0].Usage;
        if (terminalUsage is null)
        {
            issues.Add("terminal turn event omitted usage");
            return new CodexEventLog(null, toolIds.Count, [.. issues]);
        }

        var usage = ValidateUsage(terminalUsage, issues);
        return new CodexEventLog(usage, toolIds.Count, [.. issues]);
    }

    private static TokenUsage? ValidateUsage(CodexUsage usage, ICollection<string> issues)
    {
        ValidateToken(usage.InputTokens, "input_tokens", required: true, issues);
        ValidateToken(usage.CachedInputTokens, "cached_input_tokens", required: true, issues);
        ValidateToken(usage.CacheWriteInputTokens, "cache_write_input_tokens", required: false, issues);
        ValidateToken(usage.CacheWriteTokens, "cache_write_tokens", required: false, issues);
        ValidateToken(usage.OutputTokens, "output_tokens", required: true, issues);
        ValidateToken(usage.ReasoningOutputTokens, "reasoning_output_tokens", required: true, issues);
        if (usage.InputTokens is null
            || usage.CachedInputTokens is null
            || usage.OutputTokens is null
            || usage.ReasoningOutputTokens is null)
        {
            return null;
        }

        var cacheWrite = usage.CacheWriteInputTokens ?? usage.CacheWriteTokens;
        if (usage.CacheWriteInputTokens is not null
            && usage.CacheWriteTokens is not null
            && usage.CacheWriteInputTokens != usage.CacheWriteTokens)
        {
            issues.Add("usage cache-write token aliases disagreed");
        }

        if (usage.CachedInputTokens > usage.InputTokens)
        {
            issues.Add("cached_input_tokens exceeded input_tokens");
            return null;
        }

        var uncached = usage.InputTokens.Value - usage.CachedInputTokens.Value;
        long? regularUncached = null;
        if (cacheWrite is not null)
        {
            if (cacheWrite > uncached)
            {
                issues.Add("cache_write_input_tokens exceeded non-cached input tokens");
            }
            else
            {
                regularUncached = uncached - cacheWrite;
            }
        }

        return new TokenUsage
        {
            InputTokens = usage.InputTokens.Value,
            CachedInputTokens = usage.CachedInputTokens.Value,
            CacheWriteInputTokens = cacheWrite,
            UncachedInputTokens = uncached,
            RegularUncachedInputTokens = regularUncached,
            OutputTokens = usage.OutputTokens.Value,
            ReasoningOutputTokens = usage.ReasoningOutputTokens.Value,
        };
    }

    private static void ValidateToken(long? value, string name, bool required, ICollection<string> issues)
    {
        if (value is null)
        {
            if (required)
            {
                issues.Add($"usage omitted {name}");
            }

            return;
        }

        if (value < 0)
        {
            issues.Add($"usage field {name} was not a nonnegative integer");
        }
    }

    private static void DetectToolCall(string line, int lineNumber, ISet<string> toolIds)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var eventTypeElement))
            {
                return;
            }

            var eventType = eventTypeElement.GetString();
            if (eventType is "item.started" or "item.completed"
                && root.TryGetProperty("item", out var item)
                && item.ValueKind == JsonValueKind.Object
                && item.TryGetProperty("type", out var itemTypeElement))
            {
                var itemType = itemTypeElement.GetString();
                if (!string.IsNullOrWhiteSpace(itemType) && !NonToolItemTypes.Contains(itemType))
                {
                    var id = item.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                    toolIds.Add(string.IsNullOrWhiteSpace(id) ? $"line-{lineNumber}" : id);
                }
            }

            if (eventType == "response_item"
                && root.TryGetProperty("payload", out var payload)
                && payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("type", out var payloadType)
                && payloadType.GetString() == "function_call")
            {
                var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
                toolIds.Add(string.IsNullOrWhiteSpace(id) ? $"line-{lineNumber}" : id);
            }
        }
        catch (JsonException)
        {
            // The strict parser reports malformed JSON with the precise line number.
        }
    }
}
