using DataTray.Core.Update;

namespace DataTray.Core.Tests.Update;

/// <summary>
/// The channel name and the feed URL are the two values that fail <em>silently</em> when they are
/// wrong: a feed that does not exist reads as "you are up to date", with no error anywhere and
/// nothing in the UI to notice. The service that uses them is barely unit-testable (it needs a real
/// managed install), but these two are pure, so they get real tests rather than none.
/// </summary>
public class VelopackChannelTests
{
    // Per RID rather than per platform: a feed carries the packages themselves, so one shared "win"
    // feed would hand an x64 package to an arm64 install.
    [Theory]
    [InlineData(UpdateChannel.Stable, "win-x64", "win-x64-stable")]
    [InlineData(UpdateChannel.Nightly, "win-arm64", "win-arm64-nightly")]
    [InlineData(UpdateChannel.Preview, "linux-x64", "linux-x64-preview")]
    [InlineData(UpdateChannel.Stable, "osx-arm64", "osx-arm64-stable")]
    public void Channel_is_rid_and_stream(UpdateChannel channel, string rid, string expected) =>
        Assert.Equal(expected, channel.VelopackChannel(rid));

    // Stable's assets sit on the human-pushed v-tag, which is also what GitHub serves as "latest
    // release". Never api.github.com: that one is rate limited to 60 requests an hour.
    [Fact]
    public void Stable_reads_the_latest_release() =>
        Assert.Equal(
            "https://example.test/repo/releases/latest/download",
            UpdateChannel.Stable.FeedBaseUrl("https://example.test/repo"));

    [Theory]
    [InlineData(UpdateChannel.Nightly, "nightly")]
    [InlineData(UpdateChannel.Preview, "preview")]
    public void Rolling_streams_read_their_fixed_tag(UpdateChannel channel, string tag) =>
        Assert.Equal(
            $"https://example.test/repo/releases/download/{tag}",
            channel.FeedBaseUrl("https://example.test/repo"));

    // A trailing slash on the repository URL must not produce a double slash in the feed URL — that
    // is a 404, and a 404 on a feed is indistinguishable from "nothing newer".
    [Fact]
    public void Repository_url_trailing_slash_is_trimmed() =>
        Assert.Equal(
            "https://example.test/repo/releases/download/nightly",
            UpdateChannel.Nightly.FeedBaseUrl("https://example.test/repo/"));

    // The stream word is the same one the rolling release tags use, and build.yml stamps it into the
    // version. If these ever drift apart, the app asks for a channel no build ever published.
    [Theory]
    [InlineData(UpdateChannel.Stable, "stable")]
    [InlineData(UpdateChannel.Preview, "preview")]
    [InlineData(UpdateChannel.Nightly, "nightly")]
    public void Stream_matches_the_release_tag(UpdateChannel channel, string expected)
    {
        Assert.Equal(expected, channel.Stream());
        Assert.Equal(expected, channel.Tag());
    }
}
