using System.Xml.Linq;
using AwesomeAssertions;
using WingetNudge.Core.Actions;
using WingetNudge.Core.Notifications;
using WingetNudge.Core.Packages;

namespace WingetNudge.Core.Tests;

public sealed class NudgeActionTests
{
    [Theory]
    [InlineData("action=picker", typeof(NudgeAction.OpenPicker))]
    [InlineData("foo=bar;action=picker", typeof(NudgeAction.OpenPicker))]
    [InlineData("action=all", typeof(NudgeAction.Unknown))]
    [InlineData("action=", typeof(NudgeAction.Unknown))]
    [InlineData("", typeof(NudgeAction.Unknown))]
    [InlineData("=picker;;garbage", typeof(NudgeAction.Unknown))]
    [InlineData("action=pick%65r", typeof(NudgeAction.OpenPicker))]
    public void Parse_MapsSerializedArgumentsToActions(string serialized, Type expected)
    {
        NudgeAction.Parse(serialized).Should().BeOfType(expected);
    }

    [Fact]
    public void Buttons_OfferReviewAndDismissOnly()
    {
        UpdateActions
            .Buttons.Select(static b => (b.Label, b.Argument, b.IsDismiss))
            .Should()
            .Equal(("Review updates", UpdateActions.Picker, false), ("Dismiss", null, true));
        UpdateActions.LaunchArgument.Should().Be("action=picker");
    }
}

public sealed class UpdateNotificationTextTests
{
    [Fact]
    public void Compose_WithNothing_ReturnsNull()
    {
        UpdateNotificationText.Compose([]).Should().BeNull();
    }

    [Theory]
    [InlineData(1, "1 update available", "* A1")]
    [InlineData(4, "4 updates available", "* A1\n* A2\n* A3\n* A4")]
    [InlineData(5, "5 updates available", "* A1\n* A2\n* A3\n  + 2 more")]
    [InlineData(9, "9 updates available", "* A1\n* A2\n* A3\n  + 6 more")]
    public void Compose_ListsUpToFourNamesThenTruncates(int count, string title, string body)
    {
        string[] names = Enumerable.Range(1, count).Select(static i => $"A{i}").ToArray();

        UpdateNotificationText? text = UpdateNotificationText.Compose(names);

        text.Should().Be(new UpdateNotificationText(title, body));
    }
}

public sealed class NotificationPayloadTests
{
    [Fact]
    public void Build_ProducesAToastWithBodyLaunchReviewButtonAndSystemDismiss()
    {
        UpdateNotificationText text = new("2 updates available", "* Git & Tools\n* <Bun>");

        string xml = NotificationPayload.Build(text, @"C:\apps\nudge\Assets\icon.png");
        XDocument document = XDocument.Parse(xml);

        XElement toast = document.Root.Should().NotBeNull().And.Subject;
        toast.Attribute("launch")?.Value.Should().Be("action=picker");
        toast.Attribute("activationType")?.Value.Should().Be("foreground");
        toast
            .Descendants("text")
            .Select(static t => t.Value)
            .Should()
            .Equal("2 updates available", "* Git & Tools\n* <Bun>");
        toast
            .Descendants("image")
            .Single()
            .Attribute("src")
            ?.Value.Should()
            .Be("file:///C:/apps/nudge/Assets/icon.png");
        List<XElement> actions = toast.Descendants("action").ToList();
        actions.Should().HaveCount(2);
        actions[0].Attribute("content")?.Value.Should().Be("Review updates");
        actions[0].Attribute("arguments")?.Value.Should().Be("action=picker");
        actions[0].Attribute("activationType")?.Value.Should().Be("foreground");
        actions[1].Attribute("content")?.Value.Should().Be("Dismiss");
        actions[1].Attribute("activationType")?.Value.Should().Be("system");
        actions[1].Attribute("arguments")?.Value.Should().Be("dismiss");
    }
}

public sealed class PackageIdValidatorTests
{
    [Theory]
    [InlineData("Git.Git", true)]
    [InlineData("Microsoft.VCRedist.2015+.x64", true)]
    [InlineData("Notepad++.Notepad++", true)]
    [InlineData("Python.Python.3.13", true)]
    [InlineData("a", false)]
    [InlineData("", false)]
    [InlineData("Git.Git; calc", false)]
    [InlineData("Git.Git&", false)]
    [InlineData(".Leading", false)]
    [InlineData("Has Space.App", false)]
    public void IsValid_AllowsWingetIdsAndRejectsShellMetacharacters(string id, bool valid)
    {
        PackageIdValidator.IsValid(id).Should().Be(valid);
        Action ensure = () => PackageIdValidator.Ensure(id);
        if (valid)
        {
            ensure.Should().NotThrow();
        }
        else
        {
            ensure.Should().Throw<ArgumentException>().WithMessage($"*{id}*");
        }
    }
}

public sealed class UpgradeOutcomeTests
{
    [Fact]
    public void Reason_TranslatesTheFilesInUseExitCodeWhenWingetGivesNoCode()
    {
        UpgradeOutcome outcome = new(false, "InstallError", 6, null, false, "");

        outcome.Reason.Should().Be("InstallError (exit code 6): files are locked by a running app");
        outcome.IsFilesInUse.Should().BeTrue();
        outcome.RefusesElevation.Should().BeFalse();
    }

    [Fact]
    public void Reason_SpellsOutWingetsOwnCodeAndDetectsTheHResults()
    {
        UpgradeOutcome filesInUse = new(false, "InstallError", 0, UpgradeOutcome.FilesInUseHResult, false, "");
        UpgradeOutcome elevation = new(false, "InstallError", 0, UpgradeOutcome.RefusesElevationHResult, false, "");

        filesInUse
            .Reason.Should()
            .Be("InstallError: Application is currently in use by another application. (0x8A150111)");
        filesInUse.IsFilesInUse.Should().BeTrue();
        elevation.RefusesElevation.Should().BeTrue();
        elevation.IsFilesInUse.Should().BeFalse();
    }

    [Fact]
    public void Reason_KeepsAnUnknownCodeAsHex()
    {
        UpgradeOutcome outcome = new(false, "InstallError", 1, unchecked((int)0x8A159999), false, "");

        outcome.Reason.Should().Be("InstallError (exit code 1) (0x8A159999)");
    }

    [Fact]
    public void Status_DropsTheTrailingNewlineAComExceptionMessageCarries()
    {
        UpgradeOutcome.Failed("Class not registered\r\n").Status.Should().Be("Class not registered");
    }
}

public sealed class WingetErrorCodesTests
{
    [Theory]
    [InlineData(0x8A150006, "APPINSTALLER_CLI_ERROR_SHELLEXEC_INSTALL_FAILED")]
    [InlineData(0x8A150056, "APPINSTALLER_CLI_ERROR_INSTALLER_PROHIBITS_ELEVATION")]
    [InlineData(0x8A150111, "APPINSTALLER_CLI_ERROR_INSTALL_PACKAGE_IN_USE_BY_APPLICATION")]
    [InlineData(0x8A150001, "APPINSTALLER_CLI_ERROR_INTERNAL_ERROR")]
    public void Find_ResolvesWingetsOwnSymbols(long hresult, string symbol)
    {
        WingetErrorCodes.Find(unchecked((int)hresult))?.Symbol.Should().Be(symbol);
    }

    [Fact]
    public void Find_ReturnsNullForACodeWingetDoesNotDefine()
    {
        WingetErrorCodes.Find(unchecked((int)0x8A159999)).Should().BeNull();
    }
}
