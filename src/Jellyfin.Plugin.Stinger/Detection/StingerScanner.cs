using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Stinger.Data;
using Jellyfin.Plugin.Stinger.Model;
using Jellyfin.Plugin.Stinger.Sources;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.Detection;

/// <summary>
/// Analyzes a single movie for stingers and applies the configured labeling (segments,
/// overview marker). Shared by the full-library scheduled task and the on-add watcher so
/// both funnel through identical logic.
/// </summary>
public class StingerScanner
{
    private const string SegmentProviderId = "jellyfin.plugin.stinger";

    private readonly IMediaSegmentManager _segmentManager;
    private readonly FfmpegFeatureExtractor _extractor;
    private readonly TmdbKeywordSource _tmdbSource;
    private readonly WikipediaListSource _wikipediaSource;
    private readonly StingerStore _store;
    private readonly ILogger<StingerScanner> _logger;

    public StingerScanner(
        IMediaSegmentManager segmentManager,
        FfmpegFeatureExtractor extractor,
        TmdbKeywordSource tmdbSource,
        WikipediaListSource wikipediaSource,
        StingerStore store,
        ILogger<StingerScanner> logger)
    {
        _segmentManager = segmentManager;
        _extractor = extractor;
        _tmdbSource = tmdbSource;
        _wikipediaSource = wikipediaSource;
        _store = store;
        _logger = logger;
    }

    /// <summary>Returns false when the movie was skipped (too short, or unchanged since the last scan).</summary>
    public async Task<bool> ScanMovieAsync(Movie movie, bool force, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(movie.Path) || movie.RunTimeTicks is null or < TimeSpan.TicksPerMinute * 20)
        {
            return false;
        }

        var existing = _store.Get(movie.Id);
        if (!force && existing is not null && existing.ItemDateModifiedUtc == movie.DateModified)
        {
            return false;
        }

        var config = Plugin.Instance!.Configuration;
        var runtime = TimeSpan.FromTicks(movie.RunTimeTicks!.Value);

        DetectionOutcome detection;
        try
        {
            var features = await _extractor.ExtractAsync(
                movie.Path, runtime, TimeSpan.FromMinutes(config.TailWindowMinutes), cancellationToken).ConfigureAwait(false);
            detection = StingerClassifier.Classify(features, new DetectionOptions());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "ffmpeg analysis failed for {Movie}", movie.Name);
            detection = new DetectionOutcome { State = StingerState.Unknown, Notes = $"analysis failed: {ex.Message}" };
        }

        var tmdbId = movie.GetProviderId(MetadataProvider.Tmdb);
        var tmdb = tmdbId is null ? null : await _tmdbSource.GetFlagsAsync(tmdbId, cancellationToken).ConfigureAwait(false);
        var wiki = await _wikipediaSource.IsListedAsync(movie.Name, movie.ProductionYear, cancellationToken).ConfigureAwait(false);

        var result = ResultMerger.Merge(movie.Id, detection, tmdb, wiki);
        result.ItemDateModifiedUtc = movie.DateModified;

        await ApplySegmentsAsync(movie, result, existing, cancellationToken).ConfigureAwait(false);
        await ApplyOverviewAsync(movie, result, cancellationToken).ConfigureAwait(false);

        _store.Set(result);
        _logger.LogInformation(
            "{Movie}: {State} (mid={Mid} post={Post}) — {Notes}",
            movie.Name, result.State, result.HasMidCredits, result.HasPostCredits, result.DetectionNotes);
        return true;
    }

    private async Task ApplySegmentsAsync(
        Movie movie, StingerResult result, StingerResult? existing, CancellationToken cancellationToken)
    {
        if (existing is not null)
        {
            foreach (var segmentId in existing.SegmentIds)
            {
                try
                {
                    await _segmentManager.DeleteSegmentAsync(segmentId).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Could not delete old segment {Id}", segmentId);
                }
            }
        }

        var config = Plugin.Instance!.Configuration;
        if (!config.EnableSegments || result.CreditsStartTicks is null)
        {
            return;
        }

        // Outro segments cover credits-only stretches, each ending where a stinger (or the
        // file) begins — so a client's "skip credits" lands on the stinger, not past it.
        var endTicks = movie.RunTimeTicks!.Value;
        var spans = new List<(long Start, long End)>();
        var cursor = result.CreditsStartTicks.Value;
        foreach (var stinger in result.Stingers.OrderBy(s => s.StartTicks))
        {
            if (stinger.StartTicks - cursor > TimeSpan.TicksPerSecond * 5)
            {
                spans.Add((cursor, stinger.StartTicks));
            }

            cursor = Math.Max(cursor, stinger.EndTicks);
        }

        if (endTicks - cursor > TimeSpan.TicksPerSecond * 5)
        {
            spans.Add((cursor, endTicks));
        }

        foreach (var (start, end) in spans)
        {
            var created = await _segmentManager.CreateSegmentAsync(
                new MediaSegmentDto
                {
                    Id = Guid.NewGuid(),
                    ItemId = movie.Id,
                    Type = MediaSegmentType.Outro,
                    StartTicks = start,
                    EndTicks = end,
                },
                SegmentProviderId).ConfigureAwait(false);
            result.SegmentIds.Add(created.Id);
        }
    }

    private async Task ApplyOverviewAsync(Movie movie, StingerResult result, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var marker = config.EnableOverviewText ? OverviewMarker.BuildMarker(result, config.AddNoStingerOverview) : null;
        var updated = OverviewMarker.Apply(movie.Overview, marker);
        result.AppliedOverviewMarker = marker;

        if (updated is null)
        {
            return;
        }

        movie.Overview = updated;
        await movie.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
    }
}
