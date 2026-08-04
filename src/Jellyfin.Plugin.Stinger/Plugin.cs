using Jellyfin.Plugin.Stinger.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Stinger;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "a8d7f2c4-1b3e-4c5a-9e6f-2d8b7a4c9e01";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override string Name => "Stinger";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description =>
        "Detects mid- and post-credits scenes (stingers) by analyzing the credits region of each movie, "
        + "cross-checked against TMDB keywords and Wikipedia.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
            }
        };
    }
}
