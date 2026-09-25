using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Registration;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Upgrade;

namespace WingetNudge.Core.Tests;

public sealed class UpdateLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private const int LogsPerPackage = 10;

    private readonly UpdateLog _log;

    public UpdateLogTests()
    {
        _log = new UpdateLog(_data.Paths, _clock, logsPerPackage: LogsPerPackage);
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Append_KeepsThirtyDaysOfEntries()
    {
        _log.Append("Old.App", "upgraded", "Ok", 0);
        _clock.Advance(TimeSpan.FromDays(31));
        _log.Append("New.App", "failed", "InstallError", 6);

        _log.Load()
            .Select(static e => (e.PackageId, e.Result, e.Status, e.InstallerErrorCode))
            .Should()
            .Equal(("New.App", "failed", "InstallError", 6L));
    }

    [Fact]
    public void SaveInstallerLog_WritesTheOutcomeAndKeepsTenPerPackage()
    {
        UpgradeOutcome outcome = new(false, "InstallError", 6, UpgradeOutcome.FilesInUseHResult, true, "corr");
        string? first = null;
        for (int index = 0; index < 12; index++)
        {
            string written = _log.SaveInstallerLog("Git.Git", outcome);
            first ??= written;
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        _log.SaveInstallerLog("Other.App", outcome);

        string[] gitLogs = Directory.GetFiles(_data.Paths.InstallerLogDirectory, "Git.Git_*.log");
        gitLogs.Should().HaveCount(LogsPerPackage);
        gitLogs.Should().NotContain(first, "the oldest dump is pruned first");
        Directory.GetFiles(_data.Paths.InstallerLogDirectory, "Other.App_*.log").Should().HaveCount(1);
        string content = File.ReadAllText(gitLogs.Max(StringComparer.Ordinal) ?? "");
        content
            .Should()
            .Contain("Package: Git.Git")
            .And.Contain("InstallerErrorCode: 6")
            .And.Contain("ExtendedErrorCode: 0x8A150111")
            .And.Contain("ExtendedError: APPINSTALLER_CLI_ERROR_INSTALL_PACKAGE_IN_USE_BY_APPLICATION")
            .And.Contain("RebootRequired: True");
    }
}

public sealed class LegacyDataMigratorTests : IDisposable
{
    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void MigrateIfNeeded_CopiesKnownFilesAndRenamesTheConfig()
    {
        string legacy = Path.Combine(_data.Root, "WingetUpdater");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "preferences.json"), "{}");
        File.WriteAllText(Path.Combine(legacy, "module-config.json"), """{ "cooldownHours": 48 }""");
        File.WriteAllText(Path.Combine(legacy, "unrelated.txt"), "x");

        int copied = LegacyDataMigrator.MigrateIfNeeded(_data.Paths, legacy);

        copied.Should().Be(2);
        File.Exists(_data.Paths.Preferences).Should().BeTrue();
        Settings.Load(_data.Paths).CooldownHours.Should().Be(48);
        File.Exists(Path.Combine(_data.Paths.Directory, "unrelated.txt")).Should().BeFalse();
        File.Exists(Path.Combine(legacy, "preferences.json")).Should().BeTrue("the legacy directory is left alone");
    }

    [Fact]
    public void MigrateIfNeeded_DoesNothingWhenTheNewDirectoryExistsOrTheLegacyOneIsMissing()
    {
        string legacy = Path.Combine(_data.Root, "WingetUpdater");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "preferences.json"), "{}");
        Directory.CreateDirectory(_data.Paths.Directory);

        LegacyDataMigrator.MigrateIfNeeded(_data.Paths, legacy).Should().Be(0);
        LegacyDataMigrator
            .MigrateIfNeeded(new DataPaths(Path.Combine(_data.Root, "fresh")), Path.Combine(_data.Root, "nope"))
            .Should()
            .Be(0);
        File.Exists(_data.Paths.Preferences).Should().BeFalse();
    }
}

