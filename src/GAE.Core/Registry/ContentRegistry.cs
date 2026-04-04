using System.Collections.Concurrent;

namespace GAE.Core.Registry;

/// <summary>Thread-safe in-memory content registry with fuzzy name lookup.</summary>
public class ContentRegistry<T> : IContentRegistry<T> where T : IRegistryEntry
{
    private readonly ConcurrentDictionary<string, T> _byId = new(StringComparer.OrdinalIgnoreCase);

    public T? GetById(string id) => _byId.GetValueOrDefault(id);

    public T? FindByName(string name)
    {
        // Exact match first
        var entry = _byId.Values.FirstOrDefault(e =>
            e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is not null) return entry;

        // Partial/contains match
        return _byId.Values.FirstOrDefault(e =>
            e.Name.Contains(name, StringComparison.OrdinalIgnoreCase) ||
            name.Contains(e.Name, StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<T> GetAll() => _byId.Values.OrderBy(e => e.Name).ToList();

    public bool Exists(string id) => _byId.ContainsKey(id);

    public void Register(T entry) => _byId[entry.Id] = entry;

    public void Remove(string id) => _byId.TryRemove(id, out _);

    public void Clear() => _byId.Clear();

    public void Load(IEnumerable<T> entries)
    {
        foreach (var e in entries)
            _byId[e.Id] = e;
    }

    public int Count => _byId.Count;
}
