namespace GAE.Core.Registry;

/// <summary>Base contract for anything stored in a content registry.</summary>
public interface IRegistryEntry
{
    string Id { get; }
    string Name { get; }
    string? Description { get; }
}
