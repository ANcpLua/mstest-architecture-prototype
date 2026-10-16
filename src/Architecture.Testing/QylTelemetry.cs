using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Qyl.Api.Contracts.Common.Pagination;
using Qyl.Api.Contracts.OTel.Logs;
using Qyl.Api.Contracts.OTel.Metrics;

namespace Architecture.Testing;

/// <summary>Reads telemetry persisted by the qyl platform. The caller owns the supplied HTTP client.</summary>
/// <param name="client">A client whose base address is the qyl collector.</param>
public sealed class QylTelemetry(HttpClient client)
{
    internal async Task ExportAsync(string route, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        using var body = new ReadOnlyMemoryContent(payload);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/x-protobuf");
        using var response = await client.PostAsync(route, body, cancellationToken).ConfigureAwait(false);
        _ = response.EnsureSuccessStatusCode();
        if ((await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)).Length > 0)
        {
            throw new InvalidDataException("qyl did not accept the complete telemetry export.");
        }
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
