using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Stinger.Detection;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.ScheduledTasks;

public class StingerScanTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly StingerScanner _scanner;
    private readonly ILogger<StingerScanTask> _logger;

    public StingerScanTask(ILibraryManager libraryManager, StingerScanner scanner, ILogger<StingerScanTask> logger)
    {
        _libraryManager = libraryManager;
        _scanner = scanner;
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
            Type = TaskTriggerInfoType.IntervalTrigger,
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

            try
            {
                if (await _scanner.ScanMovieAsync(movie, force, cancellationToken).ConfigureAwait(false))
                {
                    analyzed++;
                }
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
}
