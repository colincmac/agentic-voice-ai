namespace Showcase.Authentication.Core;
/// <summary>
/// Keyed services don't provide an accessible API for resolving
/// all the service keys associated with a given type.
/// See https:///github.com/dotnet/runtime/issues/100105 for more info.
/// This internal class is used to track the resource names that have been registered
/// so that they can be resolved in the `IProtectedMetadataProvider` implementation.
/// This is inspired by the implementation used in Orleans. See
/// https:///github.com/dotnet/orleans/blob/005ab200bc91302245857cb75efaa436296a1aae/src/Orleans.Runtime/Hosting/NamedService.cs.
/// See also https://github.com/dotnet/aspnetcore/blob/main/src/OpenApi/src/Services/NamedService.cs
/// </summary>
public sealed class NamedService<TService>(string name)
{
    public string Name { get; } = name;
}
