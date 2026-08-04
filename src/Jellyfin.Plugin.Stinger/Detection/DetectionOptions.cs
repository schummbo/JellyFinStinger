namespace Jellyfin.Plugin.Stinger.Detection;

/// <summary>
/// Classifier thresholds. Luma/saturation values are on ffmpeg signalstats' 0-255 scale.
/// </summary>
public sealed class DetectionOptions
{
    /// <summary>A frame darker than this and desaturated reads as "credits-like".</summary>
    public double CreditsMaxYAvg { get; init; } = 70;

    public double CreditsMaxSatAvg { get; init; } = 32;

    /// <summary>Below this the frame is effectively black.</summary>
    public double BlackMaxYAvg { get; init; } = 20;

    /// <summary>Momentary loudness below this counts as silence.</summary>
    public double SilenceLufs { get; init; } = -55;

    /// <summary>Loudness above this counts as active audio (dialogue/effects).</summary>
    public double AudioActiveLufs { get; init; } = -45;

    /// <summary>The credits region must open with at least this many continuous credits-like seconds.</summary>
    public double MinCreditsSeconds { get; init; } = 25;

    /// <summary>From credits start to end of file, at least this fraction must be credits-like.</summary>
    public double MinCreditsDensity { get; init; } = 0.5;

    /// <summary>Minimum length for a visual stinger candidate.</summary>
    public double MinStingerSeconds { get; init; } = 8;

    /// <summary>Content runs separated by less than this merge into one candidate.</summary>
    public double MergeGapSeconds { get; init; } = 4;

    /// <summary>Majority-vote smoothing window over the credits/content signal.</summary>
    public double SmoothSeconds { get; init; } = 2.5;

    /// <summary>A candidate ending within this of the effective end counts as post-credits.</summary>
    public double EndProximitySeconds { get; init; } = 12;

    /// <summary>Minimum active-audio run over black tail to call an audio-only stinger.</summary>
    public double MinAudioStingerSeconds { get; init; } = 3;

    /// <summary>Audio-only stingers must start at least this long after the last visible frame.</summary>
    public double AudioStingerMinDelaySeconds { get; init; } = 4;
}
