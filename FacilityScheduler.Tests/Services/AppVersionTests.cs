using FacilityScheduler.Services;

namespace FacilityScheduler.Tests.Services;

public class AppVersionTests
{
    [Fact]
    public void Parse_SplitsTheVersionAndBuildTheCsprojComposes()
    {
        var info = AppVersion.Parse("1.0.0+build.2608171432");

        Assert.Equal("1.0.0", info.Version);
        Assert.Equal("2608171432", info.Build);
    }

    [Fact]
    public void Parse_DecodesTheBuildNumberBackToItsTimestamp()
    {
        var info = AppVersion.Parse("1.0.0+build.2608171432");

        Assert.Equal(new DateTime(2026, 8, 17, 14, 32, 0, DateTimeKind.Utc), info.BuiltUtc);
    }

    [Fact]
    public void Parse_NonTimestampBuildNumber_KeepsTheBuildButHasNoDate()
    {
        // A build system overriding -p:BuildNumber with its own scheme (a CI run number, say) is
        // supported - the number still displays, there's just no date to decode out of it.
        var info = AppVersion.Parse("1.0.0+build.ci-4471");

        Assert.Equal("ci-4471", info.Build);
        Assert.Null(info.BuiltUtc);
    }

    [Fact]
    public void Parse_PlainVersionWithNoBuildMetadata_StillReportsTheVersion()
    {
        var info = AppVersion.Parse("2.1.3");

        Assert.Equal("2.1.3", info.Version);
        Assert.Equal("unknown", info.Build);
        Assert.Null(info.BuiltUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_MissingAttribute_DegradesInsteadOfThrowing(string? informationalVersion)
    {
        // This string renders on the Settings page. A malformed or absent attribute must never be
        // able to throw there.
        var info = AppVersion.Parse(informationalVersion);

        Assert.Equal("unknown", info.Version);
        Assert.Equal("unknown", info.Build);
        Assert.Null(info.BuiltUtc);
    }

    [Fact]
    public void Display_ReadsTheRealAssemblyAttribute_NotAPlaceholder()
    {
        // Guards the csproj wiring itself, not the parser: if the version block were removed or the
        // SDK started appending a git sha again (IncludeSourceRevisionInInformationalVersion), this
        // is what would notice.
        Assert.NotEqual("unknown", AppVersion.Version);
        Assert.NotEqual("unknown", AppVersion.Build);
        Assert.StartsWith("Version ", AppVersion.Display);
    }
}
