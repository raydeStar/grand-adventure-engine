using System.Collections.Concurrent;

namespace GAE.Core.Registry;

/// <summary>Thread-safe in-memory content registry with fuzzy name lookup.</summary>
public class ContentRegistry<T> : IContentRegistry<T> where T : IRegistryEntry
{
    // Shortest entry name allowed to match by being *contained in* the query. Below this, generic
    // short names would match nearly every lookup.
    private const int MinimumReverseMatchLength = 4;

    private readonly ConcurrentDictionary<string, T> _byId = new(StringComparer.OrdinalIgnoreCase);

    public T? GetById(string id) => _byId.GetValueOrDefault(id);

    /// <summary>
    /// Resolves an entry by name: exact match first, then the closest partial match.
    ///
    /// Ordering matters here. Enumerating a ConcurrentDictionary yields no defined order, so
    /// picking the "first" partial match made the same lookup resolve to different entries across
    /// restarts. Candidates are ranked deterministically instead, preferring the longest — and
    /// therefore most specific — matching name.
    /// </summary>
    public T? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return default;

        var snapshot = _byId.Values.ToArray();

        // Exact match first, tie-broken by id so a duplicate name resolves consistently.
        var exact = snapshot
            .Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (exact is not null) return exact;

        // Partial match. The reverse direction (the query containing an entry's name) is limited to
        // names of a few characters or more, so a short entry name cannot match almost any query.
        return snapshot
            .Where(e => !string.IsNullOrWhiteSpace(e.Name))
            .Where(e => e.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                || (e.Name.Length >= MinimumReverseMatchLength
                    && name.Contains(e.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(e => e.Name.Length)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
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
