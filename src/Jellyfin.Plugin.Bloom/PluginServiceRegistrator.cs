using Jellyfin.Plugin.Bloom.Api;
using Jellyfin.Plugin.Bloom.Crypto;
using Jellyfin.Plugin.Bloom.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Bloom;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton(sp => new FederationPaths(sp.GetRequiredService<IApplicationPaths>()));
        serviceCollection.AddSingleton<FederationStore>();
        serviceCollection.AddSingleton(sp => ServerIdentity.LoadOrCreate(sp.GetRequiredService<FederationPaths>().IdentityFile));
        serviceCollection.AddSingleton<NonceCache>();
        serviceCollection.AddSingleton<PeerClient>();
        serviceCollection.AddSingleton<CatalogService>();
        serviceCollection.AddSingleton<LibraryIntegration>();
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddSingleton<PairingService>();
        serviceCollection.AddSingleton<VersionLinker>();
        serviceCollection.AddHostedService<FederationNotifier>();

        // "Download" of a friend's movie returns the video (relayed), not our .strm file.
        serviceCollection.Configure<MvcOptions>(options => options.Filters.Add<FederatedDownloadFilter>());
    }
}