public sealed class InstallLocationIndexTests : IDisposable
{
    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Resolve_PrefersTheWingetIdThenADisplayNamePrefixAndRequiresTheDirectoryToExist()
    {
        string gitDir = Path.Combine(_data.Root, "Git");
        string codeDir = Path.Combine(_data.Root, "VS Code");
        Directory.CreateDirectory(gitDir);
        Directory.CreateDirectory(codeDir);
        InstallLocationIndex index = new();
        index.Add("Git.Git", "Git version 2.47", gitDir);
        index.Add(null, "Microsoft Visual Studio Code (User)", codeDir);
        index.Add("Gone.App", "Gone", Path.Combine(_data.Root, "missing"));

        index.Resolve(new PackageRef("Git.Git", "Something Else")).Should().Be(gitDir);
        index
            .Resolve(new PackageRef("Microsoft.VisualStudioCode", "Microsoft Visual Studio Code"))
            .Should()
            .Be(codeDir);
        index.Resolve(new PackageRef("Gone.App", "Gone")).Should().BeNull();
        index.Resolve(new PackageRef("Unknown.App", "")).Should().BeNull();
    }

    [Fact]
    public void EnumerateExecutables_FindsExesThreeLevelsDeep()
    {
        string root = Path.Combine(_data.Root, "app");
        string deep = Path.Combine(root, "a", "b", "c");
        string tooDeep = Path.Combine(deep, "d");
        Directory.CreateDirectory(tooDeep);
        File.WriteAllText(Path.Combine(root, "app.exe"), "");
        File.WriteAllText(Path.Combine(deep, "helper.exe"), "");
        File.WriteAllText(Path.Combine(tooDeep, "far.exe"), "");
        File.WriteAllText(Path.Combine(root, "readme.txt"), "");

        IReadOnlyList<string> executables = InstallLocationIndex.EnumerateExecutables(root);

        executables.Select(Path.GetFileName).Should().BeEquivalentTo("app.exe", "helper.exe");
    }
}

public sealed class SettingsTests : IDisposable
{
    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Load_FillsEveryDefaultWhenNothingIsSaved()
    {
        Settings settings = Settings.Load(_data.Paths);

        settings.Frequency.Should().Be(CheckFrequency.Weekly);
        settings.CheckDay.Should().Be(DayOfWeek.Monday);
        settings.CheckHour.Should().Be(9);
        settings.CheckMinute.Should().Be(0);
        settings.CheckAtLogon.Should().BeTrue();
        settings.CheckAtUnlock.Should().BeFalse();
        settings.LogonDelayMinutes.Should().Be(2);
        settings.CooldownHours.Should().Be(Settings.DefaultCooldownHours);
        settings.FailedExpiryDays.Should().Be(7);
        settings.ToolCacheHours.Should().Be(ToolProber.DefaultCacheHours);
        settings.LogRetentionDays.Should().Be(30);
        settings.InstallerLogsPerPackage.Should().Be(10);
        settings.HasGitHubToken.Should().BeFalse();
    }

