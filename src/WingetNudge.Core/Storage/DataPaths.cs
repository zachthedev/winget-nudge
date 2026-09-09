namespace WingetNudge.Core.Storage;

/// <summary>Locations of every file the app persists under one data directory.</summary>
/// <param name="Directory">Root data directory. Created on first write.</param>
public sealed record DataPaths(string Directory)
{
    /// <summary>Data directory name under <c>%LOCALAPPDATA%</c>.</summary>
    public const string AppFolderName = "WingetNudge";

    /// <summary>Paths rooted at <c>%LOCALAPPDATA%\WingetNudge</c>.</summary>
    public static DataPaths Default { get; } =
        new(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppFolderName
            )
        );

    /// <summary>Muted and failed package states.</summary>
    public string Preferences => Path.Combine(Directory, "preferences.json");

    /// <summary>Per-package upgrade outcomes for the last 30 days.</summary>
    public string UpdateLog => Path.Combine(Directory, "update-log.json");

    /// <summary>Full installer result dumps for failed upgrades.</summary>
    public string InstallerLogDirectory => Path.Combine(Directory, "installer-logs");

    /// <summary>First-seen timestamp per package version.</summary>
    public string VersionTracking => Path.Combine(Directory, "version-tracking.json");

    /// <summary>User-tunable settings.</summary>
    public string Settings => Path.Combine(Directory, "settings.json");

    /// <summary>Tools installed outside winget that the app probes.</summary>
    public string ToolRegistry => Path.Combine(Directory, "manual-registry.json");

    /// <summary>Cached latest-version probe results for manual tools.</summary>
    public string ToolCache => Path.Combine(Directory, "manual-registry-cache.json");

    /// <summary>Release notes already fetched, keyed by package and version.</summary>
    public string ChangelogCache => Path.Combine(Directory, "changelog-cache.json");

    /// <summary>What the last notification announced, so a quiet check repeats nothing.</summary>
    public string NotificationState => Path.Combine(Directory, "notification-state.json");
}
