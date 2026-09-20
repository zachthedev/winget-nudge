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

    /// <summary>
    /// File one run holds an exclusive handle on, so a second process of the same kind stands
    /// down. It stays empty; the handle is the lock.
    /// </summary>
    /// <remarks>
    /// The name is one of <see cref="RunLock"/>'s two constants rather than any string. This type
    /// is public and an elevated process builds paths with it, and an unconstrained name reaches
    /// <see cref="Path.Combine(string, string)"/>, where a rooted or relative one lands outside the
    /// data directory.
    /// </remarks>
    /// <param name="name">Run name, from <see cref="RunLock"/>.</param>
    /// <returns>Path of the lock file.</returns>
    /// <exception cref="ArgumentException">The name is neither run name.</exception>
    public string RunLockFile(string name) =>
        name is RunLock.Upgrade or RunLock.Check
            ? Path.Combine(Directory, $"{name}.lock")
            : throw new ArgumentException(
                $"'{name}' names no run. The runs are '{RunLock.Upgrade}' and '{RunLock.Check}'.",
                nameof(name)
            );
}