    [Fact]
    public void Load_KeepsACorruptFileAndFallsBackToDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_data.Paths.Settings) ?? "");
        File.WriteAllText(_data.Paths.Settings, "{ not json");

        Settings settings = Settings.Load(_data.Paths);

        settings.Should().Be(new Settings());
        Directory
            .GetFiles(Path.GetDirectoryName(_data.Paths.Settings) ?? "", "settings.json*.corrupt")
            .Should()
            .ContainSingle("user choices are worth repairing by hand");
    }

    [Theory]
    [InlineData("utf-16")]
    [InlineData("utf-8")]
    public void Load_OfAFileWithItsByteOrderMark_ReadsItAndKeepsIt(string encodingName)
    {
        // The first run copies the PowerShell version's files over. Windows PowerShell's Out-File writes UTF-16
        // with a byte order mark, and its Set-Content -Encoding UTF8 writes UTF-8 with one.
        Encoding encoding = Encoding.GetEncoding(encodingName);
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(_data.Paths.Settings, """{ "cooldownHours": 30 }""", encoding);
        byte[] mark = encoding.GetPreamble();
        mark.Should().NotBeEmpty("the case is about a file that carries its byte order mark");
        File.ReadAllBytes(_data.Paths.Settings).Should().StartWith(mark, "the write puts the mark first");

        Settings.Load(_data.Paths).CooldownHours.Should().Be(30, "a byte order mark names the file's encoding");
        File.Exists(_data.Paths.Settings).Should().BeTrue("a file that reads stays where it is");
        Directory
            .GetFiles(_data.Paths.Directory, "settings.json.*.corrupt")
            .Should()
            .BeEmpty("a file that reads is never set aside");
    }

    [Fact]
    public void Load_WhileARenameHoldsTheFile_ReadsTheSavedSettings()
    {
        // A rename holds the file it moves with delete access and shares everything, for as long as the
        // rename takes. This handle stands in for it, and lets go only once the load has returned.
        new Settings { CooldownHours = 30 }.Save(_data.Paths);
        using FileStream rename = new(
            _data.Paths.Settings,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.DeleteOnClose
        );

        Func<Settings> load = () => Settings.Load(_data.Paths);

        load.Should()
            .NotThrow("a read shares delete access, so a rename under way never refuses it")
            .Which.CooldownHours.Should()
            .Be(30, "the load reads what the save wrote");
    }

    [Fact]
    public void Load_WhileAnExclusiveHolderLetsGoInsideTheWritersWait_ReadsTheSavedSettings()
    {
        // Another program holds the file sharing nothing, and lets go at the read's third wait between attempts. A
        // plain read waits up to nine times, on the writers' table. The read under the write lock waits twice, so a
        // read on that table makes its last attempt with the holder still there. The release comes at a count, not a
        // time, so no stall on a loaded machine moves it.
        new Settings { CooldownHours = 30 }.Save(_data.Paths);
        FileStream holder = new(_data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None);
        using AttemptWaits waits = new(3, holder.Dispose);
        Func<Settings> load = () => Settings.Load(_data.Paths);

        try
        {
            load.Should()
                .NotThrow("the holder lets go at the read's third wait, which a read that waits twice never reaches")
                .Which.CooldownHours.Should()
                .Be(30, "the load reads what the save wrote");
            waits.Delays.Take(3).Should().Equal([1, 2, 4], "a plain read waits on the writers' table");
        }
        finally
        {
            // The data directory is deleted after the case, and a held file would refuse that.
            holder.Dispose();
        }
    }

    [Fact]
    public void Load_WhileADeleteWaitsOnAnotherHandleInsideTheWritersWait_WaitsThenReadsDefaults()
    {
        // Another program's reader holds the file sharing delete, and a delete-on-close handle closes beside
        // it, so the file waits deleted and every open meets access denied. The reader lets go at the read's third
        // wait between attempts, which completes the delete. A read that does not retry access denied gives up at its
        // first attempt, and one on the write lock's table after two waits, so neither reaches the release.
        new Settings { CooldownHours = 30 }.Save(_data.Paths);
        FileStream reader = new(
            _data.Paths.Settings,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete
        );
        new FileStream(
            _data.Paths.Settings,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            4096,
            FileOptions.DeleteOnClose
        ).Dispose();
        using AttemptWaits waits = new(3, reader.Dispose);

        try
        {
            File.Exists(_data.Paths.Settings).Should().BeTrue("a file that waits deleted still exists by name");
            Action open = () =>
                new FileStream(
                    _data.Paths.Settings,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                ).Dispose();
            open.Should().Throw<UnauthorizedAccessException>("a file that waits deleted refuses every open");

            Func<Settings> load = () => Settings.Load(_data.Paths);

            load.Should()
                .NotThrow(
                    "the reader lets go at the read's third wait, which a read that fails access denied at once, "
                        + "or waits twice, never reaches"
                )
                .Which.CooldownHours.Should()
                .Be(Settings.DefaultCooldownHours, "the delete completes once the reader lets go, so nothing remains");
            waits.Delays.Take(3).Should().Equal([1, 2, 4], "a plain read waits on the writers' table");
        }
        finally
        {
            // The data directory is deleted after the case, and a held file would refuse that.
            reader.Dispose();
        }
    }

    [Fact]
    public void Load_WhileAnotherProgramHoldsTheFileThroughout_WaitsTheWholeWritersTableThenThrows()
    {
        // Another program holds settings.json sharing nothing and never lets go, so the read waits out the whole
        // writers' table before it gives up. The planned waits prove the table. The time taken proves the waits
        // sleep: the nine add to 511 ms, and each can end up to one 15.6 ms timer tick early, so the read takes at
        // least about 370 ms, and 350 ms leaves room. It is the one clock reading among the retry cases, and a lower
        // bound, so a stall only lengthens the call and never fails it. A read that skips its sleeps passes only if a
        // stall of 350 ms or more lands inside it.
        new Settings { CooldownHours = 30 }.Save(_data.Paths);
        using FileStream holder = new(_data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None);
        using AttemptWaits waits = new();
        Func<Settings> load = () => Settings.Load(_data.Paths);

        Stopwatch watch = Stopwatch.StartNew();
        load.Should().Throw<IOException>("the holder never lets go");
        watch.Stop();

        waits
            .Delays.Should()
            .Equal(
                [1, 2, 4, 8, 16, 32, 64, 128, 256],
                "a plain read waits the whole writers' table before it gives up"
            );
        watch
            .ElapsedMilliseconds.Should()
            .BeGreaterThanOrEqualTo(
                350,
                "the nine waits add to 511 ms, and each can end up to one 15.6 ms timer tick early, which leaves at least about 370 ms"
            );
    }

    [Fact]
    public void Save_WhileAnotherProgramHoldsTheFile_GivesUpItsLockedReadWithinTheShortWait()
    {
        // Another program holds settings.json sharing nothing and never lets go. The save's read under the write
        // lock plans its waits from the short table, 50 and 100 ms, where a plain read plans 511 ms on the writers'
        // table. The case checks the planned waits rather than a clock, so no stall on a loaded machine moves it.
        new Settings { CooldownHours = 30 }.Save(_data.Paths);
        using FileStream holder = new(_data.Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.None);
        using AttemptWaits waits = new();
        Action save = () => new Settings { CooldownHours = 40 }.Save(_data.Paths);

        save.Should().Throw<IOException>("the holder never lets go");
        waits
            .Delays.Should()
            .Equal(
                [50, 100],
                "a read under the write lock gives up after 150 ms of waits, where a plain read waits 511 ms"
            );
    }

    [Fact]
    public async Task Save_RacingALoadOverACorruptFile_KeepsEverySave()
    {
        // The settings view's save and another process's load meet over a corrupt file. The two interleave
        // differently from round to round, and every order must keep the save and a repair copy.
        Directory.CreateDirectory(_data.Paths.Directory);
        for (int round = 0; round < 100; round++)
        {
            File.WriteAllText(_data.Paths.Settings, "{ corrupt");
            using Barrier start = new(2);
            int hours = Settings.DefaultCooldownHours + 1 + round;
            Task reader = Task.Run(
                () =>
                {
                    start.SignalAndWait(TestContext.Current.CancellationToken);
                    Settings.Load(_data.Paths);
                },
                TestContext.Current.CancellationToken
            );
            Task writer = Task.Run(
                () =>
                {
                    start.SignalAndWait(TestContext.Current.CancellationToken);
                    new Settings { CooldownHours = hours }.Save(_data.Paths);
                },
                TestContext.Current.CancellationToken
            );

            Func<Task> race = () => Task.WhenAll(reader, writer);
            await race.Should().NotThrowAsync($"round {round}: neither side may trip over the other");
            Settings
                .Load(_data.Paths)
                .CooldownHours.Should()
                .Be(hours, $"round {round}: a save that returned is on disk, not in a repair copy");
            Directory
                .GetFiles(_data.Paths.Directory, "settings.json.*.corrupt")
                .Should()
                .HaveCount(Math.Min(round + 1, JsonFile.CorruptCopiesKept), $"round {round}: the corrupt file is kept");
        }
    }

    [Fact]
    public void Clamped_PullsEveryValueBackIntoRange()
    {
        Settings wild = new()
        {
            Frequency = (CheckFrequency)99,
            CheckDay = (DayOfWeek)42,
            CheckHour = 99,
            CheckMinute = -5,
            LogonDelayMinutes = 9999,
            CooldownHours = -1,
            FailedExpiryDays = 0,
            ToolCacheHours = 0,
            LogRetentionDays = 100_000,
            InstallerLogsPerPackage = 0,
        };

        Settings clamped = wild.Clamped();

        clamped.Frequency.Should().Be(CheckFrequency.Weekly);
        clamped.CheckDay.Should().Be(DayOfWeek.Monday);
        clamped.CheckHour.Should().Be(23);
        clamped.CheckMinute.Should().Be(0);
        clamped.LogonDelayMinutes.Should().Be(Settings.MaxLogonDelayMinutes);
        clamped.CooldownHours.Should().Be(0);
        clamped.FailedExpiryDays.Should().Be(1);
        clamped.ToolCacheHours.Should().Be(1);
        clamped.LogRetentionDays.Should().Be(Settings.MaxLogRetentionDays);
        clamped.InstallerLogsPerPackage.Should().Be(1);
    }

    [Fact]
    public void Save_RoundTripsEverySetting()
    {
        Settings saved = new()
        {
            Frequency = CheckFrequency.Daily,
            CheckDay = DayOfWeek.Thursday,
            CheckHour = 17,
            CheckMinute = 45,
            CheckAtLogon = false,
            CheckAtUnlock = true,
            LogonDelayMinutes = 15,
            CooldownHours = 72,
            FailedExpiryDays = 14,
            ToolCacheHours = 6,
            LogRetentionDays = 90,
            InstallerLogsPerPackage = 25,
        };
        saved.Save(_data.Paths);

        Settings.Load(_data.Paths).Should().Be(saved);
    }

    [Fact]
    public void WithGitHubToken_EncryptsTheTokenAndKeepsItOutOfTheFile()
    {
        Settings saved = new Settings().WithGitHubToken("  ghp_secret_value  ");
        saved.Save(_data.Paths);

        string onDisk = File.ReadAllText(_data.Paths.Settings);

        onDisk.Should().NotContain("ghp_secret_value", "a credential must not sit in cleartext");
        saved.HasGitHubToken.Should().BeTrue();
        Settings.Load(_data.Paths).ResolveGitHubToken().Should().Be("ghp_secret_value");
    }

    [Fact]
    public void ToString_RedactsTheToken()
    {
        new Settings().WithGitHubToken("ghp_secret_value").ToString().Should().NotContain("ghp_");
    }

    [Fact]
    public void ResolveGitHubToken_PrefersTheSettingOverTheEnvironment()
    {
        string? original = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "ghp_environment");
        try
        {
            new Settings().WithGitHubToken("ghp_setting").ResolveGitHubToken().Should().Be("ghp_setting");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", original);
        }
    }

    [Fact]
    public void ResolveGitHubToken_FallsBackToTheEnvironmentVariable()
    {
        string? original = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", "ghp_environment");
        try
        {
            new Settings().ResolveGitHubToken().Should().Be("ghp_environment");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", original);
        }
    }

    [Fact]
    public void ResolveGitHubToken_IgnoresABlobItCannotDecrypt()
    {
        string? original = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        try
        {
            new Settings { ProtectedGitHubToken = "not-a-blob" }
                .ResolveGitHubToken()
                .Should()
                .BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", original);
        }
    }

    [Fact]
    public void ResolveGitHubToken_IsNullWhenNeitherSourceHasOne()
    {
        string? original = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        Environment.SetEnvironmentVariable("GITHUB_TOKEN", null);
        try
        {
            new Settings().ResolveGitHubToken().Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("GITHUB_TOKEN", original);
        }
    }
}

