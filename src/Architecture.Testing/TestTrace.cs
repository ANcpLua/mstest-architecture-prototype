using System.Text;
using Qyl.Telemetry.SemanticConventions;
using Qyl.Telemetry.Testing;

namespace Architecture.Testing;

/// <summary>The canonical telemetry graph viewed by an architecture check.</summary>
public sealed class TestTrace
{
    internal TestTrace(string id, TelemetrySnapshot capture)
    {
        Telemetry = new QylTrace(id, capture);
        Services = [.. Telemetry.Services.Where(service => service != SpanRecorder.TestProcess)];
    }

    /// <summary>The trace id, as 32 hex characters.</summary>
    public string Id => Telemetry.TraceId;

    /// <summary>The shared trace graph, with its complete unfiltered capture.</summary>
    public QylTrace Telemetry { get; }

    /// <summary>Every span of this trace, preserving its typed values and metadata.</summary>
    public IReadOnlyList<SpanRecord> Spans => Telemetry.Spans;

    /// <summary>The service calls established by matching client and server spans.</summary>
    public IReadOnlyList<Call> Calls => Telemetry.Calls;

    /// <summary>The services that produced spans, without the test process.</summary>
    public IReadOnlyList<string> Services { get; }

    /// <summary>The complete identities of the stores a service touched.</summary>
    public IReadOnlyList<StoreIdentity> StoresOf(string service) => Telemetry.StoresOf(service);

    /// <summary>The architecture evidence as a few lines for the runner output.</summary>
    public string Summary()
    {
        var text = new StringBuilder()
            .AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Trace {Id}: {Spans.Count} span(s)");
        foreach (var span in Spans)
        {
            var store = span.GetString("db.namespace");
            var target = span.GetString("url.full");
            var detail = store is not null ? $" -> store {store}" : target is not null ? $" -> {target}" : "";
            _ = text.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
                $"  {span.Service,-13}{span.Kind,-9}{span.Name}{detail}");
        }

        var calls = Calls.Select(call => $"{call.From} -> {call.To}").ToArray();
        _ = text.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"Calls: {(calls.Length == 0 ? "none" : string.Join(", ", calls))}");
        _ = text.AppendLine(System.Globalization.CultureInfo.InvariantCulture,
            $"Stores: {(Services.Count == 0 ? "none" : string.Join("; ", Services.Select(service => $"{service}: {string.Join(", ", StoresOf(service).Select(store => store.ToString()).DefaultIfEmpty("none"))}")))}");
        return text.ToString();
    }
}
