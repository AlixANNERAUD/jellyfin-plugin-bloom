using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.Bloom.Models;

namespace Jellyfin.Plugin.Bloom.Services;

/// <summary>
/// Writes Kodi-style .nfo files that Jellyfin's NFO reader understands, so friends' metadata
/// (titles, plots, provider ids, cast...) shows up without any internet lookup.
/// </summary>
public static class NfoBuilder
{
    private static readonly XmlWriterSettings Settings = new()
    {
        Indent = true,
        Encoding = new UTF8Encoding(false),
        OmitXmlDeclaration = false
    };

    /// <summary>
    /// Builds a movie.nfo.
    /// </summary>
    /// <param name="item">Movie.</param>
    /// <param name="peerName">Friend name (added as a tag).</param>
    /// <returns>XML.</returns>
    public static string Movie(CatalogItem item, string peerName) => Build("movie", item, peerName, w => { });

    /// <summary>
    /// Builds a tvshow.nfo.
    /// </summary>
    /// <param name="item">Series.</param>
    /// <param name="peerName">Friend name.</param>
    /// <returns>XML.</returns>
    public static string Series(CatalogItem item, string peerName) => Build("tvshow", item, peerName, w =>
    {
        Element(w, "status", item.Status);
        Element(w, "enddate", item.EndDate);
    });

    /// <summary>
    /// Builds a season.nfo.
    /// </summary>
    /// <param name="item">Season.</param>
    /// <returns>XML.</returns>
    public static string Season(CatalogItem item) => Build("season", item, null, w =>
    {
        Element(w, "seasonnumber", item.IndexNumber?.ToString(CultureInfo.InvariantCulture));
    });

    /// <summary>
    /// Builds an episode .nfo.
    /// </summary>
    /// <param name="item">Episode.</param>
    /// <param name="seriesName">Series name.</param>
    /// <returns>XML.</returns>
    public static string Episode(CatalogItem item, string seriesName) => Build("episodedetails", item, null, w =>
    {
        Element(w, "showtitle", seriesName);
        Element(w, "season", item.ParentIndexNumber?.ToString(CultureInfo.InvariantCulture));
        Element(w, "episode", item.IndexNumber?.ToString(CultureInfo.InvariantCulture));
        Element(w, "episodenumberend", item.IndexNumberEnd?.ToString(CultureInfo.InvariantCulture));
    });

    private static string Build(string root, CatalogItem item, string? peerName, Action<XmlWriter> extra)
    {
        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, Settings))
        {
            w.WriteStartDocument(true);
            w.WriteStartElement(root);

            Element(w, "title", item.Name);
            Element(w, "originaltitle", item.OriginalTitle);
            Element(w, "sorttitle", item.SortName);
            Element(w, "year", item.Year?.ToString(CultureInfo.InvariantCulture));
            Element(w, "premiered", item.PremiereDate);
            Element(w, root == "episodedetails" ? "aired" : "releasedate", item.PremiereDate);
            Element(w, "plot", item.Overview);
            Element(w, "tagline", item.Tagline);
            Element(w, "mpaa", item.OfficialRating);
            Element(w, "rating", item.CommunityRating?.ToString("0.0", CultureInfo.InvariantCulture));
            Element(w, "criticrating", item.CriticRating?.ToString("0", CultureInfo.InvariantCulture));
            if (item.RunTimeTicks is > 0)
            {
                Element(w, "runtime", ((long)Math.Round(TimeSpan.FromTicks(item.RunTimeTicks.Value).TotalMinutes)).ToString(CultureInfo.InvariantCulture));
            }

            extra(w);

            foreach (var genre in item.Genres)
            {
                Element(w, "genre", genre);
            }

            foreach (var studio in item.Studios)
            {
                Element(w, "studio", studio);
            }

            foreach (var tag in item.Tags)
            {
                Element(w, "tag", tag);
            }

            if (!string.IsNullOrWhiteSpace(peerName))
            {
                Element(w, "tag", Text.Get("FriendTag", peerName));
            }

            var first = true;
            foreach (var (provider, id) in item.ProviderIds.OrderBy(p => ProviderOrder(p.Key)))
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                w.WriteStartElement("uniqueid");
                w.WriteAttributeString("type", provider.ToLowerInvariant());
                if (first)
                {
                    w.WriteAttributeString("default", "true");
                    first = false;
                }

                w.WriteString(Clean(id));
                w.WriteEndElement();
            }

            foreach (var person in item.People)
            {
                switch (person.Type)
                {
                    case "Director":
                        Element(w, "director", person.Name);
                        break;
                    case "Writer":
                        Element(w, "credits", person.Name);
                        break;
                    case "Actor":
                    case "GuestStar":
                        w.WriteStartElement("actor");
                        Element(w, "name", person.Name);
                        Element(w, "role", person.Role);
                        Element(w, "type", person.Type);
                        w.WriteEndElement();
                        break;
                }
            }

            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static int ProviderOrder(string key) => key.ToLowerInvariant() switch
    {
        "tmdb" => 0,
        "imdb" => 1,
        "tvdb" => 2,
        _ => 3
    };

    private static void Element(XmlWriter w, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        w.WriteElementString(name, Clean(value));
    }

    /// <summary>Removes characters that are invalid in XML 1.0.</summary>
    /// <summary>
    /// Drops the characters XML cannot hold (control characters, lone surrogates), keeping emoji and other
    /// characters outside the basic plane, which are written as surrogate pairs.
    /// </summary>
    internal static string Clean(string value)
    {
        var result = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (XmlConvert.IsXmlChar(c))
            {
                result.Append(c);
            }
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && XmlConvert.IsXmlSurrogatePair(value[i + 1], c))
            {
                result.Append(c).Append(value[i + 1]);
                i++;
            }
        }

        return result.ToString();
    }
}
