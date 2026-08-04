using System.Collections.Concurrent;
using Jellyfin.Plugin.Stinger.Data;
using Jellyfin.Plugin.Stinger.Model;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Stinger.Playback;

/// <summary>
/// Sends a one-shot "stay tuned" message to the playing client when playback
/// reaches the credits of a movie known to have a stinger.
/// </summary>
public sealed class StingerNotifier : IHostedService
{
    private static readonly TimeSpan TriggerWindow = TimeSpan.FromSeconds(60);

    private readonly ISessionManager _sessionManager;
    private readonly StingerStore _store;
    private readonly ILogger<StingerNotifier> _logger;
    private readonly ConcurrentDictionary<string, byte> _notified = new();

    public StingerNotifier(ISessionManager sessionManager, StingerStore store, ILogger<StingerNotifier> logger)
    {
        _sessionManager = sessionManager;
        _store = store;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress += OnPlaybackProgress;
        _sessionManager.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessionManager.PlaybackProgress -= OnPlaybackProgress;
        _sessionManager.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private async void OnPlaybackProgress(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var config = Plugin.Instance?.Configuration;
            if (config?.EnableNotification != true
                || e.Item is not Movie movie
                || e.PlaybackPositionTicks is not { } position
                || e.Session is null)
            {
                return;
            }

            var result = _store.Get(movie.Id);
            if (result is null || result.State != StingerState.HasStinger)
            {
                return;
            }

            // Trigger at the detected credits start; fall back to the last 8 minutes
            // when presence came from external sources without timestamps.
            var trigger = result.CreditsStartTicks
                ?? Math.Max(0, (movie.RunTimeTicks ?? 0) - TimeSpan.FromMinutes(8).Ticks);

            if (position < trigger || position > trigger + TriggerWindow.Ticks)
            {
                return;
            }

            var key = $"{e.Session.Id}:{movie.Id}";
            if (!_notified.TryAdd(key, 0))
            {
                return;
            }

            await _sessionManager.SendMessageCommand(
                e.Session.Id,
                e.Session.Id,
                new MessageCommand
                {
                    Header = "Stinger",
                    Text = config.NotificationText,
                    TimeoutMs = 10000,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stinger notification failed");
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e.Session is not null)
        {
            foreach (var key in _notified.Keys.Where(k => k.StartsWith($"{e.Session.Id}:", StringComparison.Ordinal)))
            {
                _notified.TryRemove(key, out _);
            }
        }
    }
}
