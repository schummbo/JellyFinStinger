using Jellyfin.Plugin.Stinger.Model;
using Jellyfin.Plugin.Stinger.Sources;

namespace Jellyfin.Plugin.Stinger.Detection;

/// <summary>
/// Merges local detection with external signals into a final three-state result.
///
/// Rules (external sources can affirm presence but never absence):
///  - detection HasStinger            → HasStinger (timestamps from detection)
///  - detection NoStinger + source yes → Unknown ("sources disagree" — detection likely missed it)
///  - detection NoStinger + no signal  → NoStinger
///  - detection Unknown  + source yes  → HasStinger (kinds from TMDB flags, no timestamps)
///  - detection Unknown  + no signal   → Unknown
/// </summary>
public static class ResultMerger
{
    public static StingerResult Merge(
        Guid itemId,
        DetectionOutcome detection,
        TmdbStingerFlags? tmdb,
        bool? wikipediaListed)
    {
        var externalYes = tmdb is { During: true } or { After: true } || wikipediaListed == true;

        var result = new StingerResult
        {
            ItemId = itemId,
            CreditsStartTicks = ToTicks(detection.CreditsStartSeconds),
            DetectedAtUtc = DateTime.UtcNow,
            DetectionNotes = detection.Notes,
            TmdbDuringCredits = tmdb?.During,
            TmdbAfterCredits = tmdb?.After,
            WikipediaListed = wikipediaListed,
        };

        switch (detection.State)
        {
            case StingerState.HasStinger:
                result.State = StingerState.HasStinger;
                foreach (var s in detection.Stingers)
                {
                    result.Stingers.Add(new StingerSpan
                    {
                        StartTicks = ToTicks(s.StartSeconds)!.Value,
                        EndTicks = ToTicks(s.EndSeconds)!.Value,
                        Kind = s.Kind,
                    });
                }

                result.HasMidCredits = detection.Stingers.Any(s => s.Kind == StingerKind.MidCredits);
                result.HasPostCredits = detection.Stingers.Any(s => s.Kind is StingerKind.PostCredits or StingerKind.AudioOnly);
                break;

            case StingerState.NoStinger when externalYes:
                result.State = StingerState.Unknown;
                result.DetectionNotes = $"{detection.Notes}; external sources report a stinger but detection found none";
                break;

            case StingerState.NoStinger:
                result.State = StingerState.NoStinger;
                break;

            case StingerState.Unknown when externalYes:
                result.State = StingerState.HasStinger;
                result.HasMidCredits = tmdb?.During == true;
                result.HasPostCredits = tmdb?.After == true || (tmdb?.During != true && wikipediaListed == true);
                result.DetectionNotes = $"{detection.Notes}; presence from external sources only (no timestamps)";
                break;

            default:
                result.State = StingerState.Unknown;
                break;
        }

        return result;
    }

    private static long? ToTicks(double? seconds) =>
        seconds is null ? null : (long)(seconds.Value * TimeSpan.TicksPerSecond);
}
