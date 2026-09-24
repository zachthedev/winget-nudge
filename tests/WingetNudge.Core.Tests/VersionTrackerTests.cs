using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;
using WingetNudge.Core.Tracking;

namespace WingetNudge.Core.Tests;

public sealed class VersionTrackerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly VersionTracker _tracker;

    public VersionTrackerTests()
    {
        _tracker = new VersionTracker(_data.Paths, _clock);
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Reconcile_FirstRun_SeedsEveryVersionPastTheCooldown()
    {
        Dictionary<string, Dictionary<string, VersionObservation>> tracking = _tracker.Reconcile([
            Fixture.Updatable("Git.Git", "2.47.0", "2.48.1"),
            Fixture.Current("VideoLAN.VLC", "3.0.23"),
        ]);

        DateTimeOffset seed = Now.AddHours(-(Settings.DefaultCooldownHours + 1));
        tracking["Git.Git"]["2.47.0"].FirstSeen.Should().Be(seed);
        tracking["Git.Git"]["2.48.1"].FirstSeen.Should().Be(seed, "a first run treats every version as already known");
        tracking["VideoLAN.VLC"]["3.0.23"].FirstSeen.Should().Be(seed);
        tracking
            .Values.SelectMany(static v => v.Values)
            .Should()
            .OnlyContain(static o => o.Published == null && o.Source == PublishSource.FirstSeen);
    }

    [Fact]
    public void Reconcile_LaterRun_StampsNewUpgradeVersionsWithNowAndInstalledOnesPastCooldown()
    {
        _tracker.Reconcile([Fixture.Current("Git.Git", "2.47.0")]);
        _clock.Advance(TimeSpan.FromDays(3));

        Dictionary<string, Dictionary<string, VersionObservation>> tracking = _tracker.Reconcile([
            Fixture.Updatable("Git.Git", "2.47.0", "2.48.1"),
            Fixture.Current("New.Package", "1.0"),
        ]);

        tracking["Git.Git"]["2.48.1"].FirstSeen.Should().Be(_clock.GetUtcNow());
        tracking["New.Package"]
            ["1.0"]
            .FirstSeen.Should()
            .Be(_clock.GetUtcNow().AddHours(-(Settings.DefaultCooldownHours + 1)));
    }

    [Fact]
    public void Reconcile_PrunesVersionsAndPackagesNoLongerPresent()
    {
        _tracker.Reconcile([Fixture.Updatable("Git.Git", "2.47.0", "2.48.1"), Fixture.Current("Gone.Package", "1.0")]);

        Dictionary<string, Dictionary<string, VersionObservation>> tracking = _tracker.Reconcile([
            Fixture.Current("Git.Git", "2.48.1"),
        ]);

        tracking.Should().ContainKey("Git.Git").WhoseValue.Keys.Should().Equal("2.48.1");
        tracking.Should().NotContainKey("Gone.Package");
    }

    [Fact]
    public async Task Reconcile_KeepsExistingTimestampsAndResolvedDates()
    {
        _tracker.Reconcile([Fixture.Current("Git.Git", "2.47.0")]);
        _clock.Advance(TimeSpan.FromHours(1));
        _tracker.Reconcile([Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")]);
        DateTimeOffset firstSeen = _clock.GetUtcNow();
        DateTimeOffset published = Now.AddDays(-2);
        await _tracker.ResolvePublishDatesAsync(
            [Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")],
            new FakeReleaseDates().Map("Git.Git", "2.48.1", new ResolvedDate(published, PublishSource.WingetPkgs)),
            CancellationToken.None
        );
        _clock.Advance(TimeSpan.FromHours(5));

        Dictionary<string, Dictionary<string, VersionObservation>> tracking = _tracker.Reconcile([
            Fixture.Updatable("Git.Git", "2.47.0", "2.48.1"),
        ]);

        tracking["Git.Git"]
            ["2.48.1"]
            .Should()
            .Be(new VersionObservation(firstSeen, published, PublishSource.WingetPkgs));
    }

    [Fact]
    public void Load_ReadsTheSchemaOneMapOfFirstSeenTimestamps()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(
            _data.Paths.VersionTracking,
            """{ "Git.Git": { "2.48.1": "2026-09-01T10:00:00+00:00" }, "Bad.Entry": "not an object" }"""
        );

        Dictionary<string, Dictionary<string, VersionObservation>> tracking = _tracker.Load();

        tracking.Should().HaveCount(1);
        tracking["Git.Git"]
            ["2.48.1"]
            .Should()
            .Be(VersionObservation.Seen(new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Load_DeletesACorruptFile()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(_data.Paths.VersionTracking, "{ not json");

        _tracker.Load().Should().BeEmpty();
        File.Exists(_data.Paths.VersionTracking).Should().BeFalse();
    }

    [Fact]
    public void Load_WhileARenameHoldsTheFile_ReadsTheSavedTracking()
    {
        // A rename holds the file it moves with delete access and shares everything, for as long as the
        // rename takes. This handle stands in for it, and lets go only once the load has returned.
        _tracker.Reconcile([Fixture.Current("Git.Git", "2.47.0")]);
        using FileStream rename = new(
            _data.Paths.VersionTracking,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.DeleteOnClose
        );

        Func<Dictionary<string, Dictionary<string, VersionObservation>>> load = () => _tracker.Load();

        load.Should()
            .NotThrow("a read shares delete access, so a rename under way never refuses it")
            .Which.Should()
            .ContainKey("Git.Git", "the load reads what the reconcile saved")
            .WhoseValue.Should()
            .ContainKey("2.47.0");
    }

    [Fact]
    public void Load_WhileAnExclusiveHolderLetsGoInsideTheWritersWait_ReadsTheSavedTracking()
    {
        // Another program holds the file sharing nothing, and a dedicated thread lets it go 370 ms in. The read
        // retries a refused open on the writers' schedule, whose sleeps add to 511 ms before the last attempt,
        // and a sleep never returns early. A read that does not retry gives up at its first attempt.
        _tracker.Reconcile([Fixture.Current("Git.Git", "2.47.0")]);
        FileStream holder = new(_data.Paths.VersionTracking, FileMode.Open, FileAccess.Read, FileShare.None);
        Thread releaser = new(() =>
        {
            Thread.Sleep(370);
            holder.Dispose();
        });
        releaser.Start();

        Func<Dictionary<string, Dictionary<string, VersionObservation>>> load = () => _tracker.Load();

        try
        {
            load.Should()
                .NotThrow(
                    "the holder lets go at 370 ms, 141 ms before the read's last attempt at 511 ms or later, "
                        + "and 370 ms after a read that does not retry has given up"
                )
                .Which.Should()
                .ContainKey("Git.Git", "the load reads what the reconcile saved")
                .WhoseValue.Should()
                .ContainKey("2.47.0");
        }
        finally
        {
            // The data directory is deleted after the case, and a held file would refuse that.
            releaser.Join();
        }
    }

    [Theory]
    [InlineData(2, true, 22)]
    [InlineData(23.5, true, 1)]
    [InlineData(24, false, 0)]
    [InlineData(48, false, 0)]
    public void GetCooling_UsesTheDefaultTwentyFourHourWindow(double ageHours, bool cooling, int remaining)
    {
        _tracker.Reconcile([Fixture.Current("Git.Git", "2.47.0")]);
        _clock.Advance(TimeSpan.FromDays(1));
        _tracker.Reconcile([Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")]);
        _clock.Advance(TimeSpan.FromHours(ageHours));

        IReadOnlyDictionary<string, CoolingInfo> result = _tracker.GetCooling([
            Fixture.Updatable("Git.Git", "2.47.0", "2.48.1"),
        ]);

        result.ContainsKey("Git.Git").Should().Be(cooling);
        if (cooling)
        {
            result["Git.Git"].RemainingHours.Should().Be(remaining);
            result["Git.Git"].Source.Should().Be(PublishSource.FirstSeen);
        }
    }

    [Fact]
    public async Task GetCooling_PrefersThePublishDateOverFirstSeen()
    {
        _tracker.Reconcile([Fixture.Current("Git.Git", "2.47.0")]);
        _clock.Advance(TimeSpan.FromDays(1));
        PackageInfo[] updatable = [Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")];
        _tracker.Reconcile(updatable);
        FakeReleaseDates resolver = new FakeReleaseDates().Map(
            "Git.Git",
            "2.48.1",
            new ResolvedDate(_clock.GetUtcNow().AddDays(-5), PublishSource.WingetPkgs)
        );
        await _tracker.ResolvePublishDatesAsync(updatable, resolver, CancellationToken.None);

        _tracker
            .GetCooling(updatable)
            .Should()
            .BeEmpty("the version has been public for five days even though this machine just saw it");
    }

    [Fact]
    public void GetCooling_HonorsACooldownOverrideFromSettings()
    {
        new Settings { CooldownHours = 72 }.Save(_data.Paths);
        _tracker.Reconcile([Fixture.Current("Git.Git", "2.47.0")]);
        _clock.Advance(TimeSpan.FromDays(1));
        _tracker.Reconcile([Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")]);
        _clock.Advance(TimeSpan.FromHours(30));

        _tracker.GetCooling([Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")]).Should().ContainKey("Git.Git");
    }

    [Fact]
    public void GetCooling_IgnoresUntrackedPackages()
    {
        _tracker.GetCooling([Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")]).Should().BeEmpty();
    }

    [Fact]
    public async Task ResolvePublishDates_FillsOnlyUnresolvedVersionsAndRetriesFailuresLater()
    {
        PackageInfo[] updatable =
        [
            Fixture.Updatable("Git.Git", "2.47.0", "2.48.1"),
            Fixture.Updatable("Bun.Bun", "1.3", "1.4"),
        ];
        _tracker.Reconcile(updatable);
        FakeReleaseDates resolver = new FakeReleaseDates().Map(
            "Git.Git",
            "2.48.1",
            new ResolvedDate(Now.AddDays(-1), PublishSource.Manifest)
        );

        await _tracker.ResolvePublishDatesAsync(updatable, resolver, CancellationToken.None);
        await _tracker.ResolvePublishDatesAsync(updatable, resolver, CancellationToken.None);

        resolver.Requests.Should().Equal("Git.Git@2.48.1", "Bun.Bun@1.4", "Bun.Bun@1.4");
        Dictionary<string, Dictionary<string, VersionObservation>> tracking = _tracker.Load();
        tracking["Git.Git"]["2.48.1"].Published.Should().Be(Now.AddDays(-1));
        tracking["Git.Git"]["2.48.1"].Source.Should().Be(PublishSource.Manifest);
        tracking["Bun.Bun"]["1.4"].Published.Should().BeNull();
    }

    [Fact]
    public async Task ResolvePublishDatesAsync_KeepsAReconcileThatRanDuringTheLookup()
    {
        PackageInfo git = Fixture.Updatable("Git.Git", "2.47.0", "2.48.1");
        PackageInfo vlc = Fixture.Current("VideoLAN.VLC", "3.0.23");
        _tracker.Reconcile([git]);
        VersionTracker other = new(_data.Paths, _clock);
        ResolvedDate published = new(Now.AddDays(-5), PublishSource.WingetPkgs);

        await _tracker.ResolvePublishDatesAsync(
            [git],
            new ReconcilingResolver(() => other.Reconcile([git, vlc]), published),
            CancellationToken.None
        );

        Dictionary<string, Dictionary<string, VersionObservation>> tracking = _tracker.Load();
        tracking.Should().ContainKey("VideoLAN.VLC", "the reconcile that ran during the lookup survives");
        tracking["Git.Git"]["2.48.1"].Published.Should().Be(published.Date);
    }

    // Stands in for a second process that reconciles while a publish date lookup is in flight.
    private sealed class ReconcilingResolver(Action meanwhile, ResolvedDate date) : IReleaseDateResolver
    {
        public Task<ResolvedDate?> ResolveAsync(string packageId, string version, CancellationToken cancellationToken)
        {
            meanwhile();
            return Task.FromResult<ResolvedDate?>(date);
        }
    }
}
