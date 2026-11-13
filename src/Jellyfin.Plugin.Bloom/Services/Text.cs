using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using MediaBrowser.Controller.Configuration;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// The texts Bloom shows to people, in English and French.
/// Messages answering the settings page follow the page's language (it sends <see cref="LanguageHeader"/>);
/// everything else (scheduled task, tags, background sync errors) follows the server's display language, like
/// Jellyfin's own texts.
/// </summary>
public static class Text
{
    /// <summary>Header the settings page sends with its language.</summary>
    public const string LanguageHeader = "X-Bloom-Language";

    private static readonly AsyncLocal<string?> RequestLanguage = new();

    private static Func<string?> _serverLanguage = static () => null;

    /// <summary>Gets every text: key → (English, French). Placeholders are {0}, {1}…</summary>
    internal static IReadOnlyDictionary<string, (string En, string Fr)> All { get; } = new Dictionary<string, (string En, string Fr)>(StringComparer.Ordinal)
    {
        // Pairing.
        ["NotAnInvite"] = ("This is not a Bloom invite code.", "Ce n'est pas un code d'invitation Bloom."),
        ["CorruptInvite"] = ("This invite code is corrupted.", "Code d'invitation corrompu."),
        ["IncompatibleInvite"] = ("This invite code is corrupted or comes from an incompatible version.", "Code d'invitation corrompu ou d'une version incompatible."),
        ["InvalidInviteKey"] = ("The key in this invite is invalid.", "La clé contenue dans l'invitation est invalide."),
        ["InvalidInviteUrl"] = ("The URL in this invite is invalid.", "L'URL contenue dans l'invitation est invalide."),
        ["InviteExpired"] = ("This invite has expired: ask for a new one.", "Cette invitation a expiré, demandez-en une nouvelle."),
        ["SetPublicUrlFirst"] = ("First enter your server's public URL (the one your friends can reach).", "Renseignez d'abord l'URL publique de votre serveur (celle que vos amis peuvent joindre)."),
        ["SetPublicUrlForFriend"] = ("First enter your server's public URL: your friend needs it to play your movies.", "Renseignez d'abord l'URL publique de votre serveur : votre ami en a besoin pour lire vos films."),
        ["OwnInvite"] = ("This is your own invite: send it to your friend.", "C'est votre propre invitation : envoyez-la à votre ami."),
        ["AlreadyFriends"] = ("This server is already one of your friends.", "Ce serveur fait déjà partie de vos amis."),
        ["IdentityMismatch"] = ("The server answered with another identity than the one in the invite.", "Le serveur a répondu avec une autre identité que celle de l'invitation."),
        ["IncompatibleProtocol"] = ("Incompatible protocol version.", "Version de protocole incompatible."),
        ["InvalidKey"] = ("Invalid key.", "Clé invalide."),
        ["SelfFriend"] = ("A server can't be friends with itself.", "Un serveur ne peut pas être ami avec lui-même."),
        ["InvalidUrl"] = ("Invalid URL.", "URL invalide."),
        ["UnknownInvite"] = ("Unknown invite, already used or expired.", "Invitation inconnue, déjà utilisée ou expirée."),

        // Requests between servers (the code travels with the error, so each side reads it in its own language).
        ["InvalidLibrary"] = ("Invalid library.", "Bibliothèque invalide."),
        ["LibraryNotShared"] = ("This library isn't shared with you.", "Cette bibliothèque n'est pas partagée avec vous."),
        ["UnsignedRequest"] = ("Unsigned request.", "Requête non signée."),
        ["ClockSkew"] = ("The two servers' clocks are out of sync.", "Horloges désynchronisées entre les deux serveurs."),
        ["RequestTooLarge"] = ("Request too large.", "Requête trop grosse."),
        ["UnknownServer"] = ("Unknown server: you are not (or no longer) one of this server's friends.", "Serveur inconnu : vous ne faites pas (ou plus) partie des amis de ce serveur."),
        ["InvalidSignature"] = ("Invalid signature.", "Signature invalide."),
        ["ReplayedRequest"] = ("Replayed request.", "Requête rejouée."),
        ["Timeout"] = ("Timed out contacting {0}", "Délai dépassé en contactant {0}"),
        ["Unreachable"] = ("Server unreachable: {0}", "Serveur injoignable : {0}"),
        ["UnsignedResponse"] = ("The answer isn't signed with this server's key (wrong URL, a proxy altering answers, or impersonation).", "La réponse n'est pas signée par la clé de ce serveur (mauvaise URL, proxy qui altère les réponses, ou usurpation)."),
        ["EmptyResponse"] = ("Empty answer.", "Réponse vide."),
        ["UnreadableResponse"] = ("Unreadable answer.", "Réponse illisible."),
        ["HttpError"] = ("HTTP {0}", "HTTP {0}"),
        ["HttpErrorWithMessage"] = ("HTTP {0}: {1}", "HTTP {0} : {1}"),

        // Settings page.
        ["InvalidPublicUrl"] = ("Invalid public URL (for example https://jellyfin.example.com).", "URL publique invalide (ex. : https://jellyfin.exemple.fr)."),
        ["MediaRootNotAbsolute"] = ("The folder for friends' media must be an absolute path.", "Le dossier des médias fédérés doit être un chemin absolu."),
        ["FriendRefused"] = ("Your friend's server refused or didn't answer: {0}", "Le serveur de votre ami a refusé ou n'a pas répondu : {0}"),
        ["WrongLibraryType"] = ("\"{0}\" isn't a {1} library.", "« {0} » n'est pas une bibliothèque de {1}."),
        ["MoviesType"] = ("movie", "films"),
        ["ShowsType"] = ("show", "séries"),
        ["UnknownFriend"] = ("Unknown friend.", "Ami inconnu."),
        ["LibraryMissing"] = ("The library \"{0}\" doesn't exist.", "La bibliothèque « {0} » n'existe pas."),

        // Sync.
        ["SyncInterrupted"] = ("Sync interrupted (it took too long, or the friend was removed meanwhile).", "Synchronisation interrompue (trop longue, ou ami retiré entre-temps)."),
        ["InvalidCatalog"] = ("Invalid catalog received.", "Catalogue invalide reçu."),
        ["CatalogChanging"] = ("Your friend's catalog is changing right now (scan in progress?); it will be retried at the next sync.", "Le catalogue de votre ami change en ce moment (analyse en cours ?) ; nouvel essai à la prochaine synchronisation."),

        // Jellyfin.
        ["PluginDescription"] = ("Share your libraries with your friends' Jellyfin servers: their movies and shows appear in your libraries and play from their server.", "Fédération entre serveurs Jellyfin amis : partagez vos bibliothèques, les films de vos amis apparaissent chez vous et sont lus depuis leur serveur."),
        ["TaskName"] = ("Sync friends' libraries", "Synchroniser les bibliothèques des amis"),
        ["TaskDescription"] = ("Fetches the catalogs your friends share with you (Bloom) and updates your libraries.", "Récupère les catalogues partagés par vos amis (Bloom) et met à jour vos bibliothèques."),
        ["FriendTag"] = ("Shared by {0}", "Fédération : {0}"),
    };

