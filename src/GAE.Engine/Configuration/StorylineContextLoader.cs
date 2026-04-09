using GAE.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GAE.Engine.Configuration;

/// <summary>
/// Loads Blind Adventure storyline definitions from YAML files.
/// The loader keeps the format intentionally flat so authored config stays easy to read and edit.
/// </summary>
public static class StorylineContextLoader
{
    /// <summary>
    /// Deserializes a storyline definition from YAML text.
    /// </summary>
    public static StorylineContext LoadFromYaml(string yamlContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(yamlContent);

        var storyline = BuildDeserializer().Deserialize<StorylineContext>(yamlContent);
        return storyline ?? new StorylineContext();
    }

    /// <summary>
    /// Reads a storyline definition from a YAML file on disk.
    /// </summary>
    public static async Task<StorylineContext> LoadFromFileAsync(string filePath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var yamlContent = await File.ReadAllTextAsync(filePath, ct);
        return LoadFromYaml(yamlContent);
    }

    /// <summary>
    /// Reads every YAML storyline definition from a directory and returns them ordered by file name.
    /// </summary>
    public static async Task<IReadOnlyList<StorylineContext>> LoadFromDirectoryAsync(string directoryPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        var files = Directory.EnumerateFiles(directoryPath, "*.yaml", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var storylines = new List<StorylineContext>(files.Count);
        foreach (var file in files)
            storylines.Add(await LoadFromFileAsync(file, ct));

        return storylines;
    }

    private static IDeserializer BuildDeserializer() => new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();
}