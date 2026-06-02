using System.ComponentModel;
using Agents.AI.Extensions.AITools;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agents.AI.Extensions.Tests.AITools;

public sealed class NamedAIFunctionProviderTests
{
    [Fact]
    public void Resolve_FromKeyedRegistration_ReturnsFunction()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunction("greet", AIFunctionFactory.Create((string name) => $"hi {name}", "greet"));

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<INamedAIFunctionProvider>();

        var fn = provider.Resolve("greet");

        Assert.Equal("greet", fn.Name);
    }

    [Fact]
    public void TryResolve_UnknownName_ReturnsNull()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunction("greet", AIFunctionFactory.Create(() => "hi", "greet"));

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<INamedAIFunctionProvider>();

        Assert.Null(provider.TryResolve("unknown"));
    }

    [Fact]
    public void Resolve_UnknownName_Throws()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunction("greet", AIFunctionFactory.Create(() => "hi", "greet"));

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<INamedAIFunctionProvider>();

        Assert.Throws<KeyNotFoundException>(() => provider.Resolve("unknown"));
    }

    [Fact]
    public void Register_SameName_LastWins()
    {
        var first = AIFunctionFactory.Create(() => "first", "tool");
        var second = AIFunctionFactory.Create(() => "second", "tool");

        var services = new ServiceCollection();
        services.AddNamedAIFunction("tool", first);
        services.AddNamedAIFunction("tool", second);

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<INamedAIFunctionProvider>();

        Assert.Same(second, provider.Resolve("tool"));
    }

    [Fact]
    public void ResolveAll_AggregatesMissingNamesInOneException()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunction("a", AIFunctionFactory.Create(() => "a", "a"));

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<INamedAIFunctionProvider>();

        var ex = Assert.Throws<KeyNotFoundException>(
            () => provider.ResolveAll(["a", "b", "c"]));

        Assert.Contains("b", ex.Message);
        Assert.Contains("c", ex.Message);
    }

    [Fact]
    public void ResolveAll_PreservesOrder()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunction("a", AIFunctionFactory.Create(() => "a", "a"));
        services.AddNamedAIFunction("b", AIFunctionFactory.Create(() => "b", "b"));
        services.AddNamedAIFunction("c", AIFunctionFactory.Create(() => "c", "c"));

        var sp = services.BuildServiceProvider();
        var provider = sp.GetRequiredService<INamedAIFunctionProvider>();

        var resolved = provider.ResolveAll(["c", "a", "b"]);

        Assert.Equal(["c", "a", "b"], resolved.Select(f => f.Name));
    }

    [Fact]
    public void Scoped_Lifetime_ProducesPerScopeInstance()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunction(
            "scoped",
            sp => AIFunctionFactory.Create(() => "ok", "scoped"),
            ServiceLifetime.Scoped);

        var root = services.BuildServiceProvider();

        using var scope1 = root.CreateScope();
        var fn1 = scope1.ServiceProvider.GetRequiredService<INamedAIFunctionProvider>().Resolve("scoped");

        using var scope2 = root.CreateScope();
        var fn2 = scope2.ServiceProvider.GetRequiredService<INamedAIFunctionProvider>().Resolve("scoped");

        Assert.NotSame(fn1, fn2);
    }

    [Fact]
    public void Singleton_Lifetime_ReturnsSameInstance()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunction(
            "singleton",
            sp => AIFunctionFactory.Create(() => "ok", "singleton"),
            ServiceLifetime.Singleton);

        var root = services.BuildServiceProvider();

        using var scope1 = root.CreateScope();
        var fn1 = scope1.ServiceProvider.GetRequiredService<INamedAIFunctionProvider>().Resolve("singleton");

        using var scope2 = root.CreateScope();
        var fn2 = scope2.ServiceProvider.GetRequiredService<INamedAIFunctionProvider>().Resolve("singleton");

        Assert.Same(fn1, fn2);
    }

    [Fact]
    public void IAIToolCollection_TollsAreDiscoveredByName()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunctionProvider();
        services.AddScoped<IAIToolCollection, TwoToolsCollection>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<INamedAIFunctionProvider>();

        Assert.NotNull(provider.TryResolve("alpha"));
        Assert.NotNull(provider.TryResolve("beta"));
        Assert.Null(provider.TryResolve("gamma"));
    }

    [Fact]
    public void Keyed_Registration_TakesPrecedenceOverIAIToolCollection()
    {
        var collectionTool = AIFunctionFactory.Create([Description("from collection")] () => "from-collection", "alpha");
        var keyedTool = AIFunctionFactory.Create([Description("keyed")] () => "from-keyed", "alpha");

        var services = new ServiceCollection();
        services.AddScoped<IAIToolCollection>(_ => new SingleToolCollection(collectionTool));
        services.AddNamedAIFunction("alpha", keyedTool);

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<INamedAIFunctionProvider>();

        Assert.Same(keyedTool, provider.Resolve("alpha"));
    }

    [Fact]
    public void Names_EnumeratesIAIToolCollectionContents()
    {
        var services = new ServiceCollection();
        services.AddNamedAIFunctionProvider();
        services.AddScoped<IAIToolCollection, TwoToolsCollection>();

        var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var provider = scope.ServiceProvider.GetRequiredService<INamedAIFunctionProvider>();

        var names = provider.Names.ToHashSet();

        Assert.Contains("alpha", names);
        Assert.Contains("beta", names);
    }

    private sealed class TwoToolsCollection : IAIToolCollection
    {
        public IEnumerable<AITool> AsAITools()
        {
            yield return AIFunctionFactory.Create(() => "alpha", "alpha");
            yield return AIFunctionFactory.Create(() => "beta", "beta");
        }
    }

    private sealed class SingleToolCollection(AITool tool) : IAIToolCollection
    {
        public IEnumerable<AITool> AsAITools() => [tool];
    }
}
