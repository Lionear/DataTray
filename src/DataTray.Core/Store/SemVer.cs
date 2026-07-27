namespace DataTray.Core.Store;

/// <summary>
/// Minimal SemVer ordering good enough for the store: compares the dotted numeric core
/// (<c>major.minor.patch…</c>) segment by segment, and treats a pre-release suffix
/// (<c>1.2.0-beta</c>) as lower than the same release (<c>1.2.0</c>), per SemVer §11. Build
/// metadata (<c>+…</c>) is ignored. Non-numeric cores fall back to an ordinal string compare so a
/// malformed version never throws — it just sorts deterministically.
/// </summary>
public static class SemVer
{
    /// <summary>Negative if <paramref name="a"/> &lt; <paramref name="b"/>, 0 if equal, positive if greater.</summary>
    public static int Compare(string? a, string? b)
    {
        if (a == b)
        {
            return 0;
        }

        if (string.IsNullOrEmpty(a))
        {
            return string.IsNullOrEmpty(b) ? 0 : -1;
        }

        if (string.IsNullOrEmpty(b))
        {
            return 1;
        }

        var (coreA, preA) = Split(a);
        var (coreB, preB) = Split(b);

        if (TryParseCore(coreA, out var numsA) && TryParseCore(coreB, out var numsB))
        {
            var max = Math.Max(numsA.Length, numsB.Length);
            for (var i = 0; i < max; i++)
            {
                var na = i < numsA.Length ? numsA[i] : 0;
                var nb = i < numsB.Length ? numsB[i] : 0;
                if (na != nb)
                {
                    return na.CompareTo(nb);
                }
            }

            // Equal cores: a pre-release is lower than the same release; otherwise ordinal on the tag.
            if (preA.Length == 0 && preB.Length == 0)
            {
                return 0;
            }

            if (preA.Length == 0)
            {
                return 1; // a is release, b is pre-release
            }

            if (preB.Length == 0)
            {
                return -1;
            }

            return string.CompareOrdinal(preA, preB);
        }

        return string.CompareOrdinal(a, b);
    }

    private static (string Core, string Pre) Split(string version)
    {
        var plus = version.IndexOf('+');
        if (plus >= 0)
        {
            version = version[..plus];
        }

        var dash = version.IndexOf('-');
        return dash >= 0 ? (version[..dash], version[(dash + 1)..]) : (version, string.Empty);
    }

    private static bool TryParseCore(string core, out int[] nums)
    {
        var parts = core.Split('.');
        nums = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out nums[i]))
            {
                nums = [];
                return false;
            }
        }

        return true;
    }
}
