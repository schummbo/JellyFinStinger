using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Stinger.Data;
using Jellyfin.Plugin.Stinger.Detection;
using Jellyfin.Plugin.Stinger.Model;
using Jellyfin.Plugin.Stinger.Sources;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.ScheduledTasks;

public class StingerScanTask : IScheduledTask
{
    private const string SegmentProviderId = "jellyfin.plugin.stinger";

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSegmentManager _segmentManager;
    private readonly FfmpegFeatureExtractor _extractor;
    private readonly TmdbKeywordSource _tmdbSource;
    private readonly WikipediaListSource _wikipediaSource;
    private readonly StingerStore _store;
    private readonly ILogger<StingerScanTask> _logger;

    public StingerScanTask(
        ILibraryManager libraryManager,
        IMediaSegmentManager segmentManager,
        FfmpegFeatureExtractor extractor,
        TmdbKeywordSource tmdbSource,
        WikipediaListSource wikipediaSource,
        StingerStore store,
        ILogger<StingerScanTask> logger)
    {
        _libraryManager = libraryManager;
        _segmentManager = segmentManager;
        _extractor = extractor;
        _tmdbSource = tmdbSource;
        _wikipediaSource = wikipediaSource;
        _store = store;
        _logger = logger;
    }

    public string Name => "Scan movies for stingers";

    public string Key => "StingerScan";

    public string Description => "Analyzes the credits of each movie for mid/post-credits scenes and labels them.";

    public string Category => "Stinger";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfo.TriggerInterval,
            IntervalTicks = TimeSpan.FromHours(24).Ticks,
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var force = config.ForceRescan;
        if (force)
        {
            config.ForceRescan = false;
            Plugin.Instance.SaveConfiguration();
        }

        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            IsVirtualItem = false,
            Recursive = true,
        }).OfType<Movie>().ToList();

        _logger.LogInformation("Stinger scan starting: {Count} movies", movies.Count);
        var processed = 0;
        var analyzed = 0;

        foreach (var movie in movies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress.Report(100.0 * processed++ / Math.Max(1, movies.Count));

            if (string.IsNullOrEmpty(movie.Path) || movie.RunTimeTicks is null or < TimeSpan.TicksPerMinute * 20)
            {
                continue;
            }

            var existing = _store.Get(movie.Id);
            if (!force && existing is not null && existing.ItemDateModifiedUtc == movie.DateModified)
            {
                continue;
            }

            try
            {
                await ScanMovieAsync(movie, existing, cancellationToken).ConfigureAwait(false);
                analyzed++;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Stinger scan failed for {Movie}", movie.Name);
            }
        }

        progress.Report(100);
        _logger.LogInformation("Stinger scan finished: {Analyzed} of {Count} movies analyzed", analyzed, movies.Count);
    }

    private async Task ScanMovieAsync(Movie movie, StingerResult? existing, CancellationToken cancellationToken)
    {
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