public sealed class StartupRegistrarTriggerTests
{
    [Fact]
    public void BuildTriggers_WeeklyAndLogonByDefault()
    {
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers = StartupRegistrar.BuildTriggers(new Settings());

        triggers.Should().HaveCount(2);
        triggers[0].Should().BeOfType<Microsoft.Win32.TaskScheduler.WeeklyTrigger>();
        triggers[1].Should().BeOfType<Microsoft.Win32.TaskScheduler.LogonTrigger>();
    }

    [Fact]
    public void BuildTriggers_DailyReplacesTheWeeklySchedule()
    {
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers = StartupRegistrar.BuildTriggers(
            new Settings { Frequency = CheckFrequency.Daily, CheckAtLogon = false }
        );

        triggers.Should().ContainSingle().Which.Should().BeOfType<Microsoft.Win32.TaskScheduler.DailyTrigger>();
    }

    [Fact]
    public void BuildTriggers_HonorsTheChosenTime()
    {
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers = StartupRegistrar.BuildTriggers(
            new Settings
            {
                Frequency = CheckFrequency.Weekly,
                CheckHour = 17,
                CheckMinute = 45,
                CheckAtLogon = false,
            }
        );

        triggers[0].StartBoundary.Hour.Should().Be(17);
        triggers[0].StartBoundary.Minute.Should().Be(45);
    }

