using System.Globalization;
using System.Text.Json;

namespace CodexStatusbar;

/// <summary>
/// Shared numeric rules for the status strip. Everything here is pure so the headless self-test can
/// exercise it without a window station.
/// </summary>
internal static class MetricFormat
{
    /// <summary>
    /// Collapsed-strip token format: value &lt; 1000 =&gt; "843"; 1000..999999 =&gt; "12.4K";
    /// &gt;= 1_000_000 =&gt; "5.7M".
    /// </summary>
    public static string CompactTokens(long value)
    {
        var magnitude = Math.Abs(value);
        if (magnitude >= 1_000_000)
        {
            return (value / 1_000_000d).ToString("0.0", CultureInfo.InvariantCulture) + "M";
        }

        if (magnitude >= 1_000)
        {
            return (value / 1_000d).ToString("0.0", CultureInfo.InvariantCulture) + "K";
        }

        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Expanded-panel token format: thousands separators, e.g. "5,699,084".</summary>
    public static string FullTokens(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>
    /// Cache-hit percentage. Returns null when the denominator is zero so callers render "--"
    /// instead of NaN.
    /// </summary>
    public static double? CacheHitPercent(long inputTokens, long cachedInputTokens) =>
        inputTokens <= 0 ? null : cachedInputTokens * 100d / inputTokens;

    public static string FormatCachePercent(long inputTokens, long cachedInputTokens)
    {
        var percent = CacheHitPercent(inputTokens, cachedInputTokens);
        return percent is null
            ? "--"
            : percent.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%";
    }

    public static string FormatCachePercentCompact(long inputTokens, long cachedInputTokens)
    {
        var percent = CacheHitPercent(inputTokens, cachedInputTokens);
        return percent is null
            ? "--"
            : percent.Value.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>TPS body with separate markers for a held sample and partial response coverage.</summary>
    public static string FormatTps(double? tps, bool isHeld, bool isEstimated = false)
    {
        if (!IsUsableTps(tps))
        {
            return "--";
        }

        var rounded = Math.Round(tps!.Value, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);
        if (isEstimated)
        {
            return "≈" + rounded;
        }

        return isHeld ? "~" + rounded : rounded;
    }

    public static string FormatTpsFloat(double? tps) =>
        !IsUsableTps(tps) ? "--" : tps!.Value.ToString("0.0", CultureInfo.InvariantCulture);

    public static string FormatTpsInt(long tokens, long milliseconds) =>
        milliseconds <= 0
            ? "--"
            : MetricFormat.FormatTps(1000d * tokens / milliseconds, isHeld: false);

    public static string FormatSeconds(double? milliseconds) =>
        milliseconds is null or < 0
            ? "--"
            : (milliseconds.Value / 1000d).ToString("0.00", CultureInfo.InvariantCulture) + " s";

    /// <summary>A TPS is only reported when it is finite and strictly positive — never 0, never NaN.</summary>
    public static bool IsUsableTps(double? tps) =>
        tps.HasValue && double.IsFinite(tps.Value) && tps.Value > 0;

    public static string ShortThreadId(string threadId, int maximumLength = 13)
    {
        var single = threadId
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (single.Length <= maximumLength)
        {
            return single;
        }

        if (maximumLength <= 1)
        {
            return maximumLength == 1 ? "…" : string.Empty;
        }

        var prefixLength = Math.Min(8, maximumLength - 1);
        var suffixLength = maximumLength - prefixLength - 1;
        return suffixLength <= 0
            ? single[..maximumLength]
            : string.Concat(
                single.AsSpan(0, prefixLength),
                "…",
                single.AsSpan(single.Length - suffixLength, suffixLength));
    }
}

/// <summary>Rules shared by the rollout parser and the response-level TPS tracker.</summary>
internal static class TpsRules
{
    public const double SmoothingAlpha = 0.35;

    /// <summary>Restart recovery only ever reads this many bytes from the end of a rollout file.</summary>
    public const int TailReadBytes = 4 * 1024 * 1024;

    /// <summary>Refuse intervals that imply implausibly high generation rates.</summary>
    public const double ImplausibleTps = 100_000;

    public static bool IsModelOutputItem(string itemType) =>
        itemType.Equals("Reasoning", StringComparison.OrdinalIgnoreCase)
        || itemType.Equals("AgentMessage", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Pairs each response's official output count with the span from its first observable output to
/// its usage record. A response without both sides contributes to coverage only.
/// </summary>
internal sealed class TpsTracker
{
    private readonly HashSet<string> _responseIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> _pendingToolCallIds = new(StringComparer.Ordinal);
    private readonly List<(long Start, long End)> _responseIntervals = [];

    private long? _pendingStartMs;
    private long? _pendingOutputEndMs;
    private long? _firstToolCallEndMs;
    private long? _toolResultMs;
    private long? _responseBoundaryMs;
    private long _latestCumulativeOutput;
    private (long Output, long? TimestampMs)? _legacyCompletion;
    private bool _missingOutputTiming;
    private bool _activityAfterMeasurement;

    public string? TurnId { get; private set; }
    public bool Streaming { get; private set; }

    // A tail that starts mid-turn cannot identify the first response boundary reliably.
    public bool TurnItemsComplete { get; private set; }
    private bool CanTrackResponses => Streaming && TurnItemsComplete;

    public long TurnGenerationMs { get; private set; }
    public long TurnOutputTokens { get; private set; }
    public int ObservedResponseCount { get; private set; }
    public int TimedResponseCount { get; private set; }
    public int DuplicateResponseSamplesIgnored { get; private set; }
    public IReadOnlyList<(long Start, long End)> ResponseIntervals => _responseIntervals;
    public long? FirstResponseStartMs { get; private set; }
    public long? LastResponseEndMs { get; private set; }

    public double? RawLastSample { get; private set; }
    public double? SmoothedTps { get; private set; }
    public double? LastTurnTps { get; private set; }
    public long? TtftMs { get; private set; }
    public double? DisplayTps => Streaming ? SmoothedTps : LastTurnTps;
    public bool IsHeld => Streaming && SmoothedTps is not null && _activityAfterMeasurement;
    public bool IsEstimated => TimedResponseCount > 0 && TimedResponseCount < ObservedResponseCount;

    public string StateDescription => Streaming
        ? SmoothedTps is null ? "waiting" : "generating"
        : LastTurnTps is null ? "idle" : "completed";

    public void BeginTurn(string? turnId, bool itemsComplete, long cumulativeOutput)
    {
        if (string.Equals(TurnId, turnId, StringComparison.Ordinal) && TurnItemsComplete == itemsComplete)
        {
            return;
        }

        var lastTurnTps = LastTurnTps;
        ResetTurn();
        LastTurnTps = lastTurnTps;
        TurnId = turnId;
        TurnItemsComplete = itemsComplete;
        _latestCumulativeOutput = cumulativeOutput;
        Streaming = true;
    }

    public void NoteActivityAfterMeasurement() => _activityAfterMeasurement = true;

    public void ObserveModelOutputItem(long? startedAtMs, long? completedAtMs)
    {
        if (!CanTrackResponses || completedAtMs <= _responseBoundaryMs)
        {
            return;
        }

        if (_legacyCompletion is { } legacy && (legacy.TimestampMs is null || startedAtMs >= legacy.TimestampMs))
        {
            FinishUnmatchedLegacyResponse();
        }

        if (_toolResultMs is long result && startedAtMs >= result)
        {
            // The earlier response never supplied usage. The next output starts after its tool result.
            var outputCrossedResult = _pendingOutputEndMs > result;
            SkipResponse(result);
            _missingOutputTiming = outputCrossedResult;
        }

        NoteActivityAfterMeasurement();
        if (startedAtMs is not long start || completedAtMs is not long end
            || start <= 0 || end < start || start < _responseBoundaryMs)
        {
            _missingOutputTiming = true;
            return;
        }

        _pendingStartMs = Math.Min(_pendingStartMs ?? start, start);
        _pendingOutputEndMs = Math.Max(_pendingOutputEndMs ?? end, end);
    }

    /// <summary>
    /// A raw tool call marks when its arguments were emitted. If it precedes every timed model
    /// item in the response, the first output time is unknown.
    /// </summary>
    public void ObserveToolCall(string? callId, long? completedAtMs)
    {
        if (!CanTrackResponses || completedAtMs <= _responseBoundaryMs)
        {
            return;
        }

        NoteActivityAfterMeasurement();
        if (completedAtMs is not long end)
        {
            _missingOutputTiming = true;
            return;
        }

        if (callId is not null)
        {
            _pendingToolCallIds.Add(callId);
        }

        // A completion timestamp alone cannot tell whether a call after a tool result began
        // before or after that result. Wait for a usable start rather than bridge that gap.
        _missingOutputTiming |= end > _toolResultMs;
        _firstToolCallEndMs = Math.Min(_firstToolCallEndMs ?? end, end);
        _pendingOutputEndMs = Math.Max(_pendingOutputEndMs ?? end, end);
    }

    public void ObserveToolResult(string? callId, long? timestampMs)
    {
        if (CanTrackResponses && _firstToolCallEndMs is long firstCall && timestampMs is long end
            && end >= firstCall && (callId is null || _pendingToolCallIds.Contains(callId)))
        {
            // Some results are written before the same response's usage. Split only when a new
            // model item also starts after this result.
            _toolResultMs = Math.Min(_toolResultMs ?? end, end);
        }
    }

    public void ObserveLegacyUsage(long cumulativeOutput, long lastOutput, long? timestampMs)
    {
        if (!CanTrackResponses || lastOutput <= 0 || cumulativeOutput <= _latestCumulativeOutput)
        {
            return;
        }

        FinishUnmatchedLegacyResponse();
        _latestCumulativeOutput = cumulativeOutput;
        _legacyCompletion = (cumulativeOutput, timestampMs);
        NoteActivityAfterMeasurement();
    }

    public void ObserveResponse(string? responseId, long outputTokens, long? endedAtMs, long cumulativeOutput)
    {
        if (!CanTrackResponses)
        {
            return;
        }

        var hasId = !string.IsNullOrWhiteSpace(responseId);
        if (hasId && !_responseIds.Add(responseId!))
        {
            DuplicateResponseSamplesIgnored++;
            return;
        }

        ObservedResponseCount++;
        NoteActivityAfterMeasurement();
        if (endedAtMs < _responseBoundaryMs || endedAtMs < _pendingStartMs)
        {
            // A late record for an older response cannot close the one currently in flight.
            return;
        }

        if (_legacyCompletion is { } legacy && legacy.Output != cumulativeOutput)
        {
            FinishUnmatchedLegacyResponse();
        }

        // An early token_count with the same cumulative output is the other carrier for this
        // response. A late copy cannot advance this high-water mark, even after a log replay.
        _legacyCompletion = null;
        _latestCumulativeOutput = Math.Max(_latestCumulativeOutput, cumulativeOutput);
        if (hasId && outputTokens > 0 && _pendingStartMs is long start && endedAtMs is long end
            && end > start && !_missingOutputTiming && (_firstToolCallEndMs ?? start) >= start
            && (_pendingOutputEndMs ?? end) <= end)
        {
            var duration = end - start;
            if (1000d * outputTokens / duration <= TpsRules.ImplausibleTps
                && outputTokens <= long.MaxValue - TurnOutputTokens)
            {
                AddResponseSample(start, end, outputTokens, duration);
            }
        }

        ClearPendingResponse(endedAtMs ?? _pendingOutputEndMs);
    }

    private void AddResponseSample(long start, long end, long outputTokens, long duration)
    {
        TurnOutputTokens += outputTokens;
        TurnGenerationMs += duration;
        TimedResponseCount++;
        FirstResponseStartMs ??= start;
        LastResponseEndMs = end;
        if (_responseIntervals.Count < 12)
        {
            _responseIntervals.Add((start, end));
        }

        var sample = 1000d * TurnOutputTokens / TurnGenerationMs;
        RawLastSample = sample;
        SmoothedTps = SmoothedTps is double previous
            ? previous + TpsRules.SmoothingAlpha * (sample - previous)
            : sample;
        _activityAfterMeasurement = false;
    }

    private void FinishUnmatchedLegacyResponse()
    {
        if (_legacyCompletion is { } legacy)
        {
            SkipResponse(legacy.TimestampMs ?? _pendingOutputEndMs);
            _legacyCompletion = null;
        }
    }

    private void SkipResponse(long? boundaryMs)
    {
        ObservedResponseCount++;
        NoteActivityAfterMeasurement();
        ClearPendingResponse(boundaryMs);
    }

    private void ClearPendingResponse(long? boundaryMs)
    {
        if (boundaryMs is long boundary)
        {
            _responseBoundaryMs = Math.Max(_responseBoundaryMs ?? boundary, boundary);
        }

        _pendingStartMs = null;
        _pendingOutputEndMs = null;
        _firstToolCallEndMs = null;
        _toolResultMs = null;
        _pendingToolCallIds.Clear();
        _missingOutputTiming = false;
    }

    public void CompleteTurn(long? ttftMs)
    {
        FinishUnmatchedLegacyResponse();
        if (CanTrackResponses && (_pendingOutputEndMs is not null || _missingOutputTiming))
        {
            // Output was observed without a corresponding completion record.
            ObservedResponseCount++;
        }

        ClearPendingResponse(null);
        Streaming = false;
        LastTurnTps = RawLastSample;
        TtftMs = ttftMs;
    }

    public void ResetTurn()
    {
        TurnId = null;
        TurnItemsComplete = false;
        Streaming = false;
        _responseIds.Clear();
        _responseIntervals.Clear();
        _responseBoundaryMs = null;
        _latestCumulativeOutput = 0;
        _legacyCompletion = null;
        ClearPendingResponse(null);
        _activityAfterMeasurement = false;
        TurnGenerationMs = 0;
        TurnOutputTokens = 0;
        ObservedResponseCount = 0;
        TimedResponseCount = 0;
        FirstResponseStartMs = null;
        LastResponseEndMs = null;
        RawLastSample = null;
        SmoothedTps = null;
        LastTurnTps = null;
        TtftMs = null;
    }
}

/// <summary>
/// Everything recovered from one rollout file. Cumulative usage comes from whichever carrier is
/// newest in file order: <c>token_usage_record.payload.thread_token_usage</c> (Codex 0.159+) or
/// <c>event_msg/token_count.payload.info.total_token_usage</c> (the only carrier on 0.151).
/// </summary>
internal sealed class RolloutStreamState
{
    private static readonly HashSet<string> KnownRecordTypes = new(StringComparer.Ordinal)
    {
        "session_meta", "turn_context", "response_item", "event_msg",
        "token_usage_record", "world_state", "compacted"
    };

    private static readonly HashSet<string> KnownEventTypes = new(StringComparer.Ordinal)
    {
        "task_started", "task_complete", "token_count", "item_completed",
        "thread_settings_applied", "turn_aborted", "agent_message"
    };

    private static readonly HashSet<string> KnownItemTypes = new(StringComparer.Ordinal)
    {
        "UserMessage", "Reasoning", "AgentMessage", "CommandExecution",
        "FileChange", "McpToolCall", "WebSearch", "ImageView"
    };

    public TpsTracker Tps { get; } = new();

    public string? SessionMetaId { get; set; }

    public string? SessionMetaOriginator { get; set; }

    public long InputTokens { get; set; }

    public long CachedInputTokens { get; set; }

    public long OutputTokens { get; set; }

    public long ReasoningOutputTokens { get; set; }

    public long TotalTokens { get; set; }

    public long ContextUsedTokens { get; set; }

    public long ContextWindowTokens { get; set; }

    public bool HasCumulativeUsage { get; set; }

    /// <summary>True when at least one cumulative reading came from the rollout itself.</summary>
    public bool HasRolloutUsage { get; set; }

    public string? CurrentTurnId { get; set; }

    public bool Streaming => Tps.Streaming;

    /// <summary>Distinct unknown record/event/item types, for the debug log's Warnings line.</summary>
    public SortedSet<string> Warnings { get; } = new(StringComparer.Ordinal);

    public long LineCount { get; set; }

    public bool SessionMetaSeen { get; set; }

    public long UncachedInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    /// <summary>
    /// Which official carrier supplied the cumulative usage:
    /// <c>token_usage_record</c> (Codex 0.159+), <c>token_count</c> (the only carrier on 0.151),
    /// both, or <c>none</c>.
    /// </summary>
    public string Carrier { get; private set; } = "none";

    /// <summary>Identical cumulative readings seen again and deliberately not re-applied.</summary>
    public int DuplicateUsageReadingsIgnored { get; private set; }

    public void ObserveLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        LineCount++;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            Warnings.Add("malformed-json");
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeElement)
                || typeElement.ValueKind != JsonValueKind.String)
            {
                Warnings.Add("missing-record-type");
                return;
            }

            var recordType = typeElement.GetString() ?? string.Empty;
            switch (recordType)
            {
                case "session_meta":
                    ObserveSessionMeta(root);
                    break;
                case "token_usage_record":
                    ObserveTokenUsageRecord(root);
                    break;
                case "event_msg":
                    ObserveEventMessage(root);
                    break;
                case "response_item":
                    ObserveResponseItem(root);
                    break;
                case "turn_context":
                case "world_state":
                case "compacted":
                    break;
                default:
                    Warnings.Add("record:" + recordType);
                    break;
            }
        }
    }

    private void ObserveSessionMeta(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        SessionMetaSeen = true;
        SessionMetaId = GetString(payload, "id") ?? SessionMetaId;
        SessionMetaOriginator = GetString(payload, "originator") ?? SessionMetaOriginator;
    }

    private void ObserveEventMessage(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload)
            || payload.ValueKind != JsonValueKind.Object)
        {
            Warnings.Add("event_msg:missing-payload");
            return;
        }

