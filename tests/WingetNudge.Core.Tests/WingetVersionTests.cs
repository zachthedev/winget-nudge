using AwesomeAssertions;
using WingetNudge.Core.Packages;

namespace WingetNudge.Core.Tests;

public sealed class WingetVersionTests
{
    [Theory]
    // Version pairs winget reported for real upgrades.
    [InlineData("1.7.2", "1.19.2", true)]
    [InlineData("6.3.0", "6.6.1.0", true)]
    [InlineData("2026.05.07-c55b318", "2026.09.08-e1d69dd", true)]
    // A bounded installed version is unknown, not behind, so it names no upgrade.
    [InlineData("< 4.0.0", "4.0.0", false)]
    // The same release written two ways is not an upgrade.
    [InlineData("2.2.0.0", "2.2.0", false)]
    [InlineData("1.29.290.0", "1.29.290", false)]
    [InlineData("6.2.0.0", "6.2", false)]
    [InlineData("v2.0.8", "2.0.8", false)]
    [InlineData("ad 9.7.15", "9.7.15", false)]
    // A catalog lagging behind the installed version is not an upgrade either.
    [InlineData("1.27.1", "1.27.0", false)]
    [InlineData("8.52", "8.50", false)]
    [InlineData("152.0.7977.83", "151.0.7922.174", false)]
    [InlineData("2604.1.75.0", "2204.1.8.0", false)]
    [InlineData("26.150.0804.0011", "26.134.0713.0007", false)]
    [InlineData("7.2409.9001.0", "7.2208.15002.0", false)]
    [InlineData("2.4.0.0", "2.3.1", false)]
    [InlineData("> 152.0.7933.0", "152.0.7933.0", false)]
    public void IsUpgrade_MatchesWhatWingetOffers(string installed, string offered, bool expected)
    {
        WingetVersion.IsUpgrade(installed, offered).Should().Be(expected);
    }

    [Fact]
    public void IsUpgrade_RefusesToJudgeABoundedInstalledVersion()
    {
        // Winget writes "< 4.0.0" when it could not read the version, not when it read an older one.
        WingetVersion.IsUpgrade("< 4.0.0", "4.0.0").Should().BeFalse();
        WingetVersion.IsUpgrade("< 14.2.Rel1", "14.2.Rel1").Should().BeFalse();
        WingetVersion.IsBounded("< 4.0.0").Should().BeTrue();
        WingetVersion.IsBounded("4.0.0").Should().BeFalse();
    }

    [Theory]
    // Neither string carries a number, so nothing about release order can be read from them.
    [InlineData("abc", "def")]
    [InlineData("stable", "nightly")]
    [InlineData("", "")]
    public void IsUpgrade_RefusesTwoVersionsCarryingNoNumber(string installed, string offered)
    {
        WingetVersion.IsUpgrade(installed, offered).Should().BeFalse();
    }

    [Fact]
    public void IsUpgrade_RefusesABoundedOfferedVersion()
    {
        // A bound on the offered side is as unreadable as one on the installed side.
        WingetVersion.IsUpgrade("1.2.3", "< 9.9.9").Should().BeFalse();
        WingetVersion.IsUpgrade("1.2.3", "> 9.9.9").Should().BeFalse();
    }

    [Fact]
    public void IsUpgrade_StillWorksWhenOnlyOneSideCarriesANumber()
    {
        WingetVersion.IsUpgrade("unknown", "1.0").Should().BeTrue();
    }

    [Fact]
    public void Compare_KeepsAPartTooWideForALongOnTheNewerSide()
    {
        WingetVersion.Compare("99999999999999999999", "2").Should().BePositive();
        WingetVersion.Compare("2", "99999999999999999999").Should().BeNegative();
    }

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("1.0", null)]
    [InlineData(null, null)]
    public void IsUpgrade_IsFalseWhenEitherVersionIsUnknown(string? installed, string? offered)
    {
        WingetVersion.IsUpgrade(installed, offered).Should().BeFalse();
    }

    [Fact]
    public void Compare_OrdersAPreReleaseBeforeItsRelease()
    {
        WingetVersion.Compare("1.0-rc1", "1.0").Should().BeNegative();
        WingetVersion.Compare("1.0", "1.0-rc1").Should().BePositive();
        WingetVersion.Compare("1.0-rc1", "1.0-rc2").Should().BeNegative();
    }

    [Fact]
    public void Compare_TreatsTheSameReleaseAsEqualHoweverItIsWritten()
    {
        WingetVersion.Compare("3.0", "3.0.0.0").Should().Be(0);
        WingetVersion.Compare("v1.2.3", "1.2.3").Should().Be(0);
    }

    [Fact]
    public void Compare_PutsABoundedVersionOnTheRightSideOfItsBound()
    {
        WingetVersion.Compare("< 4.0.0", "4.0.0").Should().BeNegative();
        WingetVersion.Compare("> 4.0.0", "4.0.0").Should().BePositive();
        WingetVersion.Compare("< 4.0.0", "3.9.9").Should().BePositive("the bound is above 3.9.9");
    }
}
