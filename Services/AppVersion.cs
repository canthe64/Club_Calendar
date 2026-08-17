using System.Globalization;
using System.Reflection;

namespace FacilityScheduler.Services;

/// <summary>
/// The running build's identity, shown on the Settings page so "what's actually deployed right now?"
/// is answerable without portal access or a redeploy. Read once from the assembly's
/// InformationalVersion, which FacilityScheduler.csproj composes as "1.0.0+build.2608171432" - see
/// that file's version block for where each half comes from and how to bump it.
/// </summary>
public static class AppVersion
{
    private static readonly VersionInfo Current = Parse(
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The hand-set part, e.g. "1.0.0".</summary>
    public static string Version => Current.Version;

    /// <summary>The build stamp, e.g. "2608171432". "unknown" if the attribute was missing or unparsable.</summary>
    public static string Build => Current.Build;

    /// <summary>When this build was compiled, decoded from <see cref="Build"/>. Null if the build
    /// number didn't come from this project's timestamp scheme (e.g. a CI override).</summary>
    public static DateTime? BuiltUtc => Current.BuiltUtc;

    /// <summary>"Version 1.0.0    Build 2608171432" - the Settings page's primary line.</summary>
    public static string Display => $"Version {Version}    Build {Build}";

    internal readonly record struct VersionInfo(string Version, string Build, DateTime? BuiltUtc);

    /// <summary>Split out from the static initializer so the parse can be tested directly. Every
    /// failure path degrades to a displayable string rather than throwing - a version banner must
    /// never be able to take the Settings page down, and an assembly built without the csproj's
    /// version block (or by a build system with its own numbering) is a normal thing to encounter,
    /// not an error.</summary>
    internal static VersionInfo Parse(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new VersionInfo("unknown", "unknown", null);
        }

        const string marker = "+build.";
        var markerIndex = informationalVersion.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            // A plain version with no build metadata - report it rather than claiming "unknown",
            // since the version half is still real and useful.
            return new VersionInfo(informationalVersion, "unknown", null);
        }

        var version = informationalVersion[..markerIndex];
        var build = informationalVersion[(markerIndex + marker.Length)..];

        return new VersionInfo(
            string.IsNullOrWhiteSpace(version) ? "unknown" : version,
            string.IsNullOrWhiteSpace(build) ? "unknown" : build,
            DecodeBuildTimestamp(build));
    }

    private static DateTime? DecodeBuildTimestamp(string build) =>
        DateTime.TryParseExact(build, "yyMMddHHmm", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : null;
}
