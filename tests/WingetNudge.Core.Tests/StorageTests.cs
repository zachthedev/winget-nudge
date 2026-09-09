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
        UpgradeOutcome outcome = new(
            false,
            "InstallError",
            6,
            UpgradeOutcome.FilesInUseHResult,
            true,
            "corr"
        );
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
        Directory
            .GetFiles(_data.Paths.InstallerLogDirectory, "Other.App_*.log")
            .Should()
            .HaveCount(1);
        string content = File.ReadAllText(gitLogs.Max(StringComparer.Ordinal) ?? "");
        content
            .Should()
            .Contain("Package: Git.Git")
            .And.Contain("InstallerErrorCode: 6")
            .And.Contain("ExtendedErrorCode: 0x8A150111")
            .And.Contain(
                "ExtendedError: APPINSTALLER_CLI_ERROR_INSTALL_PACKAGE_IN_USE_BY_APPLICATION"
            )
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
        File.WriteAllText(
            Path.Combine(legacy, "module-config.json"),
            """{ "cooldownHours": 48 }"""
        );
        File.WriteAllText(Path.Combine(legacy, "unrelated.txt"), "x");

        int copied = LegacyDataMigrator.MigrateIfNeeded(_data.Paths, legacy);

        copied.Should().Be(2);
        File.Exists(_data.Paths.Preferences).Should().BeTrue();
        Settings.Load(_data.Paths).CooldownHours.Should().Be(48);
        File.Exists(Path.Combine(_data.Paths.Directory, "unrelated.txt")).Should().BeFalse();
        File.Exists(Path.Combine(legacy, "preferences.json"))
            .Should()
            .BeTrue("the legacy directory is left alone");
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
            .MigrateIfNeeded(
                new DataPaths(Path.Combine(_data.Root, "fresh")),
                Path.Combine(_data.Root, "nope")
            )
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
            new Settings()
                .WithGitHubToken("ghp_setting")
                .ResolveGitHubToken()
                .Should()
                .Be("ghp_setting");
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
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers =
            StartupRegistrar.BuildTriggers(new Settings());

        triggers.Should().HaveCount(2);
        triggers[0].Should().BeOfType<Microsoft.Win32.TaskScheduler.WeeklyTrigger>();
        triggers[1].Should().BeOfType<Microsoft.Win32.TaskScheduler.LogonTrigger>();
    }

    [Fact]
    public void BuildTriggers_DailyReplacesTheWeeklySchedule()
    {
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers =
            StartupRegistrar.BuildTriggers(
                new Settings { Frequency = CheckFrequency.Daily, CheckAtLogon = false }
            );

        triggers
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeOfType<Microsoft.Win32.TaskScheduler.DailyTrigger>();
    }

    [Fact]
    public void BuildTriggers_HonorsTheChosenTime()
    {
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers =
            StartupRegistrar.BuildTriggers(
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
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers =
            StartupRegistrar.BuildTriggers(
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
        IReadOnlyList<Microsoft.Win32.TaskScheduler.Trigger> triggers =
            StartupRegistrar.BuildTriggers(
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
