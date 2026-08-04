namespace Jellyfin.Plugin.Stinger.Detection;

/// <summary>One sampled video frame. Time is seconds from the start of the file.</summary>
public sealed record VideoFrame(double Time, double YAvg, double SatAvg, double YDif, double SceneScore);

/// <summary>One audio metering window. LoudnessM is EBU R128 momentary loudness in LUFS.</summary>
public sealed record AudioFrame(double Time, double LoudnessM);

public sealed class FeatureSeries
{
    /// <summary>Seconds into the file where analysis began.</summary>
    public required double TailStart { get; init; }

    /// <summary>Total runtime of the file in seconds.</summary>
    public required double Duration { get; init; }

    public required IReadOnlyList<VideoFrame> Video { get; init; }

    public required IReadOnlyList<AudioFrame> Audio { get; init; }
}
