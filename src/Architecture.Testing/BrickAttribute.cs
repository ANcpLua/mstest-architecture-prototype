using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Architecture.Testing;

/// <summary>
/// One part of the system under test. Stack as many bricks on a test class as the test needs.
/// A new kind of brick is a new class derived from this one; the architecture attributes stay as they are.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public abstract class BrickAttribute : Attribute
{
    /// <summary>The type that stands for the brick. Its assembly is the host that knows how to start it.</summary>
    public abstract Type Part { get; }

    /// <summary>Finds the brick in the system the host has put together.</summary>
    protected internal abstract IResourceWithEnvironment FindIn(IDistributedApplicationBuilder host);
}

/// <summary>A brick that is a whole project: the type argument is the project itself.</summary>
/// <typeparam name="TProject">The project, as the Aspire host names it, for example <c>Projects.Orders_Api</c>.</typeparam>
public sealed class ServiceAttribute<TProject> : BrickAttribute
    where TProject : IProjectMetadata, new()
{
    /// <inheritdoc />
    public override Type Part => typeof(TProject);

    /// <inheritdoc />
    protected internal override IResourceWithEnvironment FindIn(IDistributedApplicationBuilder host) =>
        Brick.Find<TProject>(host.Resources)
        ?? throw new ArchitectureException($"The host {Part.Assembly.GetName().Name} does not start {Part.Name}.");
}

/// <summary>How a project type is matched to what the host starts.</summary>
internal static class Brick
{
    public static ProjectResource? Find<TProject>(IEnumerable<IResource> resources)
        where TProject : IProjectMetadata, new()
    {
        var path = new TProject().ProjectPath;
        return resources.OfType<ProjectResource>().SingleOrDefault(project =>
            string.Equals(project.GetProjectMetadata().ProjectPath, path, StringComparison.Ordinal));
    }
}
