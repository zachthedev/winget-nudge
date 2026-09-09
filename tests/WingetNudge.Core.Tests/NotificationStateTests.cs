using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Notifications;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

public sealed class NotificationStateTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public void Dispose() => _data.Dispose();

    private NotificationState Build() => new(_data.Paths, _clock);

    [Fact]
    public void HasNews_WithNothingEverAnnounced_IsTrueForAnyUpdate()
    {
        Build().HasNews(["Git.Git@2.48.1"]).Should().BeTrue();
    }

    [Fact]
    public void HasNews_WithNoUpdates_IsFalse()
    {
        Build().HasNews([]).Should().BeFalse("silence is not news");
    }

    [Fact]
    public void HasNews_ForTheSameSetAlreadyAnnounced_IsFalse()
    {
        NotificationState state = Build();
        state.Record(["Git.Git@2.48.1", "Bun.Bun@1.4"]);

        state
            .HasNews(["Git.Git@2.48.1", "Bun.Bun@1.4"])
            .Should()
            .BeFalse("an interval check must not repeat itself all day");
    }

    [Fact]
    public void HasNews_ForANewerVersionOfAnAnnouncedPackage_IsTrue()
    {
        NotificationState state = Build();
        state.Record(["Git.Git@2.48.1"]);

        state.HasNews(["Git.Git@2.49.0"]).Should().BeTrue();
    }

    [Fact]
    public void HasNews_WhenOnePackageIsAdded_IsTrue()
    {
        NotificationState state = Build();
        state.Record(["Git.Git@2.48.1"]);

        state.HasNews(["Git.Git@2.48.1", "Bun.Bun@1.4"]).Should().BeTrue();
    }

    [Fact]
    public void HasNews_WhenTheSetOnlyShrinks_IsFalse()
    {
        NotificationState state = Build();
        state.Record(["Git.Git@2.48.1", "Bun.Bun@1.4"]);

        state
            .HasNews(["Git.Git@2.48.1"])
            .Should()
            .BeFalse("upgrading one package is not a reason to announce the rest again");
    }

    [Fact]
    public void Record_SurvivesANewInstanceOverTheSameDirectory()
    {
        Build().Record(["Git.Git@2.48.1"]);

        Build().HasNews(["Git.Git@2.48.1"]).Should().BeFalse();
        Build().Load().ShownAt.Should().Be(Now);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "announced": null }""")]
    public void Load_OverValidJsonInTheWrongShape_ReturnsEmptyAndAnnouncesAgain(string content)
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(_data.Paths.NotificationState, content);

        NotificationState state = Build();
        state.Load().Announced.Should().BeEmpty();
        state.HasNews(["Git.Git@2.48.1"]).Should().BeTrue("a bad record repeats, never throws");
    }

    [Fact]
    public void Load_OverACorruptFile_ReturnsEmptyAndAnnouncesAgain()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(_data.Paths.NotificationState, "{ not json");

        NotificationState state = Build();
        state.Load().Announced.Should().BeEmpty();
        state.HasNews(["Git.Git@2.48.1"]).Should().BeTrue("a lost record repeats, never skips");
    }
}