    [Fact]
    public void BuildTriggers_AddsUnlockWhenAsked()
    {
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers = StartupRegistrar.BuildTriggers(
            new Settings
            {
                Frequency = CheckFrequency.Never,
                CheckAtLogon = false,
                CheckAtUnlock = true,
            }
        );

        triggers
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<Microsoft.Win32.TaskScheduler.SessionStateChangeTrigger>();
    }

    [Fact]
    public void BuildTriggers_IsEmptyWhenEveryEventIsOff()
    {
        // An empty list is what tells the registrar to delete the task rather than register one
        // that can never fire.
        StartupRegistrar
            .BuildTriggers(
                new Settings
                {
                    Frequency = CheckFrequency.Never,
                    CheckAtLogon = false,
                    CheckAtUnlock = false,
                }
            )
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void RegisterScheduledTask_ReportsRemovalWhenNothingCanFire()
    {
        // Registering a task with no trigger would leave one that can never run, so the
        // registrar removes it instead and has to say so. Removing is the step that reaches
        // the machine-wide Task Scheduler, so the prefix is one no real task can carry.
        StartupRegistrar registrar = new(
            Path.Combine(Path.GetTempPath(), "WingetNudge.Tests", "never-run.exe"),
            "Winget Nudge Test",
            $"WingetNudge.Tests.{Guid.NewGuid():N}"
        );

        bool registered = registrar.RegisterScheduledTask(
            new Settings
            {
                Frequency = CheckFrequency.Never,
                CheckAtLogon = false,
                CheckAtUnlock = false,
            }
        );

        registered.Should().BeFalse();
    }

    [Fact]
    public void BuildTriggers_AppliesTheLogonDelay()
    {
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers = StartupRegistrar.BuildTriggers(
            new Settings { Frequency = CheckFrequency.Never, LogonDelayMinutes = 15 }
        );

        triggers
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<Microsoft.Win32.TaskScheduler.LogonTrigger>()
            .Which.Delay.Should()
            .Be(TimeSpan.FromMinutes(15));
    }
}

public sealed class RunLockTests : IDisposable
{
    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Acquire_WhileHeld_StandsDown()
    {
        using RunLock first = Taken(RunLock.Acquire(_data.Paths, RunLock.Upgrade));

        RunLock
            .Acquire(_data.Paths, RunLock.Upgrade)
            .Should()
            .BeOfType<RunLockAttempt.Held>("a second upgrade must stand down while one runs");
    }

