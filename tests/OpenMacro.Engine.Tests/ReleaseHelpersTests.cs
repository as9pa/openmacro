using OpenMacro.Engine;

namespace OpenMacro.Engine.Tests;

public class ReleaseHelpersTests
{
    [Theory]
    [InlineData("v0.3.0", "0.3.0.0")]
    [InlineData("0.3.0", "0.3.0.0")]
    [InlineData("V1.2", "1.2.0.0")]
    [InlineData("v0.3.0-beta.1", "0.3.0.0")]
    [InlineData("v2.0.1+build5", "2.0.1.0")]
    public void ParsesTags(string tag, string expected) =>
        Assert.Equal(Version.Parse(expected), ReleaseHelpers.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("v")]
    public void RejectsNonVersionTags(string? tag) => Assert.Null(ReleaseHelpers.ParseTag(tag));

    [Theory]
    [InlineData("0.3.0", "0.2.0.0", true)]
    [InlineData("0.2.0", "0.2.0.0", false)] // unset parts must not rank below 0
    [InlineData("0.2.0", "0.10.0", false)]
    [InlineData("0.10.0", "0.9.9", true)]
    [InlineData("0.2.0.1", "0.2.0", true)]
    public void ComparesVersions(string latest, string current, bool newer) =>
        Assert.Equal(newer, ReleaseHelpers.IsNewer(Version.Parse(latest), Version.Parse(current)));

    [Fact]
    public void FormatsThreePartsUnlessTheFourthIsSet()
    {
        Assert.Equal("0.2.0", ReleaseHelpers.Format(new Version(0, 2, 0, 0)));
        Assert.Equal("0.2.0", ReleaseHelpers.Format(new Version(0, 2)));
        Assert.Equal("0.2.0.3", ReleaseHelpers.Format(new Version(0, 2, 0, 3)));
    }

    [Fact]
    public void ParsesSha256Sums()
    {
        var exe = new string('A', 64);
        var zip = new string('b', 64);
        var text =
            $"{exe}  openmacro-v0.3.0-win-x64.exe\r\n"
            + $"{zip} *openmacro-v0.3.0-win-x64-dotnet.zip\n"
            + "\n"
            + "not a hash line\n"
            + $"{new string('z', 64)}  bad-hex.bin\n";

        var sums = ReleaseHelpers.ParseSha256Sums(text);

        Assert.Equal(2, sums.Count);
        Assert.Equal(new string('a', 64), sums["openmacro-v0.3.0-win-x64.exe"]);
        Assert.Equal(zip, sums["OPENMACRO-v0.3.0-win-x64-dotnet.zip"]);
    }
}
