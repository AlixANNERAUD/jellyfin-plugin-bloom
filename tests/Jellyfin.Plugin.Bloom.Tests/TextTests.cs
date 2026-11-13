using System.Text.RegularExpressions;
using Jellyfin.Plugin.Bloom.Services;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public partial class TextTests
{
    [Fact]
    public void EveryTextExistsInBothLanguagesWithTheSamePlaceholders()
    {
        foreach (var (key, (en, fr)) in Text.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), key);
            Assert.False(string.IsNullOrWhiteSpace(fr), key);
            Assert.Equal(Placeholders(en), Placeholders(fr));
        }
    }

    [Fact]
    public void TextsFollowTheRequestLanguage()
    {
        Text.UseRequestLanguage(null);
        Assert.Equal("Shared by Alice", Text.Get("FriendTag", "Alice"));

        Text.UseRequestLanguage("fr-FR");
        Assert.Equal("Fédération : Alice", Text.Get("FriendTag", "Alice"));

        Text.UseRequestLanguage("en-US");
        Assert.Equal("Invalid key.", Text.Get("InvalidKey"));
    }

    [Fact]
    public void ErrorsKeepTheirCode()
    {
        Text.UseRequestLanguage("fr");
        var error = new FederationException("UnknownInvite");
        Assert.Equal("UnknownInvite", error.Key);
        Assert.Equal("Invitation inconnue, déjà utilisée ou expirée.", error.Message);
    }

    private static string Placeholders(string text)
        => string.Join(",", PlaceholderRegex().Matches(text).Select(m => m.Value).Order());

    [GeneratedRegex(@"\{\d+\}")]
    private static partial Regex PlaceholderRegex();
}
