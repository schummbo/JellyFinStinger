using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Stinger.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>TMDB API key used for the keyword cross-check. Empty disables the TMDB source.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Cross-check detection against Wikipedia's list of films with post-credits scenes.</summary>
    public bool EnableWikipediaSource { get; set; } = true;

    /// <summary>Write Outro media segments so "skip credits" lands on the stinger.</summary>
    public bool EnableSegments { get; set; } = true;

    /// <summary>Append a stinger marker line to the movie overview.</summary>
    public bool EnableOverviewText { get; set; } = true;

    /// <summary>Also add an overview line for confirmed no-stinger movies.</summary>
    public bool AddNoStingerOverview { get; set; }

    /// <summary>Show a "stay tuned" message on clients when playback reaches the credits.</summary>
    public bool EnableNotification { get; set; } = true;

    public string NotificationText { get; set; } = "Stay tuned — there's a scene during or after the credits.";

    /// <summary>How many minutes from the end of the file to analyze.</summary>
    public int TailWindowMinutes { get; set; } = 15;

    /// <summary>When true, the next scan re-analyzes every movie, then resets itself.</summary>
    public bool ForceRescan { get; set; }
}
