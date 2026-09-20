using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Preferences;
using WingetNudge.Core.Tests.Support;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Tracking;

namespace WingetNudge.Core.Tests;

public sealed class UpdateCheckTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly PreferenceStore _preferences;
    private readonly VersionTracker _tracker;
    private readonly FakeReleaseDates _releaseDates = new();

    public UpdateCheckTests()
    {
        _preferences = new PreferenceStore(_data.Paths, _clock);
        _tracker = new VersionTracker(_data.Paths, _clock);
    }

    public void Dispose() => _data.Dispose();

    private UpdateCheck Build(params PackageInfo[] inventory)
    {
        ToolRegistry registry = new(_data.Paths);
        using HttpClient http = new FakeHttpHandler().CreateClient();
        ToolProber prober = new(registry, new FakeProcessRunner(), http, _clock);
        return new UpdateCheck(new FakePackageSource(inventory), _preferences, _tracker, _releaseDates, prober, _clock);
    }

    [Fact]
    public void Partition_PutsEachPackageInTheFirstMatchingSection()
    {
        PreferenceSnapshot snapshot = Snapshot(
            ("Failed.App", new PreferenceEntry(PreferenceState.Failed, Now, "exit code 6")),
            ("Muted.App", new PreferenceEntry(PreferenceState.Muted, Now)),
            ("Skipped.App", new PreferenceEntry(PreferenceState.Skipped, Now, null, "2.0")),
            ("Stale.App", new PreferenceEntry(PreferenceState.Skipped, Now, null, "1.9")),
            ("FailedAndMuted.App", new PreferenceEntry(PreferenceState.Failed, Now, "boom"))
        );
        Dictionary<string, CoolingInfo> cooling = new()
        {
            ["Cooling.App"] = new CoolingInfo(Now.AddHours(-2), PublishSource.WingetPkgs, 22),
            ["Muted.App"] = new CoolingInfo(Now.AddHours(-2), PublishSource.WingetPkgs, 22),
        };
        PackageInfo[] updatable =
        [
            Fixture.Updatable("Normal.App", "1.0", "2.0"),
            Fixture.Updatable("Failed.App", "1.0", "2.0"),
            Fixture.Updatable("Muted.App", "1.0", "2.0"),
            Fixture.Updatable("Skipped.App", "1.0", "2.0"),
            Fixture.Updatable("Stale.App", "1.0", "2.0"),
            Fixture.Updatable("Cooling.App", "1.0", "2.0"),
            Fixture.Updatable("FailedAndMuted.App", "1.0", "2.0"),
        ];

        PackagePartition partition = PackagePartition.Build(updatable, snapshot, cooling, [], Now);

        partition.Normal.Select(static c => c.Id).Should().Equal("Normal.App", "Stale.App");
        partition
            .Failed.Select(static f => (f.Candidate.Id, f.Reason))
            .Should()
            .Equal(("Failed.App", "exit code 6"), ("FailedAndMuted.App", "boom"));
        partition.Muted.Select(static c => c.Id).Should().Equal("Muted.App");
        partition.Skipped.Select(static c => c.Id).Should().Equal("Skipped.App");
        partition.Cooling.Select(static c => c.Candidate.Id).Should().Equal("Cooling.App");
        partition.Total.Should().Be(7);
    }

    [Fact]
    public void Partition_UsesTrackingForAvailabilityAndFallsBackToNow()
    {
        Dictionary<string, Dictionary<string, VersionObservation>> tracking = new()
        {
            ["Tracked.App"] = new Dictionary<string, VersionObservation>
            {
                ["2.0"] = new VersionObservation(Now.AddHours(-1), Now.AddDays(-3), PublishSource.Manifest),
            },
        };

        PackagePartition partition = PackagePartition.Build(
            [Fixture.Updatable("Tracked.App", "1.0", "2.0"), Fixture.Updatable("Untracked.App", "1.0", "2.0")],
            Snapshot(),
            new Dictionary<string, CoolingInfo>(),
            tracking,
            Now
        );

        partition
            .Normal[0]
            .Should()
            .Be(
                new UpdateCandidate(
                    Fixture.Updatable("Tracked.App", "1.0", "2.0"),
                    Now.AddDays(-3),
                    PublishSource.Manifest
                )
            );
        partition.Normal[1].AvailableSince.Should().Be(Now);
        partition.Normal[1].Source.Should().Be(PublishSource.FirstSeen);
    }

    [Fact]
    public async Task RunAsync_ReconcilesResolvesDatesAndDropsStaleSkips()
    {
        _preferences.SkipVersion("Git.Git", "2.48.0");
        _preferences.SkipVersion("Bun.Bun", "1.4");
        _releaseDates.Map("Git.Git", "2.48.1", new ResolvedDate(Now.AddDays(-2), PublishSource.WingetPkgs));
        UpdateCheck check = Build(
            Fixture.Updatable("Git.Git", "2.47.0", "2.48.1", "Git"),
            Fixture.Updatable("Bun.Bun", "1.3", "1.4", "Bun"),
            Fixture.Updatable("Fresh.App", "1.0", "1.1", "Fresh"),
            Fixture.Current("VideoLAN.VLC", "3.0.23")
        );

        UpdateCheckResult result = await check.RunAsync(CancellationToken.None);

        result.Partition.Normal.Select(static c => c.Id).Should().Equal("Git.Git", "Fresh.App");
        result.Partition.Skipped.Select(static c => c.Id).Should().Equal("Bun.Bun");
        _preferences.Load().All.Keys.Should().Equal("Bun.Bun");
        result.Names.Should().Equal("Git", "Fresh");
        _tracker.Load()["Git.Git"]["2.48.1"].Source.Should().Be(PublishSource.WingetPkgs);
        result.Tools.Should().BeEmpty();
    }

    [Fact]
    public async Task Repartition_OnARebuiltInstance_KeepsTheScansCoolingPackageCooling()
    {
        UpdateCheck first = Build(Fixture.Current("Git.Git", "2.47.0"));
        await first.RunAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(2));
        _releaseDates.Map(
            "Git.Git",
            "2.48.1",
            new ResolvedDate(_clock.GetUtcNow().AddHours(-3), PublishSource.WingetPkgs)
        );
        PackageInfo git = Fixture.Updatable("Git.Git", "2.47.0", "2.48.1");
        PackageScan scan = await Build(git).RunPackagesAsync(CancellationToken.None);

        // A settings save swaps in a new UpdateCheck that has resolved nothing of its own.
        PackagePartition resectioned = Build(git).Repartition(scan.All, scan.Tracking);

        resectioned.Normal.Should().BeEmpty("the cooldown still holds after a settings save");
        resectioned.Cooling.Should().ContainSingle().Which.Candidate.Id.Should().Be("Git.Git");
    }

    [Fact]
    public async Task RunAsync_HoldsBackAVersionPublishedInsideTheCooldown()
    {
        UpdateCheck check = Build(Fixture.Current("Git.Git", "2.47.0"));
        await check.RunAsync(CancellationToken.None);
        _clock.Advance(TimeSpan.FromDays(2));
        _releaseDates.Map(
            "Git.Git",
            "2.48.1",
            new ResolvedDate(_clock.GetUtcNow().AddHours(-3), PublishSource.WingetPkgs)
        );
        check = Build(Fixture.Updatable("Git.Git", "2.47.0", "2.48.1"));

        UpdateCheckResult result = await check.RunAsync(CancellationToken.None);

        result.Partition.Normal.Should().BeEmpty();
        result.Partition.Cooling.Should().ContainSingle().Which.Cooling.RemainingHours.Should().Be(21);
        result.Names.Should().BeEmpty();
    }

    private static PreferenceSnapshot Snapshot(params (string Id, PreferenceEntry Entry)[] entries) =>
        new(entries.ToDictionary(static e => e.Id, static e => e.Entry, StringComparer.Ordinal));
}
