using System.Diagnostics;
using System.Text;

namespace Architecture.Testing;

/// <summary>A call from one service into another, read from the trace.</summary>
/// <param name="From">The service whose client span made the call.</param>
/// <param name="To">The service whose server span answered it.</param>
public readonly record struct Call(string From, string To)
{
    /// <inheritdoc />
    public override string ToString() => $"{From} -> {To}";
}

/// <summary>Everything one test caused, as the spans of the test process and of the services tell it.</summary>
public sealed class TestTrace
{
    private readonly ILookup<string, Span> _byService;

    internal TestTrace(string id, IReadOnlyList<Span> spans)
    {
        Id = id;
        Spans = spans;
        _byService = spans.ToLookup(span => span.Service, StringComparer.Ordinal);

        var byId = spans.GroupBy(span => span.SpanId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        Calls = spans
            .Where(span => span.Kind == ActivityKind.Server && span.ParentSpanId is { } parentId
                && byId.TryGetValue(parentId, out var parent)
                && parent.Kind == ActivityKind.Client
                && parent.Service != span.Service)
            .Select(span => new Call(byId[span.ParentSpanId!].Service, span.Service))
            .Distinct()
            .ToList();

        Services = spans
            .Select(span => span.Service)
            .Where(service => service != Span.TestProcess)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The trace id, as 32 hex characters.</summary>
    public string Id { get; }

    /// <summary>Every span of the trace, in the order it was recorded.</summary>
    public IReadOnlyList<Span> Spans { get; }

    /// <summary>The services that produced spans, without the test process.</summary>
    public IReadOnlyList<string> Services { get; }

    /// <summary>The calls between services, and from the test process into a service.</summary>
    public IReadOnlyList<Call> Calls { get; }

    /// <summary>The stores one service touched in this trace.</summary>
    public IReadOnlyList<string> StoresOf(string service) =>
        _byService[service].Select(span => span.Store).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>The trace as a few lines of text, for the output of a test.</summary>
    public string Summary()
    {
        var text = new StringBuilder()
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Trace {Id}: {Spans.Count} span(s)");
        foreach (var span in Spans)
        {
            text.Append("  ").AppendLine(span.ToString());
        }

        text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Calls: {(Calls.Count == 0 ? "none" : string.Join(", ", Calls))}");
        text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Stores: {(Services.Count == 0 ? "none" : string.Join("; ", Services.Select(service => $"{service}: {string.Join(", ", StoresOf(service).DefaultIfEmpty("none"))}")))}");
        return text.ToString();
    }
}
