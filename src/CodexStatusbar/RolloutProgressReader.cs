using System.Text.Json;

namespace CodexStatusbar;

internal readonly record struct SessionProgress(int Turns, int Steps);

/// <summary>
/// Counts confirmed events from the beginning of a rollout without delaying the existing tail
/// recovery. Each background poll scans a bounded batch; once caught up, only appends are parsed.
/// </summary>
internal sealed class RolloutProgressReader
{
    internal const int ReadBudget = 8 * 1024 * 1024;
    private const int BufferSize = 64 * 1024;
    private const int CheckpointSize = 64;

    private readonly byte[] _buffer = new byte[BufferSize];
    private readonly byte[] _line = new byte[BufferSize];
    private readonly byte[] _checkpoint = new byte[CheckpointSize];
    private readonly HashSet<string> _startedTurns = new(StringComparer.Ordinal);
    private readonly HashSet<(string TurnId, string ResponseId)> _responses = [];

    // Codex can record the same completion using both carriers. The value indicates whether a
    // sample first seen through token_count has been matched to an official response ID.
    private readonly Dictionary<(string TurnId, long Total), bool> _responseTotals = [];

    private string? _headIdentity;
    private string? _latestTurnId;
    private long _offset;
    private long _observedLength;
    private DateTime? _lastWriteUtc;
    private long _lastTotal;
    private int _checkpointLength;
    private int _lineLength;
    private bool _skipLine;
    private int _steps;

    /// <summary>Returns null until the captured end of the file has been reached.</summary>
    public SessionProgress? Advance(FileStream stream, long length, string? headIdentity)
    {
        var lastWriteUtc = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
        if (length < _observedLength
            || (length == _observedLength && _lastWriteUtc is { } previousWriteUtc
                && previousWriteUtc != lastWriteUtc)
            || !string.Equals(headIdentity, _headIdentity, StringComparison.Ordinal)
            || !CheckpointMatches(stream))
        {
            Reset(headIdentity);
        }

        _observedLength = length;
        _lastWriteUtc = lastWriteUtc;
        var remaining = Math.Min(length - _offset, ReadBudget);
        if (remaining > 0)
        {
            stream.Position = _offset;
        }

        while (remaining > 0)
        {
            var read = stream.Read(_buffer, 0, (int)Math.Min(remaining, _buffer.Length));
            if (read == 0)
            {
                // The writer truncated the file after we captured its length. Retry from its head.
                Reset(headIdentity);
                return null;
            }

            var bytes = _buffer.AsSpan(0, read);
            Consume(bytes);
            _checkpointLength = Math.Min(read, CheckpointSize);
            bytes[^_checkpointLength..].CopyTo(_checkpoint);
            _offset += read;
            remaining -= read;
        }

        return _offset == length ? new SessionProgress(_startedTurns.Count, _steps) : null;
    }

    private bool CheckpointMatches(FileStream stream)
    {
        if (_checkpointLength == 0)
        {
            return true;
        }

        // A rewrite may keep the same session_meta ID and grow past the last offset between polls.
        stream.Position = _offset - _checkpointLength;
        Span<byte> checkpoint = stackalloc byte[CheckpointSize];
        var previous = checkpoint[.._checkpointLength];
        return stream.ReadAtLeast(previous, previous.Length, false) == previous.Length
            && previous.SequenceEqual(_checkpoint.AsSpan(0, _checkpointLength));
    }

    private void Reset(string? headIdentity)
    {
        _headIdentity = headIdentity;
        _latestTurnId = null;
        _offset = 0;
        _observedLength = 0;
        _lastWriteUtc = null;
        _lastTotal = 0;
        _checkpointLength = 0;
        _lineLength = 0;
        _skipLine = false;
        _steps = 0;
        _startedTurns.Clear();
        _responses.Clear();
        _responseTotals.Clear();
    }

