using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
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
        serviceCollection.AddHttpClient(PeerClient.HttpClientName, client =>
            {
                client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Jellyfin-Bloom", typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0"));
            })
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // Friends' servers never redirect Bloom requests: following one could reach any address.
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.All
            });
        serviceCollection.AddSingleton<CatalogService>();
        serviceCollection.AddSingleton<LibraryIntegration>();
        serviceCollection.AddSingleton<SyncService>();
        serviceCollection.AddSingleton<PairingService>();
        serviceCollection.AddSingleton<VersionLinker>();
        serviceCollection.AddSingleton<PeerWaker>();
        serviceCollection.AddHostedService<FederationNotifier>();

        serviceCollection.Configure<MvcOptions>(options =>
        {
            // "Download" of a friend's movie returns the video (relayed), not our .strm file.
            options.Filters.Add<FederatedDownloadFilter>();

            // Playing a friend's movie first wakes its server up if it sleeps when idle.
            options.Filters.Add<WakeFriendFilter>();
        });
    }
}
