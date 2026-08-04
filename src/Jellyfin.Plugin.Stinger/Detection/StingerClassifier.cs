using System.Globalization;
using Jellyfin.Plugin.Stinger.Model;

namespace Jellyfin.Plugin.Stinger.Detection;

public sealed record DetectedStinger(double StartSeconds, double EndSeconds, StingerKind Kind);

public sealed class DetectionOutcome
{
    public StingerState State { get; init; }

    public double? CreditsStartSeconds { get; init; }

    public IReadOnlyList<DetectedStinger> Stingers { get; init; } = Array.Empty<DetectedStinger>();

    public string Notes { get; init; } = string.Empty;
}

/// <summary>
/// Pure classification over a feature time series. No I/O — unit-testable in isolation.
///
/// Model: closing credits are a sustained run of dark, desaturated frames reaching the end of
/// the file; a stinger is a contiguous island of photographic content inside or after that run.
/// </summary>
public static class StingerClassifier
{
    public static DetectionOutcome Classify(FeatureSeries series, DetectionOptions options)
    {
        var frames = series.Video;
        if (frames.Count < 40)
        {
            return Unknown("insufficient video samples in analysis window");
        }

        var dt = (frames[^1].Time - frames[0].Time) / (frames.Count - 1);
        if (dt <= 0)
        {
            return Unknown("non-monotonic timestamps");
        }

        var creditsLike = Smooth(
            frames.Select(f => f.YAvg <= options.CreditsMaxYAvg && f.SatAvg <= options.CreditsMaxSatAvg).ToArray(),
            Math.Max(1, (int)Math.Round(options.SmoothSeconds / dt)));

        // Effective end: trim trailing padding that is both black and silent.
        var endIdx = frames.Count - 1;
        while (endIdx > 0
               && frames[endIdx].YAvg <= options.BlackMaxYAvg
               && !AudioActiveAt(series.Audio, frames[endIdx].Time, options.SilenceLufs))
        {
            endIdx--;
        }

        var endTime = frames[endIdx].Time;

        var creditsIdx = FindCreditsStart(frames, creditsLike, endIdx, dt, options);
        if (creditsIdx < 0)
        {
            return Unknown("no credits signature found (credits may run over footage or be stylized)");
        }

        var creditsStart = frames[creditsIdx].Time;
        var stingers = new List<DetectedStinger>();

        foreach (var (start, end) in ContentRuns(frames, creditsLike, creditsIdx, endIdx, dt, options))
        {
            var kind = end >= endTime - options.EndProximitySeconds
                ? StingerKind.PostCredits
                : StingerKind.MidCredits;
            stingers.Add(new DetectedStinger(start, end, kind));
        }

        var audioStinger = FindAudioOnlyStinger(series, frames, endIdx, endTime, stingers, options);
        if (audioStinger is not null)
        {
            stingers.Add(audioStinger);
        }

        var notes = string.Create(
            CultureInfo.InvariantCulture,
            $"creditsStart={creditsStart:F1}s effectiveEnd={endTime:F1}s candidates={stingers.Count} dt={dt:F2}s");

        return new DetectionOutcome
        {
            State = stingers.Count > 0 ? StingerState.HasStinger : StingerState.NoStinger,
            CreditsStartSeconds = creditsStart,
            Stingers = stingers,
            Notes = notes,
        };
    }

    private static int FindCreditsStart(
        IReadOnlyList<VideoFrame> frames, bool[] creditsLike, int endIdx, double dt, DetectionOptions o)
    {
        var minRun = Math.Max(1, (int)Math.Round(o.MinCreditsSeconds / dt));

        // Suffix counts of credits-like frames let us check density from i..endIdx cheaply.
        var suffix = new int[endIdx + 2];
        for (var i = endIdx; i >= 0; i--)
        {
            suffix[i] = suffix[i + 1] + (creditsLike[i] ? 1 : 0);
        }

        var run = 0;
        for (var i = 0; i <= endIdx; i++)
        {
            run = creditsLike[i] ? run + 1 : 0;
            if (run < minRun)
            {
                continue;
            }

            var start = i - run + 1;
            var density = (double)suffix[start] / (endIdx - start + 1);
            if (density >= o.MinCreditsDensity)
            {
                return start;
            }
        }

        return -1;
    }