    private void Consume(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            var fragment = newline >= 0 ? bytes[..newline] : bytes;
            if (!_skipLine)
            {
                // Large records are usually tool output. Keep memory bounded even for those.
                if (fragment.Length > _line.Length - _lineLength)
                {
                    _skipLine = true;
                }
                else
                {
                    fragment.CopyTo(_line.AsSpan(_lineLength));
                    _lineLength += fragment.Length;
                }
            }

            if (newline < 0)
            {
                return;
            }

            if (!_skipLine)
            {
                ObserveLine(_line.AsMemory(0, _lineLength));
            }

            _lineLength = 0;
            _skipLine = false;
            bytes = bytes[(newline + 1)..];
        }
    }

    private void ObserveLine(ReadOnlyMemory<byte> line)
    {
        var text = line.Span;
        if (text.IndexOf("\"task_started\""u8) < 0
            && text.IndexOf("\"turn_started\""u8) < 0
            && text.IndexOf("\"token_usage_record\""u8) < 0
            && text.IndexOf("\"token_count\""u8) < 0)
        {
            return;
        }

        if (text.StartsWith("\uFEFF"u8))
        {
            line = line[3..];
        }

        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (ObjectProperty(root, "payload") is not { } payload)
            {
                return;
            }

            switch (StringProperty(root, "type"))
            {
                case "event_msg":
                    ObserveEvent(payload);
                    break;
                case "token_usage_record":
                    ObserveResponse(payload);
                    break;
            }
        }
        catch (JsonException)
        {
            // An invalid record cannot confirm a turn or step. Later records are still readable.
        }
    }

    private void ObserveEvent(JsonElement payload)
    {
        switch (StringProperty(payload, "type"))
        {
            case "task_started":
            case "turn_started":
                if (StringProperty(payload, "turn_id") is { } turnId)
                {
                    _latestTurnId = turnId;
                    _startedTurns.Add(turnId);
                }
                break;

            case "token_count":
                ObserveLegacyUsage(payload);
                break;
        }
    }

    private void ObserveResponse(JsonElement payload)
    {
        if (_headIdentity is { } sessionId && sessionId != "session_meta"
            && StringProperty(payload, "thread_id") is { } threadId
            && !string.Equals(threadId, sessionId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (StringProperty(payload, "turn_id") is not { } turnId
            || StringProperty(payload, "response_id") is not { } responseId
            || UsageTotal(payload, "thread_token_usage") is not { } total
            || !_responses.Add((turnId, responseId)))
        {
            return;
        }

        var sample = (turnId, total);
        var alreadyCounted = _responseTotals.TryGetValue(sample, out var hasResponseId) && !hasResponseId;
        _responseTotals[sample] = true;
        if (!alreadyCounted)
        {
            _steps++;
            _lastTotal = total;
        }
    }

    private void ObserveLegacyUsage(JsonElement payload)
    {
        if (ObjectProperty(payload, "info") is not { } info
            || UsageTotal(info, "total_token_usage") is not { } total)
        {
            return;
        }

        var increase = total - _lastTotal;
        _lastTotal = total;

        // Legacy token_count has no response ID and is also emitted for quota updates. A change
        // covering the reported last request is evidence of a completion in the most recent turn.
        if (_latestTurnId is { } turnId
            && UsageTotal(info, "last_token_usage") is > 0 and var last
            && increase >= last
            && _responseTotals.TryAdd((turnId, total), false))
        {
            _steps++;
        }
    }

    private static JsonElement? ObjectProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Object
            ? property
            : null;

    private static string? StringProperty(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String
            && property.GetString() is { Length: > 0 } value
            ? value
            : null;

    private static long? UsageTotal(JsonElement element, string name)
    {
        if (ObjectProperty(element, name) is not { } usage)
        {
            return null;
        }

        var total = NonnegativeInteger(usage, "total_tokens");
        if (total is > 0)
        {
            return total;
        }

        var input = NonnegativeInteger(usage, "input_tokens");
        var output = NonnegativeInteger(usage, "output_tokens");
        return input is { } inputTokens && output is { } outputTokens && inputTokens <= long.MaxValue - outputTokens
            ? inputTokens + outputTokens
            : total;
    }

    private static long? NonnegativeInteger(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt64(out var value)
            && value >= 0 ? value : null;
}
