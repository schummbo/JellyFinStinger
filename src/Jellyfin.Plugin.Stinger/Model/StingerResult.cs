namespace Jellyfin.Plugin.Stinger.Model;

public enum StingerState
{
    Unknown = 0,
    HasStinger = 1,
    NoStinger = 2,
}

public enum StingerKind
{
    MidCredits = 0,
    PostCredits = 1,
    AudioOnly = 2,
}

public sealed class StingerSpan
{
    public long StartTicks { get; set; }

    public long EndTicks { get; set; }

    public StingerKind Kind { get; set; }
}

public sealed class StingerResult
{
    public Guid ItemId { get; set; }

    public StingerState State { get; set; }

    public bool HasMidCredits { get; set; }

    public bool HasPostCredits { get; set; }

    /// <summary>Where the closing credits begin, when detection found them.</summary>
    public long? CreditsStartTicks { get; set; }

    public List<StingerSpan> Stingers { get; set; } = new();

    /// <summary>Media segment ids this plugin created for the item, so rescans can replace them.</summary>
    public List<Guid> SegmentIds { get; set; } = new();

    /// <summary>The exact overview marker line we appended, so it can be removed cleanly.</summary>
    public string? AppliedOverviewMarker { get; set; }

    public DateTime DetectedAtUtc { get; set; }

    /// <summary>Item.DateModified at scan time; a change triggers a rescan.</summary>
    public DateTime ItemDateModifiedUtc { get; set; }

    public string? DetectionNotes { get; set; }

    /// <summary>TMDB keyword flags: null = source unavailable/not queried.</summary>
    public bool? TmdbDuringCredits { get; set; }

    public bool? TmdbAfterCredits { get; set; }

    /// <summary>True when the Wikipedia list contains the film. Never false-negative — absence stays null.</summary>
    public bool? WikipediaListed { get; set; }
}