    private static IEnumerable<(double Start, double End)> ContentRuns(
        IReadOnlyList<VideoFrame> frames, bool[] creditsLike, int creditsIdx, int endIdx, double dt, DetectionOptions o)
    {
        var runs = new List<(double Start, double End)>();
        var runStart = -1;
        for (var i = creditsIdx; i <= endIdx + 1; i++)
        {
            var isContent = i <= endIdx && !creditsLike[i];
            if (isContent && runStart < 0)
            {
                runStart = i;
            }
            else if (!isContent && runStart >= 0)
            {
                runs.Add((frames[runStart].Time, frames[i - 1].Time));
                runStart = -1;
            }
        }

        // Merge runs separated by brief credits-like gaps (e.g. a cut to black inside a stinger).
        var merged = new List<(double Start, double End)>();
        foreach (var r in runs)
        {
            if (merged.Count > 0 && r.Start - merged[^1].End <= o.MergeGapSeconds)
            {
                merged[^1] = (merged[^1].Start, r.End);
            }
            else
            {
                merged.Add(r);
            }
        }

        return merged.Where(r => r.End - r.Start >= o.MinStingerSeconds);
    }

    private static DetectedStinger? FindAudioOnlyStinger(
        FeatureSeries series,
        IReadOnlyList<VideoFrame> frames,
        int endIdx,
        double endTime,
        IReadOnlyList<DetectedStinger> visual,
        DetectionOptions o)
    {
        // Last frame with visible content (text or picture).
        var lastVis = endIdx;
        while (lastVis > 0 && frames[lastVis].YAvg <= o.BlackMaxYAvg)
        {
            lastVis--;
        }

        var lastVisTime = frames[lastVis].Time;
        if (endTime - lastVisTime < o.AudioStingerMinDelaySeconds + o.MinAudioStingerSeconds)
        {
            return null;
        }

        if (visual.Any(v => v.EndSeconds > lastVisTime))
        {
            return null;
        }

        // An active-audio run over the black tail, starting clearly after the visuals ended
        // (so credits music fading over black doesn't count).
        double? runStart = null;
        double lastActive = 0;
        foreach (var a in series.Audio)
        {
            if (a.Time <= lastVisTime + o.AudioStingerMinDelaySeconds || a.Time > endTime + 1)
            {
                continue;
            }

            if (a.LoudnessM >= o.AudioActiveLufs)
            {
                runStart ??= a.Time;
                lastActive = a.Time;
                if (lastActive - runStart >= o.MinAudioStingerSeconds)
                {
                    return new DetectedStinger(runStart.Value, endTime, StingerKind.AudioOnly);
                }
            }
            else if (runStart is not null && a.Time - lastActive > 1.5)
            {
                runStart = null;
            }
        }

        return null;
    }

    private static bool AudioActiveAt(IReadOnlyList<AudioFrame> audio, double time, double silenceLufs)
    {
        // Audio series can be empty (no audio stream / extraction failed) — treat as silent.
        foreach (var a in audio)
        {
            if (Math.Abs(a.Time - time) <= 1.0 && a.LoudnessM > silenceLufs)
            {
                return true;
            }
        }

        return false;
    }

    private static bool[] Smooth(bool[] input, int window)
    {
        if (window <= 1)
        {
            return input;
        }

        var result = new bool[input.Length];
        for (var i = 0; i < input.Length; i++)
        {
            var from = Math.Max(0, i - window);
            var to = Math.Min(input.Length - 1, i + window);
            var count = 0;
            for (var j = from; j <= to; j++)
            {
                if (input[j])
                {
                    count++;
                }
            }

            result[i] = count * 2 > to - from + 1;
        }

        return result;
    }

    private static DetectionOutcome Unknown(string reason) => new()
    {
        State = StingerState.Unknown,
        Notes = reason,
    };
}
