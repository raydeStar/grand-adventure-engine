using GAE.Core.Models;

namespace GAE.Engine.Tests;

public class NpcDispositionTests
{
    [Fact]
    public void DecayTowardBaseline_AfterOneHour_HalvesExcess()
    {
        var state = new NpcDispositionState
        {
            Emotion = "flustered",
            Intensity = 80,
            Baseline = 40,
            LastUpdated = DateTimeOffset.UtcNow.AddHours(-1)
        };

        state.DecayTowardBaseline(TimeSpan.FromHours(1));

        // Excess was 40, after 1 half-life should be ~20, so intensity ~60
        Assert.Equal(60, state.Intensity);
    }

    [Fact]
    public void DecayTowardBaseline_AfterTwoHours_QuartersExcess()
    {
        var state = new NpcDispositionState
        {
            Emotion = "angry",
            Intensity = 80,
            Baseline = 40
        };

        state.DecayTowardBaseline(TimeSpan.FromHours(2));

        // Excess was 40, after 2 half-lives should be ~10, so intensity ~50
        Assert.Equal(50, state.Intensity);
    }

    [Fact]
    public void DecayTowardBaseline_ZeroElapsed_NoChange()
    {
        var state = new NpcDispositionState
        {
            Emotion = "happy",
            Intensity = 70,
            Baseline = 40
        };

        state.DecayTowardBaseline(TimeSpan.Zero);

        Assert.Equal(70, state.Intensity);
        Assert.Equal("happy", state.Emotion);
    }

    [Fact]
    public void DecayTowardBaseline_LongTime_ResetsToNeutral()
    {
        var state = new NpcDispositionState
        {
            Emotion = "flustered",
            Intensity = 65,
            Baseline = 40,
            Reason = "a stolen kiss"
        };

        // After 10 hours, intensity should be very close to baseline
        state.DecayTowardBaseline(TimeSpan.FromHours(10));

        Assert.Equal("neutral", state.Emotion);
        Assert.Null(state.Reason);
        Assert.InRange(state.Intensity, 38, 42);
    }

    [Fact]
    public void DecayTowardBaseline_BelowBaseline_DecaysUpward()
    {
        var state = new NpcDispositionState
        {
            Emotion = "afraid",
            Intensity = 10,
            Baseline = 40
        };

        state.DecayTowardBaseline(TimeSpan.FromHours(1));

        // Excess was -30, halved to -15, so intensity ~25
        Assert.Equal(25, state.Intensity);
    }

    [Theory]
    [InlineData("neutral", 40, 40, "neutral")]
    [InlineData("angry", 85, 40, "overwhelmingly angry")]
    [InlineData("happy", 65, 40, "very happy")]
    [InlineData("curious", 50, 40, "somewhat curious")]
    [InlineData("annoyed", 42, 40, "slightly annoyed")]
    public void ToFlatDisposition_FormatsCorrectly(string emotion, int intensity, int baseline, string expected)
    {
        var state = new NpcDispositionState
        {
            Emotion = emotion,
            Intensity = intensity,
            Baseline = baseline
        };

        Assert.Equal(expected, state.ToFlatDisposition());
    }

    [Fact]
    public void DefaultNpc_HasNeutralDispositionState()
    {
        var npc = new Npc();

        Assert.Equal("neutral", npc.Disposition);
        Assert.Equal("neutral", npc.DispositionState.Emotion);
        Assert.Equal(40, npc.DispositionState.Intensity);
        Assert.Equal(40, npc.DispositionState.Baseline);
    }
}
