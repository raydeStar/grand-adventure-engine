namespace GAE.Core.Registry;

/// <summary>Generic read/write registry for game content definitions.</summary>
public interface IContentRegistry<T> where T : IRegistryEntry
{
    T? GetById(string id);
    T? FindByName(string name);
    IReadOnlyList<T> GetAll();
    bool Exists(string id);
    void Register(T entry);
    void Remove(string id);
    void Clear();
    void Load(IEnumerable<T> entries);
    int Count { get; }
}
