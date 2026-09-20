using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Changelog;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Preferences;
using WingetNudge.Core.Tests.Support;
using WingetNudge.Core.Upgrade;

namespace WingetNudge.Core.Tests;

public sealed class UpgradeEngineTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private static readonly PackageRef Git = new("Git.Git", "Git");
    private static readonly PackageRef Bun = new("Bun.Bun", "Bun");

    private readonly TempData _data = new();

    // The engine defaults to App Installer's real diagnostic directory; the suite must not read it.
    private readonly string _diagnostics = Directory.CreateTempSubdirectory("winget-nudge-diag").FullName;
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeUpgrader _upgrader = new();
    private readonly RecordingInteraction _ui = new();
    private readonly HttpClient _http = new FakeHttpHandler().CreateClient();
    private readonly PreferenceStore _preferences;
    private readonly UpdateLog _log;

    public UpgradeEngineTests()
    {
        _preferences = new PreferenceStore(_data.Paths, _clock);
        _log = new UpdateLog(_data.Paths, _clock);
    }

    public void Dispose()
    {
        _http.Dispose();
        _data.Dispose();
        Directory.Delete(_diagnostics, recursive: true);
    }

    private UpgradeEngine Build(
        BlockingProcessDetector? detector = null,
        IUpgradeInteraction? ui = null,
        IDeElevatedUpgrader? deElevated = null
    ) =>
        new(
            new FakePackageSource([
                Fixture.Updatable("Git.Git", "2.47.0", "2.48.1", "Git"),
                Fixture.Updatable("Bun.Bun", "1.3", "1.4", "Bun"),
            ]),
            _upgrader,
            _preferences,
            _log,
            new ChangelogFetcher(_http),
            detector ?? new BlockingProcessDetector(),
            ui ?? _ui,
            static () => new InstallLocationIndex(),
            new WingetDiagnosticsReader(_diagnostics),
            _clock,
            deElevated ?? new FakeDeElevatedUpgrader(0)
        );

    [Fact]
    public async Task RunAsync_SilentSuccess_ClearsPreferencesAndLogs()
    {
        _preferences.Set("Git.Git", PreferenceState.Failed, "old failure");
        _upgrader.On("Git.Git", UpgradeMode.Silent, Fixture.Ok());

        UpgradeSummary summary = await Build().RunAsync([Git], CancellationToken.None);

        summary.Should().Be(new UpgradeSummary(1, 0, 0, false));
        _upgrader.Attempts.Should().Equal(("Git.Git", UpgradeMode.Silent));
        _preferences.Load().All.Should().BeEmpty();
        _log.Load().Select(static e => (e.PackageId, e.Result)).Should().Equal(("Git.Git", "upgraded"));
        _ui.Events.OfType<UpgradeEvent.Finished>()
            .Single()
            .Should()
            .Be(new UpgradeEvent.Finished("Git.Git", PackageResult.Upgraded, "upgraded"));
        _ui.Events.OfType<UpgradeEvent.Progress>().Should().NotBeEmpty();
        _ui.Prompts.Should().BeEmpty("nothing blocks a package with no known install directory");
    }

    [Fact]
    public async Task RunAsync_FilesInUse_RetriesInteractivelyBeforeForce()
    {
        _upgrader
            .On("Git.Git", UpgradeMode.Silent, Fixture.Fail(exit: UpgradeOutcome.FilesInUseExitCode))
            .On("Git.Git", UpgradeMode.Interactive, Fixture.Ok());

        UpgradeSummary summary = await Build().RunAsync([Git], CancellationToken.None);

        summary.Upgraded.Should().Be(1);
        _upgrader.Attempts.Select(static a => a.Mode).Should().Equal(UpgradeMode.Silent, UpgradeMode.Interactive);
        _log.Load().Single().Result.Should().Be("upgraded-interactive");
    }

    [Fact]
    public async Task RunAsync_OtherFailure_SkipsInteractiveAndForces()
    {
        _upgrader.On("Git.Git", UpgradeMode.Silent, Fixture.Fail()).On("Git.Git", UpgradeMode.Force, Fixture.Ok());

        await Build().RunAsync([Git], CancellationToken.None);

        _upgrader.Attempts.Select(static a => a.Mode).Should().Equal(UpgradeMode.Silent, UpgradeMode.Force);
        _log.Load().Single().Result.Should().Be("upgraded-forced");
    }

    [Fact]
    public async Task RunAsync_EveryAttemptFails_RecordsTheFailureWithReasonAndInstallerLog()
    {
        _upgrader
            .On("Git.Git", UpgradeMode.Silent, Fixture.Fail("InstallError", 1603))
            .On("Git.Git", UpgradeMode.Force, Fixture.Fail("InstallError", 1603));

        UpgradeSummary summary = await Build().RunAsync([Git], CancellationToken.None);

        summary.Should().Be(new UpgradeSummary(0, 0, 1, false));
        PreferenceEntry? entry = _preferences.Load().For("Git.Git");
        entry?.State.Should().Be(PreferenceState.Failed);
        entry?.Reason.Should().Be("InstallError (exit code 1603)");
        _log.Load()
            .Single()
            .Should()
            .BeEquivalentTo(
                new
                {
                    PackageId = "Git.Git",
                    Result = "failed",
                    Status = "InstallError (exit code 1603)",
                    InstallerErrorCode = 1603L,
                }
            );
        Directory.GetFiles(_data.Paths.InstallerLogDirectory, "Git.Git_*.log").Should().HaveCount(1);
        UpgradeEvent.Finished finished = _ui.Events.OfType<UpgradeEvent.Finished>().Single();
        finished.Detail.Should().Be("InstallError (exit code 1603)", "the path belongs on the link");
        finished.LogPath.Should().NotBeNull().And.Subject.As<string>().Should().EndWith(".log");
    }

    [Fact]
    public async Task RunAsync_UpgraderThrows_IsAFailureNotACrash()
    {
        UpgradeEngine engine = new(
            new FakePackageSource([]),
            new ThrowingUpgrader(),
            _preferences,
            _log,
            new ChangelogFetcher(_http),
            new BlockingProcessDetector(),
            _ui,
            static () => new InstallLocationIndex(),
            new WingetDiagnosticsReader(_diagnostics),
            _clock,
            new FakeDeElevatedUpgrader(0)
        );

        UpgradeSummary summary = await engine.RunAsync([Git], CancellationToken.None);

        summary.Failed.Should().Be(1);
        _preferences.Load().For("Git.Git")?.Reason.Should().Be("winget exploded");
    }

    [Fact]
    public async Task RunAsync_Canceled_StopsBeforeTheNextPackage()
    {
        using CancellationTokenSource cancellation = new();
        _upgrader.On("Git.Git", UpgradeMode.Silent, Fixture.Ok());
        UpgradeEngine engine = Build();
        _ui.OnFinished = () => cancellation.Cancel();

        UpgradeSummary summary = await engine.RunAsync([Git, Bun], cancellation.Token);

        summary.Should().Be(new UpgradeSummary(1, 0, 0, true));
        _upgrader.Attempts.Select(static a => a.Id).Should().Equal("Git.Git");
    }

    [Fact]
    public async Task RunAsync_ReportsPhasesThenEachPackageInOrder()
    {
        _upgrader.On("Git.Git", UpgradeMode.Silent, Fixture.Ok()).On("Bun.Bun", UpgradeMode.Silent, Fixture.Ok());

        await Build().RunAsync([Git, Bun], CancellationToken.None);

        _ui.Events.OfType<UpgradeEvent.Phase>()
            .Select(static p => p.Text)
            .Should()
            .Equal("Preparing", "Fetching changelogs");
        _ui.Events.OfType<UpgradeEvent.Started>().Select(static s => s.Package).Should().Equal(Git, Bun);
    }

    [Fact]
    public async Task RunAsync_ReportsEveryProgressBeforeItReturns()
    {
        _upgrader.On("Git.Git", UpgradeMode.Silent, Fixture.Ok()).On("Bun.Bun", UpgradeMode.Silent, Fixture.Ok());
        HoldingContext scheduler = new();
        SynchronizationContext? outer = SynchronizationContext.Current;
        Task<UpgradeSummary> run;
        try
        {
            SynchronizationContext.SetSynchronizationContext(scheduler);
            run = Build().RunAsync([Git, Bun], CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(outer);
        }

        await run;

        scheduler.Posted.Should().Be(0, "a queued callback reports after the run says it is done");
        foreach (string id in (string[])["Git.Git", "Bun.Bun"])
        {
            int finished = _ui.Events.FindIndex(e => e is UpgradeEvent.Finished f && f.PackageId == id);
            int progress = _ui.Events.FindLastIndex(e => e is UpgradeEvent.Progress p && p.PackageId == id);
            progress.Should().BeGreaterThanOrEqualTo(0, "winget reported a snapshot for {0}", id);
            progress.Should().BeLessThan(finished, "a finished row must not go back to showing progress");
        }
    }

    [Fact]
    public async Task RunAsync_CloseSessionRefuses_ClosesDirectlyAndStillUpgrades()
    {
        FakeCloseSession session = new(ShutdownResult.Refused("RmShutdown failed: ERROR_FAIL_NOACTION_REBOOT"));
        FakeBlockingDetector detector = new();
        detector.Blocks("Git.Git", new BlockingDetection(["nxplayer"], session));
        _upgrader.OnEach(
            "Git.Git",
            UpgradeMode.Silent,
            Fixture.Fail(exit: UpgradeOutcome.FilesInUseExitCode),
            Fixture.Ok()
        );

        UpgradeSummary summary = await Build(detector).RunAsync([Git], CancellationToken.None);

        summary.Should().Be(new UpgradeSummary(1, 0, 0, false));
        detector.DirectCloses.Should().ContainSingle().Which.Should().Equal("nxplayer");
        session.RestartCalls.Should().Be(0, "Restart Manager closed nothing to bring back");
        _ui.Events.OfType<UpgradeEvent.Message>()
            .Select(static message => message.Text)
            .Should()
            .Contain(static text => text.Contains("ERROR_FAIL_NOACTION_REBOOT"));
    }

    [Fact]
    public async Task RunAsync_HoldersSurviveTheDirectClose_NamesThemAndUpgradesAnyway()
    {
        FakeBlockingDetector detector = new() { Survivors = ["nxservice"] };
        detector.Blocks("Git.Git", new BlockingDetection(["nxservice"], null));
        _upgrader.OnEach(
            "Git.Git",
            UpgradeMode.Silent,
            Fixture.Fail(exit: UpgradeOutcome.FilesInUseExitCode),
            Fixture.Ok()
        );

        UpgradeSummary summary = await Build(detector).RunAsync([Git], CancellationToken.None);

        summary.Upgraded.Should().Be(1);
        _ui.Events.OfType<UpgradeEvent.Message>()
            .Select(static message => message.Text)
            .Should()
            .Contain(static text => text.Contains("Still running: nxservice"));
    }

    [Fact]
    public async Task RunAsync_CloseSessionCloses_RestartsWithoutTouchingTheDirectClose()
    {
        FakeCloseSession session = new(ShutdownResult.Success);
        FakeBlockingDetector detector = new();
        detector.Blocks("Git.Git", new BlockingDetection(["git-gui"], session));
        _upgrader.OnEach(
            "Git.Git",
            UpgradeMode.Silent,
            Fixture.Fail(exit: UpgradeOutcome.FilesInUseExitCode),
            Fixture.Ok()
        );

        await Build(detector).RunAsync([Git], CancellationToken.None);

        session.ShutdownCalls.Should().Be(1);
        session.RestartCalls.Should().Be(1);
        session.Disposed.Should().BeTrue();
        detector.DirectCloses.Should().BeEmpty();
        _upgrader
            .Attempts.Select(static attempt => attempt.Mode)
            .Should()
            .Equal(UpgradeMode.Silent, UpgradeMode.Silent);
    }

    [Fact]
    public async Task RunAsync_SilentUpgradeSucceeds_NeverAsksAboutARunningApp()
    {
        FakeCloseSession session = new(ShutdownResult.Success);
        FakeBlockingDetector detector = new();
        detector.Blocks("Git.Git", new BlockingDetection(["git-gui"], session));
        _upgrader.On("Git.Git", UpgradeMode.Silent, Fixture.Ok());

        UpgradeSummary summary = await Build(detector).RunAsync([Git], CancellationToken.None);

        summary.Upgraded.Should().Be(1);
        _ui.Prompts.Should().BeEmpty("winget never reported a locked file");
        session.ShutdownCalls.Should().Be(0);
        detector.DirectCloses.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_AppsExitedOnTheirOwn_ClosesNothingAndRestartsNothing()
    {
        FakeCloseSession session = new(ShutdownResult.Success);
        FakeBlockingDetector detector = new();
        detector.Blocks("Git.Git", new BlockingDetection(["git-gui"], session));
        _upgrader.OnEach(
            "Git.Git",
            UpgradeMode.Silent,
            Fixture.Fail(exit: UpgradeOutcome.FilesInUseExitCode),
            Fixture.Ok()
        );
        RecordingInteraction ui = new(CloseAppsDecision.AlreadyClosed);

        UpgradeSummary summary = await Build(detector, ui).RunAsync([Git], CancellationToken.None);

        summary.Upgraded.Should().Be(1);
        session.ShutdownCalls.Should().Be(0);
        session.RestartCalls.Should().Be(0);
        detector.DirectCloses.Should().BeEmpty();
    }

    [Fact]
    public async Task RunAsync_UserSkips_LeavesTheHoldersAlone()
    {
        FakeCloseSession session = new(ShutdownResult.Success);
        FakeBlockingDetector detector = new();
        detector.Blocks("Git.Git", new BlockingDetection(["git-gui"], session));
        _upgrader.On("Git.Git", UpgradeMode.Silent, Fixture.Fail(exit: UpgradeOutcome.FilesInUseExitCode));
        RecordingInteraction ui = new(CloseAppsDecision.Skip);

        UpgradeSummary summary = await Build(detector, ui).RunAsync([Git], CancellationToken.None);

        summary.Should().Be(new UpgradeSummary(0, 1, 0, false));
        session.ShutdownCalls.Should().Be(0);
        session.RestartCalls.Should().Be(0);
        detector.DirectCloses.Should().BeEmpty();
        // The silent attempt is what surfaces the locked file in the first place.
        _upgrader.Attempts.Should().Equal(("Git.Git", UpgradeMode.Silent));
    }

    [Fact]
    public async Task RunAsync_OnePackageThrows_TheRestOfTheRunStillHappens()
    {
        FakeBlockingDetector detector = new();
        detector.Throws("Git.Git", new InvalidOperationException("detection exploded"));
        _upgrader
            .On("Git.Git", UpgradeMode.Silent, Fixture.Fail(exit: UpgradeOutcome.FilesInUseExitCode))
            .On("Bun.Bun", UpgradeMode.Silent, Fixture.Ok());

        UpgradeSummary summary = await Build(detector).RunAsync([Git, Bun], CancellationToken.None);

        summary.Should().Be(new UpgradeSummary(1, 0, 1, false));
        _upgrader.Attempts.Select(static attempt => attempt.Id).Should().Contain("Bun.Bun");
        _preferences.Load().For("Git.Git")?.Reason.Should().Be("upgrade error: detection exploded");
        _ui.Events.OfType<UpgradeEvent.Finished>()
            .Select(static finished => (finished.PackageId, finished.Result))
            .Should()
            .Equal(("Git.Git", PackageResult.Failed), ("Bun.Bun", PackageResult.Upgraded));
    }

    [Fact]
    public async Task RunAsync_WingetGoesAway_StopsTheRunAndLeavesEveryPackageUnmarked()
    {
        _preferences.Set("Bun.Bun", PreferenceState.Muted, "muted by user");
        _upgrader.WingetGoneFor("Git.Git");

        UpgradeSummary summary = await Build().RunAsync([Git, Bun], CancellationToken.None);

        summary.Upgraded.Should().Be(0);
        summary.Failed.Should().Be(0, "no installer failed; winget never answered");
        summary.Skipped.Should().Be(2, "the current package and the one behind it");
        summary.AbortReason.Should().Contain("0x80040154");
        _upgrader.Attempts.Select(static attempt => attempt.Id).Should().Equal("Git.Git");
        _preferences.Load().For("Git.Git").Should().BeNull("an unreachable winget is not a failure");
        _preferences.Load().For("Bun.Bun")?.State.Should().Be(PreferenceState.Muted);
        _log.Load().Should().BeEmpty();
        _ui.Events.OfType<UpgradeEvent.Finished>()
            .Select(static finished => (finished.PackageId, finished.Result))
            .Should()
            .Equal(("Git.Git", PackageResult.Skipped), ("Bun.Bun", PackageResult.Skipped));
        _ui.Events.OfType<UpgradeEvent.Phase>()
            .Select(static phase => phase.Text)
            .Should()
            .Contain("winget is unavailable");
    }

    [Fact]
    public async Task RunAsync_PublishesEveryChangelogBeforeTheFirstPackageStarts()
    {
        _upgrader.On("Git.Git", UpgradeMode.Silent, Fixture.Ok()).On("Bun.Bun", UpgradeMode.Silent, Fixture.Ok());

        await Build().RunAsync([Git, Bun], CancellationToken.None);

        int changelogs = _ui.Events.FindIndex(static e => e is UpgradeEvent.Changelogs);
        int firstStart = _ui.Events.FindIndex(static e => e is UpgradeEvent.Started);
        changelogs.Should().BeGreaterThanOrEqualTo(0, "the run publishes notes for every row");
        changelogs.Should().BeLessThan(firstStart, "a waiting row must not sit on \"Loading\"");
    }

    [Fact]
    public async Task RunAsync_InstallerRefusesElevation_UpgradesWithoutIt()
    {
        _upgrader
            .On("Git.Git", UpgradeMode.Silent, Fixture.Fail())
            .On("Git.Git", UpgradeMode.Force, Fixture.Fail("InstallError", 1, UpgradeOutcome.RefusesElevationHResult));
        FakeDeElevatedUpgrader deElevated = new(0);

        UpgradeSummary summary = await Build(deElevated: deElevated).RunAsync([Git], CancellationToken.None);

        summary.Upgraded.Should().Be(1);
        deElevated.Calls.Should().Equal("Git.Git");
        _log.Load().Single().Result.Should().Be("upgraded-deelevated");
    }

    [Fact]
    public async Task RunAsync_DeElevatedRetryHitsAWingetCode_SaysWhatWingetSaid()
    {
        _upgrader
            .On("Git.Git", UpgradeMode.Silent, Fixture.Fail())
            .On("Git.Git", UpgradeMode.Force, Fixture.Fail("InstallError", 1, UpgradeOutcome.RefusesElevationHResult));
        // 0x8A150101: the very code a running Spotify produced.
        FakeDeElevatedUpgrader deElevated = new(unchecked((int)0x8A150101));

        UpgradeSummary summary = await Build(deElevated: deElevated).RunAsync([Git], CancellationToken.None);

        summary.Failed.Should().Be(1);
        _preferences
            .Load()
            .For("Git.Git")
            ?.Reason.Should()
            .Be("needs non-admin; Application is currently running. Exit the application then try again. (0x8A150101)");
    }

    [Fact]
    public async Task RunAsync_DeElevatedRetryHitsAnUnknownCode_FallsBackToHex()
    {
        _upgrader
            .On("Git.Git", UpgradeMode.Silent, Fixture.Fail())
            .On("Git.Git", UpgradeMode.Force, Fixture.Fail("InstallError", 1, UpgradeOutcome.RefusesElevationHResult));
        FakeDeElevatedUpgrader deElevated = new(unchecked((int)0x8A159999));

        await Build(deElevated: deElevated).RunAsync([Git], CancellationToken.None);

        _preferences
            .Load()
            .For("Git.Git")
            ?.Reason.Should()
            .Be("needs non-admin; the de-elevated retry failed (0x8A159999)");
    }

    [Fact]
    public async Task RunAsync_TaskSchedulerRefuses_IsAFailureNotACrash()
    {
        _upgrader
            .On("Git.Git", UpgradeMode.Silent, Fixture.Fail())
            .On("Git.Git", UpgradeMode.Force, Fixture.Fail("InstallError", 1, UpgradeOutcome.RefusesElevationHResult));
        FakeDeElevatedUpgrader deElevated = new(new InvalidOperationException("Task Scheduler said no"));

        UpgradeSummary summary = await Build(deElevated: deElevated).RunAsync([Git], CancellationToken.None);

        summary.Failed.Should().Be(1);
        _ui.Events.OfType<UpgradeEvent.Message>()
            .Select(static message => message.Text)
            .Should()
            .Contain("scheduled task error: Task Scheduler said no");
    }

    [Fact]
    public async Task RunAsync_WingetGoesAwayAfterClosingApps_StillRestartsThem()
    {
        FakeCloseSession session = new(ShutdownResult.Success);
        FakeBlockingDetector detector = new();
        detector.Blocks("Git.Git", new BlockingDetection(["git-gui"], session));
        // The locked file is what brings the holders into it; winget goes away on the retry.
        _upgrader
            .On("Git.Git", UpgradeMode.Silent, Fixture.Fail(exit: UpgradeOutcome.FilesInUseExitCode))
            .WingetGoneFor("Git.Git", afterAttempts: 1);

        UpgradeSummary summary = await Build(detector).RunAsync([Git], CancellationToken.None);

        summary.AbortReason.Should().NotBeNull();
        session.ShutdownCalls.Should().Be(1);
        session.RestartCalls.Should().Be(1, "a closed editor comes back even when the run dies");
        session.Disposed.Should().BeTrue();
    }

    /// <summary>
    /// A host's scheduler, which never runs what reaches it. A snapshot the engine hands to
    /// <see cref="System.Progress{T}"/> instead of reporting directly lands here and stays.
    /// </summary>
    private sealed class HoldingContext : SynchronizationContext
    {
        public int Posted { get; private set; }

        public override void Post(SendOrPostCallback d, object? state) => Posted++;

        public override void Send(SendOrPostCallback d, object? state) => Posted++;
    }

    private sealed class ThrowingUpgrader : IPackageUpgrader
    {
        public Task<UpgradeOutcome> UpgradeAsync(
            string packageId,
            UpgradeMode mode,
            IProgress<UpgradeProgress>? progress,
            CancellationToken cancellationToken
        ) => throw new InvalidOperationException("winget exploded");
    }
}
