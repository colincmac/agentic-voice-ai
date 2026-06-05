using global::Agents.AI.ContactCenter.IvrWorkflow.Tools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.ContactCenter.Tests.IvrWorkflow.Tools;

public sealed class IvrToolRegistryTests
{
    private static AIFunction StubFunction(string name) =>
        AIFunctionFactory.Create(() => $"hi from {name}", name);

    [Fact]
    public void Builder_AccumulatesBindings_InRegistrationOrder()
    {
        var builder = new IvrToolRegistryBuilder("triage");

        builder.Add(new ToolBinding("alpha", ServiceLifetime.Singleton, _ => StubFunction("alpha")));
        builder.Add(new ToolBinding("beta", ServiceLifetime.Scoped, _ => StubFunction("beta")));
        builder.Add(new ToolBinding("gamma", ServiceLifetime.Transient, _ => StubFunction("gamma")));

        var registry = builder.Build();

        Assert.Equal("triage", registry.AgentKey);
        Assert.Equal(new[] { "alpha", "beta", "gamma" }, registry.Names);
    }

    [Fact]
    public void Builder_LastWinsOnDuplicateName_PreservesOriginalOrdering()
    {
        var builder = new IvrToolRegistryBuilder("triage");

        var first = StubFunction("first");
        var second = StubFunction("second");

        builder.Add(new ToolBinding("alpha", ServiceLifetime.Singleton, _ => first));
        builder.Add(new ToolBinding("beta", ServiceLifetime.Singleton, _ => StubFunction("beta")));
        builder.Add(new ToolBinding("alpha", ServiceLifetime.Scoped, _ => second));

        var registry = builder.Build();

        // The name keeps its original position even though the binding was replaced.
        Assert.Equal(new[] { "alpha", "beta" }, registry.Names);

        Assert.True(registry.TryGetBinding("alpha", out var binding));
        Assert.Equal(ServiceLifetime.Scoped, binding.Lifetime);
        Assert.Same(second, binding.Factory(null!));
    }

    [Fact]
    public void TryGetBinding_ReturnsFalse_WhenNameIsUnknown()
    {
        var builder = new IvrToolRegistryBuilder("triage");
        builder.Add(new ToolBinding("alpha", ServiceLifetime.Singleton, _ => StubFunction("alpha")));

        var registry = builder.Build();

        Assert.False(registry.TryGetBinding("missing", out var binding));
        Assert.Equal(default, binding);
    }

    [Fact]
    public void TryGetBinding_ReturnsTrue_WhenNameExists()
    {
        var builder = new IvrToolRegistryBuilder("triage");
        var fn = StubFunction("alpha");
        builder.Add(new ToolBinding("alpha", ServiceLifetime.Singleton, _ => fn));

        var registry = builder.Build();

        Assert.True(registry.TryGetBinding("alpha", out var binding));
        Assert.Equal("alpha", binding.Name);
        Assert.Equal(ServiceLifetime.Singleton, binding.Lifetime);
        Assert.Same(fn, binding.Factory(null!));
    }

    [Fact]
    public void Build_ReturnsImmutableSnapshot_NotLiveView()
    {
        var builder = new IvrToolRegistryBuilder("triage");
        builder.Add(new ToolBinding("alpha", ServiceLifetime.Singleton, _ => StubFunction("alpha")));

        var registry = builder.Build();

        // Mutating the builder after Build() must not affect the snapshot.
        builder.Add(new ToolBinding("beta", ServiceLifetime.Singleton, _ => StubFunction("beta")));

        Assert.Equal(new[] { "alpha" }, registry.Names);
        Assert.False(registry.TryGetBinding("beta", out _));
    }

    [Fact]
    public void Builder_RejectsBindingWithEmptyName()
    {
        var builder = new IvrToolRegistryBuilder("triage");

        Assert.Throws<ArgumentException>(() =>
            builder.Add(new ToolBinding(string.Empty, ServiceLifetime.Singleton, _ => StubFunction("x"))));
    }

    [Fact]
    public void Builder_RejectsNullFactory()
    {
        var builder = new IvrToolRegistryBuilder("triage");

        Assert.Throws<ArgumentNullException>(() =>
            builder.Add(new ToolBinding("alpha", ServiceLifetime.Singleton, null!)));
    }

    [Fact]
    public void Builder_RejectsEmptyAgentKey()
    {
        Assert.Throws<ArgumentException>(() => new IvrToolRegistryBuilder(string.Empty));
    }
}
