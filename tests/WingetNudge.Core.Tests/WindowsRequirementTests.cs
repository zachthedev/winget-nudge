using AwesomeAssertions;
using WingetNudge.CustomActions;

namespace WingetNudge.Core.Tests;

/// <summary>
/// The verdict setup's custom action reaches over the running Windows build. A floor it cannot read
/// fails setup, and a build below the floor is a refusal.
/// </summary>
public sealed class WindowsRequirementTests
{
    [Theory]
    [InlineData("22000", 22000)]
    [InlineData("26100", 26100)]
    // Leading zeros are still decimal digits alone.
    [InlineData("022000", 22000)]
    [InlineData("", null)]
    [InlineData("0", null)]
    // A sign, a blank, a separator or a version is not a bare build number.
    [InlineData("-22000", null)]
    [InlineData("+22000", null)]
    [InlineData(" 22000", null)]
    [InlineData("22000 ", null)]
    [InlineData("22,000", null)]
    [InlineData("10.0.22000.0", null)]
    [InlineData("build", null)]
    // Past int's range is not a build Windows reports.
    [InlineData("2147483648", null)]
    public void ParseFloor_TakesDecimalDigitsAboveZeroAlone(string text, int? expected)
    {
        WindowsRequirement.ParseFloor(text).Should().Be(expected);
    }

    [Theory]
    [InlineData(10u, 0x00006658u, 26200u)]
    [InlineData(10u, 0x000055F0u, 22000u)]
    // The kernel's free and checked flags in the high nibble are not part of the build.
    [InlineData(10u, 0xF0006658u, 26200u)]
    [InlineData(10u, 0xC00055F0u, 22000u)]
    // A later major still publishes the field.
    [InlineData(11u, 0x00007530u, 30000u)]
    // Before Windows 10 the offset holds a reserved field, so the page names no build and no floor admits it.
    [InlineData(6u, 0x00006658u, 0u)]
    [InlineData(0u, 0xFFFFFFFFu, 0u)]
    public void SharedDataBuild_ReadsTheBuildOnlyWhereThePageCarriesIt(uint major, uint ntBuildNumber, uint expected)
    {
        WindowsRequirement.SharedDataBuild(major, ntBuildNumber).Should().Be(expected);
    }

    [Theory]
    // The floor itself satisfies it.
    [InlineData(22000u, 22000, true)]
    [InlineData(21999u, 22000, false)]
    [InlineData(26100u, 22000, true)]
    // Windows 10's last feature update stays below a Windows 11 floor.
    [InlineData(19045u, 22000, false)]
    // A build past int's range still compares as the larger number.
    [InlineData(uint.MaxValue, 22000, true)]
    public void Satisfies_OnlyAtOrAboveTheFloor(uint build, int floor, bool expected)
    {
        WindowsRequirement.Satisfies(build, floor).Should().Be(expected);
    }
}
