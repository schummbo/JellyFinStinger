using Jellyfin.Plugin.Stinger.Detection;
using Jellyfin.Plugin.Stinger.Model;
using Xunit;

namespace Jellyfin.Plugin.Stinger.Tests;

public class OverviewMarkerTests
{
    private static StingerResult Result(StingerState state, bool mid = false, bool post = false) => new()
    {
        State = state,
        HasMidCredits = mid,
        HasPostCredits = post,
    };

    [Fact]
    public void AppendsMarkerToOverview()
    {
        var marker = OverviewMarker.BuildMarker(Result(StingerState.HasStinger, post: true), false);

        Assert.Equal("🎬 Post-credits scene", marker);
        Assert.Equal("A movie.\n\n🎬 Post-credits scene", OverviewMarker.Apply("A movie.", marker));
    }

    [Fact]
    public void Idempotent_NoChangeWhenMarkerAlreadyPresent()
    {
        var marker = OverviewMarker.BuildMarker(Result(StingerState.HasStinger, post: true), false);
        var overview = OverviewMarker.Apply("A movie.", marker)!;

        Assert.Null(OverviewMarker.Apply(overview, marker));
    }

    [Fact]
    public void ReplacesStaleMarker()
    {
        var old = OverviewMarker.Apply("A movie.", "🎬 Mid-credits scene")!;
        var updated = OverviewMarker.Apply(old, "🎬 Post-credits scene");

        Assert.Equal("A movie.\n\n🎬 Post-credits scene", updated);
    }

    [Fact]
    public void RemovesMarkerWhenResultBecomesNone()
    {
        var old = OverviewMarker.Apply("A movie.", "🎬 Post-credits scene")!;

        Assert.Equal("A movie.", OverviewMarker.Apply(old, null));
    }

    [Fact]
    public void UnknownState_ProducesNoMarker()
    {
        Assert.Null(OverviewMarker.BuildMarker(Result(StingerState.Unknown), addNoStinger: true));
    }

    [Fact]
    public void NoStinger_MarkerOnlyWhenOptedIn()
    {
        Assert.Null(OverviewMarker.BuildMarker(Result(StingerState.NoStinger), addNoStinger: false));
        Assert.Equal("🎬 No scene during or after the credits", OverviewMarker.BuildMarker(Result(StingerState.NoStinger), addNoStinger: true));
    }

    [Fact]
    public void BothKinds_CombinedMarker()
    {
        Assert.Equal(
            "🎬 Mid- and post-credits scenes",
            OverviewMarker.BuildMarker(Result(StingerState.HasStinger, mid: true, post: true), false));
    }
}
