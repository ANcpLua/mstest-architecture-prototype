using Qyl;
using Qyl.Telemetry.SemanticConventions.Activities;

/// <summary>The store this service writes to. Every write leaves a span, the way a database client would.</summary>
internal sealed class Store(string name)
{
    public string Name => name;

    public void Write(string what)
    {
        using var activity = Spans.DbClient(DbSystemName.Custom("memory"),
            collectionName: what, @namespace: name, operationName: "write", serverPort: null);
    }
}
