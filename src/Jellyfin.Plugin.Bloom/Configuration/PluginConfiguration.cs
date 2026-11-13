using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Bloom.Configuration;

/// <summary>
/// Jellyfin requires a configuration type, but Bloom keeps its state
/// (peers, invites, settings) in its own JSON store so that pairing requests
/// coming from friends can never be clobbered by a stale dashboard save.
/// See <see cref="Services.FederationStore"/>.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
}
