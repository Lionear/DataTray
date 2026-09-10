namespace DataTray.Core.Update;

/// <summary>
/// The release channel the user follows. Each maps to one rolling GitHub release tag that carries an
/// <c>update.json</c> manifest (see <see cref="AppUpdateService"/>). Ordered least-to-most bleeding-edge;
/// the default is <see cref="Stable"/>.
/// </summary>
public enum UpdateChannel
{
    /// <summary>Tagged <c>v*</c> releases — the <c>latest</c>, non-prerelease build.</summary>
    Stable,

    /// <summary>Every merge to <c>main</c>; rolling prerelease tag <c>preview</c>.</summary>
    Preview,

    /// <summary>Nightly build of <c>develop</c>; rolling prerelease tag <c>nightly</c>.</summary>
    Nightly
}

/// <summary>Maps a channel to the rolling release tag whose <c>update.json</c> is the source of truth.</summary>
public static class UpdateChannelExtensions
{
    public static string Tag(this UpdateChannel channel) => channel switch
    {
        UpdateChannel.Stable => "stable",
        UpdateChannel.Preview => "preview",
        UpdateChannel.Nightly => "nightly",
        _ => "stable"
    };

    /// <summary>
    /// The stream half of a Velopack channel name — deliberately the same word <see cref="Tag"/>
    /// returns, because build.yml stamps it into the version and names the release tag with it. Three
    /// spellings of one concept is how a build ends up publishing to a channel nobody asks for.
    /// </summary>
    public static string Stream(this UpdateChannel channel) => channel.Tag();

    /// <summary>
    /// The Velopack channel this build follows: <c>{rid}-{stream}</c>, e.g. <c>win-arm64-nightly</c>.
    /// Keyed on the RID and not on the platform, because a Velopack feed carries the packages
    /// themselves — a single shared <c>win</c> feed would offer an x64 package to an arm64 install.
    /// </summary>
    public static string VelopackChannel(this UpdateChannel channel, string rid) =>
        $"{rid}-{channel.Stream()}";

    /// <summary>
    /// Where a channel's release assets and its <c>releases.{channel}.json</c> live. Stable's sit on
    /// the human-pushed v-tag, which is also what GitHub serves as "latest release"; the two rolling
    /// streams keep their fixed tag, which build.yml republishes on every run.
    /// <para>
    /// Always a plain <c>github.com</c> download URL, never <c>api.github.com</c>: the API is rate
    /// limited to 60 requests an hour, which an update check shares with everything else on the
    /// caller's address.
    /// </para>
    /// </summary>
    public static string FeedBaseUrl(this UpdateChannel channel, string repositoryUrl)
    {
        var repo = repositoryUrl.TrimEnd('/');
        return channel == UpdateChannel.Stable
            ? $"{repo}/releases/latest/download"
            : $"{repo}/releases/download/{channel.Stream()}";
    }
}