        var eventType = GetString(payload, "type");
        if (eventType is null)
        {
            Warnings.Add("event_msg:missing-payload-type");
            return;
        }

        if (!KnownEventTypes.Contains(eventType))
        {
            Warnings.Add("event:" + eventType);
            return;
        }

        switch (eventType)
        {
            case "task_started":
                ObserveTaskStarted(payload);
                break;
            case "task_complete":
                ObserveTaskComplete(payload);
                break;
            case "token_count":
                ObserveTokenCount(root, payload);
                break;
            case "item_completed":
                ObserveItemCompleted(payload);
                break;
            case "turn_aborted":
                if (GetString(payload, "turn_id") is not string abortedTurn || IsCurrentTurn(abortedTurn))
                {
                    Tps.CompleteTurn(null);
                }
                break;
            case "thread_settings_applied":
            case "agent_message":
                break;
        }
    }

    private void ObserveTaskStarted(JsonElement payload)
    {
        var turnId = GetString(payload, "turn_id");
        CurrentTurnId = turnId;
        Tps.BeginTurn(turnId, itemsComplete: !string.IsNullOrWhiteSpace(turnId), OutputTokens);
    }

    private void ObserveTaskComplete(JsonElement payload)
    {
        if (GetString(payload, "turn_id") is not string turnId || IsCurrentTurn(turnId))
        {
            Tps.CompleteTurn(TryGetLong(payload, "time_to_first_token_ms"));
        }
    }

    private void ObserveTokenCount(JsonElement root, JsonElement payload)
    {
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        long? cumulativeOutput = null;
        if (info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
        {
            // Cumulative for the whole thread. Redundant with token_usage_record on 0.159 and the
            // only carrier on older Codex, so "last one wins in file order" is right for both.
            ApplyCumulativeUsage(total, "token_count");
            cumulativeOutput = TryGetLong(total, "output_tokens");
        }

        if (TryGetLong(info, "model_context_window") is long window && window > 0)
        {
            ContextWindowTokens = window;
        }

        if (info.TryGetProperty("last_token_usage", out var last) && last.ValueKind == JsonValueKind.Object)
        {
            ContextUsedTokens = ReadRequestSize(last);
            if (cumulativeOutput is > 0 && GetLong(last, "output_tokens") is > 0 and var lastOutput)
            {
                Tps.ObserveLegacyUsage(cumulativeOutput.Value, lastOutput, ReadTimestampMs(root));
            }
        }
    }

    private void ObserveTokenUsageRecord(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var cumulativeOutput = 0L;
        if (payload.TryGetProperty("thread_token_usage", out var threadUsage)
            && threadUsage.ValueKind == JsonValueKind.Object)
        {
            ApplyCumulativeUsage(threadUsage, "token_usage_record");
            cumulativeOutput = GetLong(threadUsage, "output_tokens");
        }

        var outputTokens = 0L;
        if (payload.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            // "Context used" is the size of the current request, never the conversation total.
            ContextUsedTokens = ReadRequestSize(usage);
            outputTokens = GetLong(usage, "output_tokens");
        }

        if (IsCurrentTurn(GetString(payload, "turn_id")))
        {
            Tps.ObserveResponse(GetString(payload, "response_id"), outputTokens, ReadTimestampMs(root), cumulativeOutput);
        }
    }

    private bool IsCurrentTurn(string? turnId)
    {
        if (string.IsNullOrWhiteSpace(turnId))
        {
            return false;
        }

        if (!Tps.TurnItemsComplete && !string.Equals(Tps.TurnId, turnId, StringComparison.Ordinal))
        {
            // A tail may contain events from a turn whose task_started record is outside the window.
            CurrentTurnId = turnId;
            Tps.BeginTurn(turnId, itemsComplete: false, OutputTokens);
        }

        return string.Equals(Tps.TurnId, turnId, StringComparison.Ordinal);
    }

    private void ObserveResponseItem(JsonElement root)
    {
        if (Tps.Streaming && root.TryGetProperty("payload", out var payload)
            && payload.ValueKind == JsonValueKind.Object
            && GetString(payload, "type") is string type)
        {
            var callId = GetString(payload, "call_id");
            if (type.EndsWith("_call", StringComparison.Ordinal))
            {
                Tps.ObserveToolCall(callId, ReadTimestampMs(root));
            }
            else if (type.EndsWith("_call_output", StringComparison.Ordinal))
            {
                Tps.ObserveToolResult(callId, ReadTimestampMs(root));
            }
        }
    }

    private void ObserveItemCompleted(JsonElement payload)
    {
        if (!IsCurrentTurn(GetString(payload, "turn_id")))
        {
            return;
        }

        var itemType = payload.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
            ? GetString(item, "type")
            : null;
        if (itemType is null)
        {
            Warnings.Add("item_completed:missing-item-type");
            return;
        }

        if (!KnownItemTypes.Contains(itemType))
        {
            Warnings.Add("item:" + itemType);
        }

        if (!TpsRules.IsModelOutputItem(itemType))
        {
            // Tool / user / IO time: it must never reach the TPS denominator. It does, however, mean
            // the displayed number was measured before this activity started, so mark it "held".
            Tps.NoteActivityAfterMeasurement();
            return;
        }

        Tps.ObserveModelOutputItem(
            TryGetLong(payload, "started_at_ms"),
            TryGetLong(payload, "completed_at_ms"));
    }

    private void ApplyCumulativeUsage(JsonElement usage, string carrier)
    {
        var total = GetLong(usage, "total_tokens");
        var input = GetLong(usage, "input_tokens");
        var output = GetLong(usage, "output_tokens");
        if (total <= 0 && input <= 0 && output <= 0)
        {
            return;
        }

        var cached = GetLong(usage, "cached_input_tokens");

        // Every carrier here is CUMULATIVE, so the newest reading simply replaces the previous one.
        // Nothing is ever added, which is what makes a duplicate reading harmless — but it is
        // counted and reported so a future Codex change that starts emitting deltas is visible
        // instead of silently corrupting the totals.
        if (HasCumulativeUsage && total == TotalTokens && input == InputTokens
            && cached == CachedInputTokens && output == OutputTokens)
        {
            DuplicateUsageReadingsIgnored++;
            return;
        }

        if (Carrier == "none")
        {
            Carrier = carrier;
        }
        else if (Carrier != carrier)
        {
            Carrier = "token_usage_record+token_count";
        }

        TotalTokens = total;
        InputTokens = input;
        CachedInputTokens = cached;
        OutputTokens = output;
        ReasoningOutputTokens = GetLong(usage, "reasoning_output_tokens");
        HasCumulativeUsage = true;
        HasRolloutUsage = true;
    }

    /// <summary>Applies a baseline recovered from state_5.sqlite when the rollout tail had nothing.</summary>
    public void ApplyStateDatabaseBaseline(long tokensUsed)
    {
        if (tokensUsed <= 0 || HasRolloutUsage)
        {
            return;
        }

        TotalTokens = tokensUsed;
        OutputTokens = 0;
        InputTokens = tokensUsed;
        CachedInputTokens = 0;
        HasCumulativeUsage = true;
    }

    /// <summary>
    /// Size of the request that produced a response: <c>total_tokens</c>, falling back to the input
    /// side when a carrier omits the total. This is "context used", not the conversation total.
    /// </summary>
    private static long ReadRequestSize(JsonElement usage)
    {
        var total = GetLong(usage, "total_tokens");
        if (total > 0)
        {
            return total;
        }

        var input = GetLong(usage, "input_tokens");
        return input > 0 ? input : GetLong(usage, "output_tokens");
    }

    private static long? ReadTimestampMs(JsonElement root) =>
        DateTimeOffset.TryParse(
            GetString(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUnixTimeMilliseconds()
            : null;

    private static string? GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static long GetLong(JsonElement element, string propertyName) => TryGetLong(element, propertyName) ?? 0;

    private static long? TryGetLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.Number
        && property.TryGetInt64(out var value)
            ? value
            : null;
}
