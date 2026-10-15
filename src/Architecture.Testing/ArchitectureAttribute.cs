namespace Architecture.Testing;

/// <summary>
/// Replaces <see cref="TestClassAttribute"/>. It decides how the stacked bricks are coupled,
/// starts them once per test class, runs every test of the class against the running system
/// and then checks the trace of the test against the architecture the class claims.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public abstract class ArchitectureAttribute : TestClassAttribute
{
    /// <summary>The name of the architecture, taken from the attribute: <c>Microservices</c>.</summary>
    public string Kind => GetType().Name[..^nameof(Attribute).Length];

    /// <inheritdoc />
    public sealed override TestMethodAttribute GetTestMethodAttribute(TestMethodAttribute testMethodAttribute)
    {
        ArgumentNullException.ThrowIfNull(testMethodAttribute);

        // MSTest discovers source locations from the original attribute before creating this execution wrapper.
        return new ArchitectureTestMethodAttribute(this, testMethodAttribute);
    }

    /// <summary>Says what is wrong with this set of bricks for this architecture, or nothing when it fits.</summary>
    protected internal abstract string? Check(IReadOnlyList<BrickAttribute> bricks);

    /// <summary>The store a brick writes to. This is where the architectures differ.</summary>
    /// <param name="brick">The service name declared by the brick.</param>
    protected internal abstract string StoreOf(string brick);

    /// <summary>
    /// Checks what really happened against the claim. A trace without a single span from a brick is not a pass:
    /// then nothing was measured.
    /// </summary>
    /// <returns>What does not fit, or null when the trace fits the architecture.</returns>
    protected internal string? Verify(TestTrace trace, IReadOnlyList<string> bricks)
    {
        ArgumentNullException.ThrowIfNull(trace);
        ArgumentNullException.ThrowIfNull(bricks);

        if (trace.Services.Count == 0)
        {
            return "No span from any brick reached the test, so the architecture was not measured.";
        }

        foreach (var service in trace.Services)
        {
            if (!bricks.Contains(service))
            {
                return $"{service} produced spans but is not a stacked brick.";
            }

            var foreign = trace.StoresOf(service).Where(store => store != StoreOf(service)).ToList();
            if (foreign.Count > 0)
            {
                return $"{service} wrote to {string.Join(", ", foreign)}, but as part of a {Kind} it owns the store {StoreOf(service)}.";
            }
        }

        return VerifyShape(trace);
    }

    /// <summary>The rule that sets this architecture apart, checked on the trace.</summary>
    protected abstract string? VerifyShape(TestTrace trace);
}

/// <summary>One deployable with its own store.</summary>
public sealed class MonolithAttribute : ArchitectureAttribute
{
    /// <inheritdoc />
    protected internal override string? Check(IReadOnlyList<BrickAttribute> bricks) =>
        bricks.Count == 1 ? null : $"A monolith is one deployable, but {bricks.Count} bricks are stacked.";

    /// <inheritdoc />
    protected internal override string StoreOf(string brick) => brick;

    /// <inheritdoc />
    protected override string? VerifyShape(TestTrace trace) =>
        trace.Calls.FirstOrDefault(call => call.From != Span.TestProcess) is { From: { } from, To: { } to }
            ? $"A monolith makes no network calls between its parts, but {from} called {to}."
            : null;
}

/// <summary>Several deployables, each with a store of its own.</summary>
public sealed class MicroservicesAttribute : ArchitectureAttribute
{
    /// <inheritdoc />
    protected internal override string? Check(IReadOnlyList<BrickAttribute> bricks) =>
        bricks.Count >= 2 ? null : $"Microservices are several deployables, but {bricks.Count} brick is stacked.";

    /// <inheritdoc />
    protected internal override string StoreOf(string brick) => brick;

    /// <inheritdoc />
    protected override string? VerifyShape(TestTrace trace)
    {
        var shared = trace.Services
            .SelectMany(service => trace.StoresOf(service).Select(store => (Store: store, Service: service)))
            .GroupBy(use => use.Store, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);

        return shared is null
            ? null
            : $"Microservices own their stores, but {shared.Key} was written by {string.Join(" and ", shared.Select(use => use.Service))}.";
    }
}

/// <summary>Several deployables around one shared store.</summary>
public sealed class DistributedMonolithAttribute : ArchitectureAttribute
{
    /// <summary>The name of the store all bricks share.</summary>
    public const string SharedStore = "shared";

    /// <inheritdoc />
    protected internal override string? Check(IReadOnlyList<BrickAttribute> bricks) =>
        bricks.Count >= 2 ? null : $"A distributed monolith is several deployables, but {bricks.Count} brick is stacked.";

    /// <inheritdoc />
    protected internal override string StoreOf(string brick) => SharedStore;

    /// <inheritdoc />
    protected override string? VerifyShape(TestTrace trace)
    {
        var writers = trace.Services.Where(service => trace.StoresOf(service).Count > 0).ToList();
        var stores = writers.SelectMany(trace.StoresOf).Distinct(StringComparer.Ordinal).ToList();

        return writers.Count >= 2 && stores.Count > 1
            ? $"A distributed monolith shares one store, but {string.Join(", ", writers)} wrote to {string.Join(", ", stores)}."
            : null;
    }
}
