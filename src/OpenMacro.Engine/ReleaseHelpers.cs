namespace OpenMacro.Engine;

/// <summary>
/// The pure parts of the app's update check: reading a release tag as a
/// version, comparing it with the running one, and reading a SHA256SUMS
/// file. Kept here, away from HttpClient and WPF, so tests can reach them.
/// </summary>
public static class ReleaseHelpers
{
    /// <summary>Reads a release tag ("v0.3.0", "0.3.0", "v0.3.0-beta") as a
    /// version, or null if it isn't one. Anything after a '-' or '+' is
    /// dropped.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;

        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V'))
            text = text[1..];

        var cut = text.IndexOfAny(['-', '+']);
        if (cut >= 0)
            text = text[..cut];

        return Version.TryParse(text, out var version) ? Normalize(version) : null;
    }

    /// <summary>Fills unset parts with 0, so "0.2.0" and the assembly's
    /// "0.2.0.0" compare equal (System.Version ranks an unset part below 0).</summary>
    public static Version Normalize(Version version) =>
        new(
            version.Major,
            version.Minor,
            Math.Max(version.Build, 0),
            Math.Max(version.Revision, 0)
        );

    public static bool IsNewer(Version latest, Version current) =>
        Normalize(latest) > Normalize(current);

    /// <summary>"0.3.0": three parts, plus the fourth only when it's set.</summary>
    public static string Format(Version version)
    {
        var v = Normalize(version);
        return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
    }

    /// <summary>Reads a SHA256SUMS file ("&lt;hex&gt;  &lt;filename&gt;" per
    /// line, the sha256sum format; a '*' before the name marks binary mode)
    /// into filename → lowercase hex. Lines that don't fit are skipped.</summary>
    public static IReadOnlyDictionary<string, string> ParseSha256Sums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            var space = line.IndexOf(' ');
            if (space != 64)
                continue;

            var hash = line[..space];
            if (!hash.All(Uri.IsHexDigit))
                continue;

            var name = line[space..].TrimStart();
            if (name.StartsWith('*'))
                name = name[1..];
            if (name.Length > 0)
                sums[name] = hash.ToLowerInvariant();
        }
        return sums;
    }
}
