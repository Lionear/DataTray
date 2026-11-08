using System.Globalization;
using System.Text.RegularExpressions;

namespace DataTray.Extensions.QueryBuilder.Dialects;

public sealed partial record ServerVersion(int Major, int Minor, int Patch)
{
    public static ServerVersion? Parse(string? text)
    {
        var match = text is null ? null : VersionPattern().Match(text);
        if (match is null || !match.Success)
        {
            return null;
        }

        var patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
        return new ServerVersion(
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            patch);
    }

    public static bool AtLeast(ServerVersion? version, int major, int minor = 0, int patch = 0)
    {
        return version is not null && (version.Major, version.Minor, version.Patch).CompareTo((major, minor, patch)) >= 0;
    }

    [GeneratedRegex(@"(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();
}