    /// <summary>
    /// Gets a value indicating whether texts are currently written in French.
    /// </summary>
    public static bool IsFrench => (RequestLanguage.Value ?? _serverLanguage() ?? string.Empty).StartsWith("fr", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Follows the server's display language (Dashboard → General) when no request asks for another one.
    /// </summary>
    /// <param name="configuration">Server configuration.</param>
    public static void UseServerLanguage(IServerConfigurationManager configuration)
        => _serverLanguage = () => configuration.Configuration.UICulture;

    /// <summary>
    /// Writes the texts of the current request (and the work it awaits) in the given language.
    /// </summary>
    /// <param name="language">Language tag ("fr", "en-US"…), or null for the server's.</param>
    public static void UseRequestLanguage(string? language)
        => RequestLanguage.Value = string.IsNullOrWhiteSpace(language) ? null : language;

    /// <summary>
    /// Whether a text exists.
    /// </summary>
    /// <param name="key">Key.</param>
    /// <returns>True if known.</returns>
    public static bool Has(string? key) => key is not null && All.ContainsKey(key);

    /// <summary>
    /// Gets a text in the current language.
    /// </summary>
    /// <param name="key">Key.</param>
    /// <param name="args">Values for the placeholders.</param>
    /// <returns>The text.</returns>
    public static string Get(string key, params object?[] args)
    {
        var (en, fr) = All[key];
        return string.Format(CultureInfo.InvariantCulture, IsFrench ? fr : en, args);
    }
}
