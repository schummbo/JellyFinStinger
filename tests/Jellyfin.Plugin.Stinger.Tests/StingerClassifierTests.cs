using Jellyfin.Plugin.Stinger.Detection;
using Jellyfin.Plugin.Stinger.Model;
using Xunit;

namespace Jellyfin.Plugin.Stinger.Tests;

public class StingerClassifierTests
{
    private const double Dt = 0.5;

    private static readonly DetectionOptions Options = new();

    /// <summary>Builds a video series from (durationSeconds, yavg, satavg) segments, starting at t=0.</summary>
    private static List<VideoFrame> Video(params (double Duration, double YAvg, double SatAvg)[] segments)
    {
        var frames = new List<VideoFrame>();
        var t = 0.0;
        foreach (var (duration, yavg, satavg) in segments)
        {
            for (var end = t + duration; t < end; t += Dt)
            {
                frames.Add(new VideoFrame(t, yavg, satavg, 1, 0));
            }
        }

        return frames;
    }

    private static FeatureSeries Series(List<VideoFrame> video, List<AudioFrame>? audio = null) => new()
    {
        TailStart = 0,
        Duration = video.Count * Dt,
        Video = video,
        Audio = audio ?? new List<AudioFrame>(),
    };

    private static (double Duration, double YAvg, double SatAvg) Content(double d) => (d, 120, 80);

    private static (double Duration, double YAvg, double SatAvg) Credits(double d) => (d, 30, 5);

    [Fact]
    public void CleanCredits_NoStinger()
    {
        var outcome = StingerClassifier.Classify(Series(Video(Content(300), Credits(400))), Options);

        Assert.Equal(StingerState.NoStinger, outcome.State);
        Assert.NotNull(outcome.CreditsStartSeconds);
        Assert.InRange(outcome.CreditsStartSeconds!.Value, 295, 310);
        Assert.Empty(outcome.Stingers);
    }

    [Fact]
    public void ContentIslandMidCredits_DetectedAsMidCredits()
    {
        var outcome = StingerClassifier.Classify(
            Series(Video(Content(300), Credits(120), Content(30), Credits(200))), Options);

        Assert.Equal(StingerState.HasStinger, outcome.State);
        var stinger = Assert.Single(outcome.Stingers);
        Assert.Equal(StingerKind.MidCredits, stinger.Kind);
        Assert.InRange(stinger.StartSeconds, 410, 430);
        Assert.InRange(stinger.EndSeconds, 440, 460);
    }

    [Fact]
    public void ContentAtEnd_DetectedAsPostCredits()
    {
        var outcome = StingerClassifier.Classify(
            Series(Video(Content(300), Credits(300), Content(25))), Options);

        Assert.Equal(StingerState.HasStinger, outcome.State);
        var stinger = Assert.Single(outcome.Stingers);
        Assert.Equal(StingerKind.PostCredits, stinger.Kind);
        Assert.InRange(stinger.StartSeconds, 590, 610);
    }

    [Fact]
    public void MidAndPostCredits_BothDetected()
    {
        var outcome = StingerClassifier.Classify(
            Series(Video(Content(300), Credits(100), Content(30), Credits(150), Content(20))), Options);

        Assert.Equal(StingerState.HasStinger, outcome.State);
        Assert.Equal(2, outcome.Stingers.Count);
        Assert.Contains(outcome.Stingers, s => s.Kind == StingerKind.MidCredits);
        Assert.Contains(outcome.Stingers, s => s.Kind == StingerKind.PostCredits);
    }

    [Fact]
    public void NoCreditsSignature_Unknown()
    {
        var outcome = StingerClassifier.Classify(Series(Video(Content(600))), Options);

        Assert.Equal(StingerState.Unknown, outcome.State);
    }

    [Fact]
    public void ShortContentBlip_IgnoredAsStinger()
    {
        var outcome = StingerClassifier.Classify(
            Series(Video(Content(300), Credits(200), (4, 120, 80), Credits(200))), Options);

        Assert.Equal(StingerState.NoStinger, outcome.State);
    }

    [Fact]
    public void TooFewSamples_Unknown()
    {
        var outcome = StingerClassifier.Classify(Series(Video(Content(5))), Options);

        Assert.Equal(StingerState.Unknown, outcome.State);
    }

    [Fact]
    public void AudioOverBlackTail_DetectedAsAudioOnlyStinger()
    {
        // Credits end, 25s of black; a voice clip plays 6s into the black.
        var video = Video(Content(300), Credits(300), (25, 5, 0));
        var audio = new List<AudioFrame>();
        for (var t = 0.0; t < 625; t += 0.1)
        {
            var active = t is > 606 and < 615;
            audio.Add(new AudioFrame(t, active ? -20 : -70));
        }

        var outcome = StingerClassifier.Classify(Series(video, audio), Options);

        Assert.Equal(StingerState.HasStinger, outcome.State);
        var stinger = Assert.Single(outcome.Stingers);
        Assert.Equal(StingerKind.AudioOnly, stinger.Kind);
    }

    [Fact]
    public void SilentBlackTail_TrimmedNotDetected()
    {
        var outcome = StingerClassifier.Classify(
            Series(Video(Content(300), Credits(300), (30, 5, 0))), Options);

        Assert.Equal(StingerState.NoStinger, outcome.State);
    }
}
