using System.Diagnostics;

namespace Architecture.Testing;

/// <summary>One span of a trace, whether the test process produced it or a service exported it.</summary>
/// <param name="Service">The service that produced the span, or <see cref="TestProcess"/>.</param>
/// <param name="Source">The activity source or instrumentation scope that produced the span.</param>
/// <param name="Name">The name of the span.</param>
/// <param name="Kind">Client, server, internal, producer or consumer.</param>
/// <param name="TraceId">The trace the span belongs to, as 32 hex characters.</param>
/// <param name="SpanId">The span's own id, as 16 hex characters.</param>
/// <param name="ParentSpanId">The id of the parent span, or null for a root.</param>
/// <param name="Attributes">The span's attributes, with every value as text.</param>
public sealed record Span(
    string Service,
    string Source,
    string Name,
    ActivityKind Kind,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    IReadOnlyDictionary<string, string> Attributes)
{
    /// <summary>The service name the test process appears under in a trace.</summary>
    public const string TestProcess = "test";

    /// <summary>The address a client span called, when it has one.</summary>
    private string? Target => Attribute("url.full") ?? Attribute("http.url");

    /// <summary>The store a span touched, when it touched one.</summary>
    public string? Store => Attribute("db.namespace") ?? Attribute("db.name");

    /// <summary>The value of one attribute, or null when the span does not have it.</summary>
    public string? Attribute(string key) => Attributes.GetValueOrDefault(key);

    /// <inheritdoc />
    public override string ToString()
    {
        var detail = Store is { } store ? $" -> store {store}" : Target is { } target ? $" -> {target}" : "";
        return $"{Service,-13}{Kind,-9}{Name}{detail}";
    }
}
