using System.Diagnostics;

/// <summary>The store this service writes to. Every write leaves a span, the way a database client would.</summary>
internal sealed class Store(string name)
{
    public static readonly ActivitySource Source = new("Shop.Store");

    public string Name => name;

    public void Write(string what)
    {
        using var activity = Source.StartActivity($"write {what}", ActivityKind.Client);
        activity?.SetTag("db.system.name", "memory");
        activity?.SetTag("db.namespace", name);
    }
}
