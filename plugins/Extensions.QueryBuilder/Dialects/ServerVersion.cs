using System.Globalization;
using System.Text.RegularExpressions;

namespace DataTray.Extensions.QueryBuilder.Dialects;

public sealed partial record ServerVersion(int Major, int Minor, int Patch)
{
    public static ServerVersion? Parse(string? text)
    {
        var match = text is null ? null : VersionPattern().Match(text);
        if (match is null || !match.Success
            || !TryPart(match.Groups[1], out var major) || !TryPart(match.Groups[2], out var minor) || !TryPart(match.Groups[3], out var patch))
        {
            return null;
        }

        return new ServerVersion(major, minor, patch);
    }

    public static bool AtLeast(ServerVersion? version, int major, int minor = 0, int patch = 0)
    {
        return version is not null && (version.Major, version.Minor, version.Patch).CompareTo((major, minor, patch)) >= 0;
    }

    // A missing part is 0; a part too large for an int makes the whole version unknown.
    private static bool TryPart(Group group, out int value)
    {
        value = 0;
        return !group.Success || int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    [GeneratedRegex(@"(\d+)(?:\.(\d+))?(?:\.(\d+))?")]
    private static partial Regex VersionPattern();
}
