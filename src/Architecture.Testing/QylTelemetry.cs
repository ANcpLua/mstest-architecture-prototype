using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Qyl.Api.Contracts.Common.Pagination;
using Qyl.Api.Contracts.OTel.Logs;
using Qyl.Api.Contracts.OTel.Metrics;
using Qyl.Api.Contracts.OTel.Traces;

namespace Architecture.Testing;

/// <summary>Reads telemetry persisted by the qyl platform. The caller owns the supplied HTTP client.</summary>
/// <param name="client">A client whose base address is the qyl collector.</param>
public sealed class QylTelemetry(HttpClient client)
{
    internal async Task<HttpResponseMessage> ExportAsync(string route, ReadOnlyMemory<byte> payload, string? contentType,
        string? contentEncoding, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, route);
        request.Content = new ReadOnlyMemoryContent(payload);
        if (contentType is not null)
        {
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        }

        if (!string.IsNullOrEmpty(contentEncoding))
        {
            request.Content.Headers.ContentEncoding.Add(contentEncoding);
        }

        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads every page of logs correlated to one trace, preserving the qyl log records.</summary>
    public async Task<IReadOnlyList<LogRecord>> LogsAsync(string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        var logs = new List<LogRecord>();
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var route = $"api/v1/logs?trace_id={Uri.EscapeDataString(traceId)}&limit=1000";
        var next = route;
        while (true)
        {
            var page = await ReadAsync<CursorPageLogRecord>(next, cancellationToken).ConfigureAwait(false);
            logs.AddRange(page.Items);
            if (!page.HasMore)
            {
                return logs;
            }

            var cursor = page.NextCursor;
            if (string.IsNullOrEmpty(cursor) || !cursors.Add(cursor))
            {
                throw new InvalidDataException("qyl returned an invalid log continuation cursor.");
            }

            next = $"{route}&cursor={Uri.EscapeDataString(cursor)}";
        }
    }

    /// <summary>Reads the span events qyl stored for one trace, across all of its spans.</summary>
    public async Task<IReadOnlyList<SpanEvent>> SpanEventsAsync(string traceId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(traceId);
        var trace = await ReadAsync<JsonElement>($"api/v1/traces/{Uri.EscapeDataString(traceId)}", cancellationToken).ConfigureAwait(false);
        return [.. trace.GetProperty("spans").EnumerateArray()
            .Where(span => span.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            .SelectMany(span => span.GetProperty("events").EnumerateArray())
            .Select(spanEvent => spanEvent.Deserialize<SpanEvent>()
                ?? throw new InvalidDataException("qyl returned an empty span event."))];
    }

    /// <summary>Queries metric buckets for an explicit time window; metrics are not individual trace events.</summary>
    public Task<MetricQueryResult> QueryMetricAsync(string name, DateTimeOffset start, DateTimeOffset end,
        MetricAggregation aggregation = MetricAggregation.Last, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var route = $"api/v1/metrics/{Uri.EscapeDataString(name)}/query"
            + $"?start_time={Timestamp(start)}&end_time={Timestamp(end)}&aggregation={aggregation.ToString().ToLowerInvariant()}";
        return ReadAsync<MetricQueryResult>(route, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(string route, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<T>(route, cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidDataException("qyl returned an empty JSON response.");

    private static string Timestamp(DateTimeOffset value) =>
        Uri.EscapeDataString(value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}
