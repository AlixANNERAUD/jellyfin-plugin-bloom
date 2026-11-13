using System.Net;
using System.Text;
using Jellyfin.Plugin.Bloom.Services;
using Xunit;

namespace Jellyfin.Plugin.Bloom.Tests;

public class RobustnessTests
{
    [Theory]
    [InlineData("{\"code\":404,\"message\":\"Not Found\"}")]
    [InlineData("{\"error\":{\"message\":\"nope\"}}")]
    [InlineData("{\"code\":[1],\"error\":true}")]
    [InlineData("[\"not an object\"]")]
    [InlineData("<html>Bad gateway</html>")]
    public void ErrorsFromOtherServersNeverThrow(string body)
    {
        Text.UseRequestLanguage("en");
        Assert.Equal("HTTP 404", PeerClient.DescribeError(HttpStatusCode.NotFound, Encoding.UTF8.GetBytes(body)));
    }

    [Fact]
    public void AFriendCanOnlyPickTextsThatNeedNoValues()
    {
        Text.UseRequestLanguage("en");

        // A known code is shown in our language.
        Assert.Equal("HTTP 403: Invalid key.", PeerClient.DescribeError(HttpStatusCode.Forbidden, Encoding.UTF8.GetBytes("{\"code\":\"InvalidKey\",\"error\":\"Clé invalide.\"}")));

        // A text with placeholders would need values the friend does not send: its own message is shown instead.
        Assert.Equal(
            "HTTP 403: boom",
            PeerClient.DescribeError(HttpStatusCode.Forbidden, Encoding.UTF8.GetBytes("{\"code\":\"HttpErrorWithMessage\",\"error\":\"boom\"}")));
    }

    [Fact]
    public void NfoTextKeepsEmojiButDropsWhatXmlCannotHold()
    {
        Assert.Equal("Coco 🎸 2017", NfoBuilder.Clean("Coco 🎸 2017"));
        Assert.Equal("ab", NfoBuilder.Clean("a\u0001b"));
        Assert.Equal("ab", NfoBuilder.Clean("a\ud83cb"));
        Assert.Equal("a", NfoBuilder.Clean("a\ud83c"));
        Assert.Equal("ab", NfoBuilder.Clean("a\udfb8b"));
    }
}
