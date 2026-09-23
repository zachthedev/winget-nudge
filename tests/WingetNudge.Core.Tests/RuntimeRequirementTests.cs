using AwesomeAssertions;
using WingetNudge.CustomActions;

namespace WingetNudge.Core.Tests;

/// <summary>
/// The verdict setup's custom action reaches over the Windows App Runtime packages registered for the
/// installing user. A version is what setup records as found; null is a refusal.
/// </summary>
public sealed class RuntimeRequirementTests
{
    private const string Package = "Microsoft.WindowsAppRuntime.2";
    private const string Architecture = "x64";
    private static readonly Version Floor = new(2, 4, 0, 0);

    [Fact]
    public void FamilyName_IsTheFamilyTheBootstrapperResolves()
    {
        // WindowsAppSDK-VersionInfo.cs in Microsoft.WindowsAppSDK.Runtime 2.4.0 names this family.
        RuntimeRequirement.FamilyName(Package).Should().Be("Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe");
    }

    [Theory]
    // Another framework from the same publisher, at a version above the floor, is not the runtime.
    [InlineData(null, "Microsoft.WindowsAppRuntime.CBS.2_2.5.0.100_x64__8wekyb3d8bbwe")]
    // The 1.x runtime numbers its versions from 8000, far above a 2.x floor, and is still the wrong package.
    [InlineData(null, "Microsoft.WindowsAppRuntime.1.8_8000.946.1701.0_x64__8wekyb3d8bbwe")]
    // The right name signed by another publisher is not Microsoft's package.
    [InlineData(null, "Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__0123456789abc")]
    // Package identity compares without regard to case.
    [InlineData("2.5.1.0", "microsoft.windowsappruntime.2_2.5.1.0_X64__8WEKYB3D8BBWE")]
    // The right package is found among others.
    [InlineData(
        "2.4.0.0",
        "Microsoft.WindowsAppRuntime.CBS.2_2.9.0.100_x64__8wekyb3d8bbwe",
        "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe"
    )]
    public void HighestSatisfying_CountsOnlyTheRuntimeFamily(string? expected, params string[] fullNames)
    {
        Verdict(fullNames, Floor).Should().Be(Expected(expected));
    }

    [Theory]
    // An x64 process cannot load an x86 or arm64 framework, whatever its version.
    [InlineData(null, "Microsoft.WindowsAppRuntime.2_2.5.1.0_x86__8wekyb3d8bbwe")]
    [InlineData(null, "Microsoft.WindowsAppRuntime.2_2.5.1.0_arm64__8wekyb3d8bbwe")]
    // A newer x86 package beside an older x64 one lends the x64 one nothing.
    [InlineData(
        null,
        "Microsoft.WindowsAppRuntime.2_2.3.1.0_x64__8wekyb3d8bbwe",
        "Microsoft.WindowsAppRuntime.2_2.5.1.0_x86__8wekyb3d8bbwe"
    )]
    // The x64 package decides, even when an x86 package is newer.
    [InlineData(
        "2.4.0.0",
        "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe",
        "Microsoft.WindowsAppRuntime.2_2.5.1.0_x86__8wekyb3d8bbwe"
    )]
    public void HighestSatisfying_CountsOnlyTheInstallerArchitecture(string? expected, params string[] fullNames)
    {
        Verdict(fullNames, Floor).Should().Be(Expected(expected));
    }

    [Theory]
    // The floor itself satisfies it.
    [InlineData("2.4.0.0", "2.4.0.0", "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe")]
    [InlineData("2.4.0.0", null, "Microsoft.WindowsAppRuntime.2_2.3.1.0_x64__8wekyb3d8bbwe")]
    // Every field counts, the revision included.
    [InlineData("2.4.0.1", null, "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe")]
    // Fields compare as numbers, so 2.10 is above 2.4 and 2.9 below 2.10.
    [InlineData("2.4.0.0", "2.10.0.0", "Microsoft.WindowsAppRuntime.2_2.10.0.0_x64__8wekyb3d8bbwe")]
    [InlineData("2.10.0.0", null, "Microsoft.WindowsAppRuntime.2_2.9.0.0_x64__8wekyb3d8bbwe")]
    // A family keeps several versions side by side, and the highest is the one recorded, whatever
    // order the packaging API lists them in.
    [InlineData(
        "2.4.0.0",
        "2.5.1.0",
        "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe",
        "Microsoft.WindowsAppRuntime.2_2.3.1.0_x64__8wekyb3d8bbwe",
        "Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe"
    )]
    public void HighestSatisfying_ComparesVersionsFieldByField(
        string floor,
        string? expected,
        params string[] fullNames
    )
    {
        Verdict(fullNames, Version.Parse(floor)).Should().Be(Expected(expected));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData(null, "Microsoft.WindowsAppRuntime.2")]
    [InlineData(null, "Microsoft.WindowsAppRuntime.2_2.5.1.0_x64_8wekyb3d8bbwe")]
    [InlineData(null, "Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe_extra")]
    [InlineData(null, "Microsoft.WindowsAppRuntime.2_two.five_x64__8wekyb3d8bbwe")]
    [InlineData(null, "Microsoft.WindowsAppRuntime.2__x64__8wekyb3d8bbwe")]
    // A malformed name beside a good one hides nothing.
    [InlineData(
        "2.4.0.0",
        "Microsoft.WindowsAppRuntime.2_two.five_x64__8wekyb3d8bbwe",
        "Microsoft.WindowsAppRuntime.2_2.4.0.0_x64__8wekyb3d8bbwe"
    )]
    public void HighestSatisfying_SkipsAMalformedFullName(string? expected, params string[] fullNames)
    {
        Verdict(fullNames, Floor).Should().Be(Expected(expected));
    }

    [Fact]
    public void HighestSatisfying_RefusesAnEmptyFamily()
    {
        // The packaging API lists nothing for a family with no package registered for the user.
        Verdict([], Floor).Should().BeNull();
    }

    [Theory]
    // Nothing registered: the API succeeds and reports no packages.
    [InlineData(0, 0u, RuntimeRequirement.Listing.Empty)]
    // Packages registered: the API asks for buffers of the size it reports.
    [InlineData(122, 4u, RuntimeRequirement.Listing.Fetch)]
    // ERROR_INVALID_PARAMETER and ERROR_MORE_DATA, which a malformed or empty family name returns with a
    // count of 0, are failures, not an empty family.
    [InlineData(87, 0u, RuntimeRequirement.Listing.Failed)]
    [InlineData(234, 0u, RuntimeRequirement.Listing.Failed)]
    // ERROR_ACCESS_DENIED.
    [InlineData(5, 0u, RuntimeRequirement.Listing.Failed)]
    // Answers the contract rules out: success with packages but no buffer, and no packages but a buffer asked for.
    [InlineData(0, 3u, RuntimeRequirement.Listing.Failed)]
    [InlineData(122, 0u, RuntimeRequirement.Listing.Failed)]
    public void ReadSizingCall_NeverReadsAnErrorAsAnEmptyFamily(
        int result,
        uint count,
        RuntimeRequirement.Listing expected
    )
    {
        RuntimeRequirement.ReadSizingCall(result, count).Should().Be(expected);
    }

    private static Version? Verdict(string[] fullNames, Version floor) =>
        RuntimeRequirement.HighestSatisfying(fullNames, Package, Architecture, floor);

    private static Version? Expected(string? expected) => expected is null ? null : Version.Parse(expected);
}
