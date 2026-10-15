namespace Architecture.Testing;

/// <summary>One executable in the system under test. Stack as many bricks as the test needs.</summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public abstract class BrickAttribute : Attribute
{
    /// <summary>A type from the service's executable assembly.</summary>
    public abstract Type Part { get; }

    /// <summary>The service name used for discovery, telemetry and store ownership.</summary>
    public abstract string Name { get; }
}

/// <summary>A service launched as its own process from the referenced project output.</summary>
/// <typeparam name="TService">A public type in the service's executable assembly.</typeparam>
/// <param name="name">The service name used in configuration and traces.</param>
public sealed class ServiceAttribute<TService>(string name) : BrickAttribute
{
    /// <inheritdoc />
    public override Type Part => typeof(TService);

    /// <inheritdoc />
    public override string Name { get; } = name;
}
