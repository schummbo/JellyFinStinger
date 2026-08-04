using System.Threading.Channels;
using Jellyfin.Plugin.Stinger.Detection;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.Library;

/// <summary>
/// Scans a movie shortly after it's added to a library, so a new stinger doesn't wait for the
/// next daily sweep. Additions funnel through a single-consumer queue with a short delay, since
/// ffmpeg analysis is CPU/IO heavy and a freshly-added item may still be missing provider ids
/// (e.g. TMDB) until metadata providers finish running.
/// </summary>
public sealed class StingerLibraryWatcher : IHostedService
{
    private static readonly TimeSpan ScanDelay = TimeSpan.FromMinutes(3);

    private readonly ILibraryManager _libraryManager;
    private readonly StingerScanner _scanner;
    private readonly ILogger<StingerLibraryWatcher> _logger;
    private readonly Channel<Guid> _queue = Channel.CreateUnbounded<Guid>();
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public StingerLibraryWatcher(ILibraryManager libraryManager, StingerScanner scanner, ILogger<StingerLibraryWatcher> logger)
    {
        _libraryManager = libraryManager;
        _scanner = scanner;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _worker = Task.Run(() => ProcessQueueAsync(_cts.Token));
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        _queue.Writer.TryComplete();
        _cts?.Cancel();

        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (Plugin.Instance?.Configuration.EnableAutoScanOnAdd == true
            && e.Item is Movie { IsVirtualItem: false } movie)
        {
            _queue.Writer.TryWrite(movie.Id);
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        await foreach (var itemId in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await Task.Delay(ScanDelay, cancellationToken).ConfigureAwait(false);

                if (_libraryManager.GetItemById(itemId) is not Movie movie)
                {
                    continue;
                }

                await _scanner.ScanMovieAsync(movie, force: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Auto-scan on add failed for item {ItemId}", itemId);
            }
        }
    }
}
