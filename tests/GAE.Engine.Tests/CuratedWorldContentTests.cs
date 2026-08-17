using GAE.Core.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace GAE.Engine.Tests;

/// <summary>
/// Guards the authored world graph and Moonfall quest arc against quiet YAML drift. A broken exit
/// is merely a typo in source control and an existential wall in a player's evening.
/// </summary>
public class CuratedWorldContentTests
{
    [Fact]
    public async Task DefaultWorld_AllAuthoredRoomsAreReachableFromSpawn()
    {
        var seed = await LoadYamlAsync<WorldSeed>("config", "lore-seed.yaml");
        var rooms = Assert.IsAssignableFrom<IReadOnlyCollection<RoomSeed>>(seed.Rooms);
        var byId = rooms.ToDictionary(room => room.Id, StringComparer.OrdinalIgnoreCase);

        Assert.True(byId.ContainsKey("spawn"));
        Assert.All(rooms, room => Assert.All(room.Exits.Values, targetId => Assert.True(
            byId.ContainsKey(targetId),
            $"Room '{room.Id}' points to missing room '{targetId}'.")));

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "spawn" };
        var queue = new Queue<string>();
        queue.Enqueue("spawn");
        while (queue.TryDequeue(out var roomId))
        {
            foreach (var targetId in byId[roomId].Exits.Values)
            {
                if (visited.Add(targetId))
                    queue.Enqueue(targetId);
            }
        }

        var unreachable = byId.Keys.Where(id => !visited.Contains(id)).OrderBy(id => id).ToList();
        Assert.True(unreachable.Count == 0, $"Unreachable curated rooms: {string.Join(", ", unreachable)}");
    }

    [Fact]
    public async Task MoonfallArc_HasCompleteQuestAndEntityReferences()
    {
        var world = await LoadYamlAsync<WorldSeed>("config", "lore-seed.yaml");
        var questSeed = await LoadYamlAsync<QuestSeed>("config", "quests.yaml");
        var roomIds = world.Rooms.Select(room => room.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var npcs = world.Rooms.SelectMany(room => room.Npcs).ToDictionary(npc => npc.Id, StringComparer.OrdinalIgnoreCase);
        var itemIds = world.Rooms
            .SelectMany(room => room.Items
                .Concat(room.Npcs.SelectMany(npc => npc.Loot))
                .Concat(room.Npcs.SelectMany(npc => npc.ShopInventory)))
            .Select(item => item.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var quests = questSeed.Quests.ToDictionary(quest => quest.Id, StringComparer.OrdinalIgnoreCase);
        var moonfallQuests = quests.Values.Where(quest => quest.Id.StartsWith("moonfall_", StringComparison.OrdinalIgnoreCase)).ToList();

        Assert.Equal(7, roomIds.Count(id => id.StartsWith("moonfall_", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(4, moonfallQuests.Count);
        Assert.Contains("moonfall_gate", world.Rooms.Single(room => room.Id == "back_alley").Exits.Values);

        foreach (var quest in moonfallQuests)
        {
            Assert.True(npcs.ContainsKey(quest.GiverId), $"Quest '{quest.Id}' has missing giver '{quest.GiverId}'.");
            Assert.All(quest.Prerequisites, prerequisite => Assert.True(
                quests.ContainsKey(prerequisite),
                $"Quest '{quest.Id}' has missing prerequisite '{prerequisite}'."));

            foreach (var objective in quest.Stages.SelectMany(stage => stage.Objectives))
            {
                var targetExists = objective.Type switch
                {
                    ObjectiveType.Discover => objective.TargetId is not null && roomIds.Contains(objective.TargetId),
                    ObjectiveType.TalkTo or ObjectiveType.Kill => objective.TargetId is not null && npcs.ContainsKey(objective.TargetId),
                    ObjectiveType.Collect => objective.TargetId is not null && itemIds.Contains(objective.TargetId),
                    ObjectiveType.Deliver => objective.TargetId is not null && npcs.ContainsKey(objective.TargetId)
                        && objective.RequiredItemId is not null && itemIds.Contains(objective.RequiredItemId),
                    _ => true
                };
                Assert.True(targetExists, $"Quest '{quest.Id}' objective '{objective.Id}' has an invalid target reference.");
            }
        }

        Assert.Contains("moonfall_missing_midnight", npcs["orla_quill"].QuestsOffered);
        Assert.Contains("moonfall_beastly_business", npcs["barnaby_rook"].QuestsOffered);
    }

    private static async Task<T> LoadYamlAsync<T>(params string[] segments)
    {
        var path = AppContext.BaseDirectory;
        for (var index = 0; index < 5; index++)
            path = Path.Combine(path, "..");

        var fullPath = Path.GetFullPath(Path.Combine(path, Path.Combine(segments)));
        var yaml = await File.ReadAllTextAsync(fullPath);
        return new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .WithEnumNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build()
            .Deserialize<T>(yaml);
    }

    private sealed class WorldSeed
    {
        public List<RoomSeed> Rooms { get; set; } = [];
    }

    private sealed class RoomSeed
    {
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, string> Exits { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<NpcSeed> Npcs { get; set; } = [];
        public List<ItemSeed> Items { get; set; } = [];
    }

    private sealed class NpcSeed
    {
        public string Id { get; set; } = string.Empty;
        public List<string> QuestsOffered { get; set; } = [];
        public List<ItemSeed> Loot { get; set; } = [];
        public List<ItemSeed> ShopInventory { get; set; } = [];
    }

    private sealed class ItemSeed
    {
        public string Id { get; set; } = string.Empty;
    }

    private sealed class QuestSeed
    {
        public List<QuestDefinition> Quests { get; set; } = [];
    }
}
