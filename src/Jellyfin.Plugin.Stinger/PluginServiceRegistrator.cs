using Jellyfin.Plugin.Stinger.Data;
using Jellyfin.Plugin.Stinger.Detection;
using Jellyfin.Plugin.Stinger.Playback;
using Jellyfin.Plugin.Stinger.Sources;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Stinger;

public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<StingerStore>();
        serviceCollection.AddSingleton<FfmpegFeatureExtractor>();
        serviceCollection.AddSingleton<TmdbKeywordSource>();
        serviceCollection.AddSingleton<WikipediaListSource>();
        serviceCollection.AddHostedService<StingerNotifier>();
    }
}
