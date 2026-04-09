using GAE.Engine.Configuration;

namespace GAE.Engine.Tests;

public class StorylineContextLoaderTests
{
    [Fact]
    public async Task LoadFromFileAsync_LoadsStorylineFieldsFromYamlConfig()
    {
        var storyline = await StorylineContextLoader.LoadFromFileAsync(GetRepoPath("config", "blind-storylines", "haunted-manor.yaml"));

        Assert.Equal("haunted-manor", storyline.Id);
        Assert.Equal("The Haunting of Ashwood Manor", storyline.Name);
        Assert.Equal("A crumbling Victorian manor on a fog-shrouded hilltop", storyline.Setting);
        Assert.Equal("Gothic horror with dark humor", storyline.Tone);
        Assert.Equal("Uncovering family secrets", storyline.Theme);
        Assert.Equal(4, storyline.PlotBeats.Count);
        Assert.Equal("The front door locks behind you", storyline.PlotBeats[0]);
        Assert.Equal("The grand foyer, all dusty chandeliers and portraits with eyes that seem to follow you.", storyline.StartingRoomDescription);
        Assert.Equal(20, storyline.MaxRooms);
    }

    [Fact]
    public async Task LoadFromDirectoryAsync_LoadsAllExampleStorylines()
    {
        var storylines = await StorylineContextLoader.LoadFromDirectoryAsync(GetRepoPath("config", "blind-storylines"));

        Assert.Equal(2, storylines.Count);
        Assert.Contains(storylines, storyline => storyline.Id == "haunted-manor");
        Assert.Contains(storylines, storyline => storyline.Id == "sunken-crown-heist");
        Assert.All(storylines, storyline =>
        {
            Assert.False(string.IsNullOrWhiteSpace(storyline.Name));
            Assert.False(string.IsNullOrWhiteSpace(storyline.Setting));
            Assert.False(string.IsNullOrWhiteSpace(storyline.Tone));
            Assert.False(string.IsNullOrWhiteSpace(storyline.Theme));
            Assert.False(string.IsNullOrWhiteSpace(storyline.StartingRoomDescription));
            Assert.NotEmpty(storyline.PlotBeats);
            Assert.True(storyline.MaxRooms > 0);
        });
    }

    private static string GetRepoPath(params string[] segments)
    {
        var path = AppContext.BaseDirectory;
        for (var index = 0; index < 5; index++)
            path = Path.Combine(path, "..");

        return Path.GetFullPath(Path.Combine(path, Path.Combine(segments)));
    }
}