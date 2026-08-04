using Jellyfin.Plugin.Stinger.Detection;
using Jellyfin.Plugin.Stinger.Model;
using Jellyfin.Plugin.Stinger.Sources;
using Xunit;

namespace Jellyfin.Plugin.Stinger.Tests;

public class ResultMergerTests
{
    private static readonly Guid ItemId = Guid.NewGuid();

    private static DetectionOutcome Outcome(StingerState state, params DetectedStinger[] stingers) => new()
    {
        State = state,
        CreditsStartSeconds = state == StingerState.Unknown ? null : 5000,
        Stingers = stingers,
    };

    [Fact]
    public void DetectionYes_WinsRegardlessOfSources()
    {
        var result = ResultMerger.Merge(
            ItemId,
            Outcome(StingerState.HasStinger, new DetectedStinger(5100, 5130, StingerKind.MidCredits)),
            tmdb: null,
            wikipediaListed: null);

        Assert.Equal(StingerState.HasStinger, result.State);
        Assert.True(result.HasMidCredits);
        Assert.False(result.HasPostCredits);
        Assert.Single(result.Stingers);
    }

    [Fact]
    public void DetectionNo_SourcesSilent_NoStinger()
    {
        var result = ResultMerger.Merge(
            ItemId, Outcome(StingerState.NoStinger), new TmdbStingerFlags(false, false), null);

        Assert.Equal(StingerState.NoStinger, result.State);
    }

    [Fact]
    public void DetectionNo_SourceSaysYes_DemotedToUnknown()
    {
        var result = ResultMerger.Merge(
            ItemId, Outcome(StingerState.NoStinger), new TmdbStingerFlags(During: false, After: true), null);

        Assert.Equal(StingerState.Unknown, result.State);
        Assert.Contains("detection found none", result.DetectionNotes, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DetectionUnknown_SourceSaysYes_HasStingerWithoutTimestamps()
    {
        var result = ResultMerger.Merge(
            ItemId, Outcome(StingerState.Unknown), new TmdbStingerFlags(During: true, After: false), null);

        Assert.Equal(StingerState.HasStinger, result.State);
        Assert.True(result.HasMidCredits);
        Assert.Empty(result.Stingers);
    }

    [Fact]
    public void DetectionUnknown_OnlyWikipediaYes_TreatedAsPostCredits()
    {
        var result = ResultMerger.Merge(ItemId, Outcome(StingerState.Unknown), null, wikipediaListed: true);

        Assert.Equal(StingerState.HasStinger, result.State);
        Assert.True(result.HasPostCredits);
    }

    [Fact]
    public void DetectionUnknown_NoSignals_StaysUnknown()
    {
        var result = ResultMerger.Merge(ItemId, Outcome(StingerState.Unknown), null, null);

        Assert.Equal(StingerState.Unknown, result.State);
    }

    [Fact]
    public void AudioOnlyStinger_CountsAsPostCredits()
    {
        var result = ResultMerger.Merge(
            ItemId,
            Outcome(StingerState.HasStinger, new DetectedStinger(5500, 5510, StingerKind.AudioOnly)),
            null,
            null);

        Assert.True(result.HasPostCredits);
        Assert.False(result.HasMidCredits);
    }
}