    [Fact]
    public void Acquire_AfterRelease_Succeeds()
    {
        Taken(RunLock.Acquire(_data.Paths, RunLock.Check)).Dispose();

        using RunLock second = Taken(RunLock.Acquire(_data.Paths, RunLock.Check));

        second.Should().NotBeNull("the next run takes a lock the last one released");
    }

    [Theory]
    [InlineData(RunLock.Check, RunLock.Upgrade)]
    [InlineData(RunLock.Upgrade, RunLock.Check)]
    public void Acquire_ForAnotherName_IsUnaffected(string held, string wanted)
    {
        using RunLock holder = Taken(RunLock.Acquire(_data.Paths, held));

        using RunLock other = Taken(RunLock.Acquire(_data.Paths, wanted));

        other.Should().NotBeNull("a check and an upgrade lock separate runs");
    }

    [Fact]
    public void Acquire_WithAReadOnlyLockFile_ReportsWhyRatherThanThrowing()
    {
        string file = _data.Paths.RunLockFile(RunLock.Upgrade);
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(file, "");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        try
        {
            RunLockAttempt attempt = RunLock.Acquire(_data.Paths, RunLock.Upgrade);

            attempt
                .Should()
                .BeOfType<RunLockAttempt.Unavailable>(
                    "any process running as the user can set that attribute, and it survives a reboot"
                )
                .Which.Reason.Should()
                .Contain(file, "the caller shows the reason to someone who has to fix it");
        }
        finally
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [Fact]
    public void Acquire_WithADirectoryInTheLockFilePlace_ReportsWhyRatherThanThrowing()
    {
        Directory.CreateDirectory(_data.Paths.RunLockFile(RunLock.Check));

        RunLock
            .Acquire(_data.Paths, RunLock.Check)
            .Should()
            .BeOfType<RunLockAttempt.Unavailable>("a directory is not a file to open");
    }

    [Fact]
    public void Acquire_ThroughAFileInTheDataDirectoryPlace_IsNotReadAsAnotherRun()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_data.Paths.Directory) ?? _data.Root);
        File.WriteAllText(_data.Paths.Directory, "");

