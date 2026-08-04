using Jellyfin.Plugin.Stinger.Model;

namespace Jellyfin.Plugin.Stinger.Detection;

/// <summary>
/// Builds and idempotently applies the "🎬 …" marker line appended to movie overviews.
/// </summary>
public static class OverviewMarker
{
    public const string Prefix = "🎬 ";

    private static readonly string[] KnownMarkers =
    {
        "🎬 Mid-credits scene",
        "🎬 Post-credits scene",
        "🎬 Mid- and post-credits scenes",
        "🎬 No scene during or after the credits",
        "🎬 Stingers: Mid-credits scene",
        "🎬 Stingers: Post-credits scene",
        "🎬 Stingers: Mid- and post-credits scenes",
        "🎬 Stingers: No scene during or after the credits",
    };

    public static string? BuildMarker(StingerResult result, bool addNoStinger)
    {
        return result.State switch
        {
            StingerState.HasStinger when result.HasMidCredits && result.HasPostCredits => KnownMarkers[6],
            StingerState.HasStinger when result.HasMidCredits => KnownMarkers[4],
            StingerState.HasStinger => KnownMarkers[5],
            StingerState.NoStinger when addNoStinger => KnownMarkers[7],
            _ => null,
        };
    }

    /// <summary>Returns the new overview, or null when no change is needed.</summary>
    public static string? Apply(string? overview, string? marker)
    {
        var lines = (overview ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(l => !KnownMarkers.Contains(l.Trim()))
            .ToList();

        while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var body = string.Join('\n', lines);
        var updated = marker is null
            ? body
            : (body.Length == 0 ? marker : $"{body}\n\n{marker}");

        return string.Equals(updated, overview ?? string.Empty, StringComparison.Ordinal) ? null : updated;
    }
}