        RunLock
            .Acquire(_data.Paths, RunLock.Upgrade)
            .Should()
            .BeOfType<RunLockAttempt.Unavailable>(
                "an IOException that is not a sharing violation is not a second run holding the lock"
            );
    }

    [Fact]
    public void Acquire_ThroughAReparsePoint_ReportsWhyRatherThanThrowing()
    {
        string file = _data.Paths.RunLockFile(RunLock.Upgrade);
        string elsewhere = Path.Combine(_data.Root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Directory.CreateSymbolicLink(_data.Paths.Directory, elsewhere);

        RunLock
            .Acquire(_data.Paths, RunLock.Upgrade)
            .Should()
            .BeOfType<RunLockAttempt.Unavailable>(
                "the elevated process refuses to write through a link any user process can plant"
            )
            .Which.Reason.Should()
            .Contain(file, "the caller shows the reason to someone who has to fix it")
            .And.Contain(SafePath.ReparsePointCause, "the reason names the link rather than a failed open");
        Directory.EnumerateFileSystemEntries(elsewhere).Should().BeEmpty("nothing is written through the link");
    }

    [Theory]
    [InlineData("")]
    [InlineData("Upgrade")]
    [InlineData(@"..\..\evil")]
    [InlineData(@"C:\Windows\Temp\evil")]
    public void RunLockFile_ForANameThatIsNoRun_Throws(string name)
    {
        Action naming = () => _data.Paths.RunLockFile(name);

        naming
            .Should()
            .Throw<ArgumentException>("a rooted or relative name lands outside the data directory")
            .WithMessage($"*{name}*");
    }

    [Theory]
    [InlineData(RunLock.Upgrade)]
    [InlineData(RunLock.Check)]
    public void RunLockFile_ForARunName_SitsInTheDataDirectory(string name)
    {
        _data.Paths.RunLockFile(name).Should().Be(Path.Combine(_data.Paths.Directory, $"{name}.lock"));
    }

    private static RunLock Taken(RunLockAttempt attempt) =>
        attempt.Should().BeOfType<RunLockAttempt.Taken>().Which.Lock;
}
