namespace WingetNudge.Core.Storage;

/// <summary>
/// Copies state from the PowerShell-era <c>WingetUpdater</c> data directory the first time the
/// app runs, so muted packages, cooldown history and registered tools carry over.
/// </summary>
public static class LegacyDataMigrator
{
    /// <summary>Data directory name of the PowerShell module.</summary>
    public const string LegacyFolderName = "WingetUpdater";

    private static readonly (string Source, Func<DataPaths, string> Target)[] Files =
    [
        ("preferences.json", static paths => paths.Preferences),
        ("version-tracking.json", static paths => paths.VersionTracking),
        ("module-config.json", static paths => paths.Settings),
        ("manual-registry.json", static paths => paths.ToolRegistry),
        ("manual-registry-cache.json", static paths => paths.ToolCache),
        ("update-log.json", static paths => paths.UpdateLog),
    ];

    /// <summary>
    /// Copies legacy files when the new data directory does not exist yet. The legacy directory
    /// is left untouched.
    /// </summary>
    /// <param name="paths">New data file locations.</param>
    /// <param name="legacyDirectory">Legacy directory, or <c>null</c> for the default under <c>%LOCALAPPDATA%</c>.</param>
    /// <returns>Number of files copied.</returns>
    public static int MigrateIfNeeded(DataPaths paths, string? legacyDirectory = null)
    {
        legacyDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            LegacyFolderName
        );

        if (Directory.Exists(paths.Directory) || !Directory.Exists(legacyDirectory))
        {
            return 0;
        }

        if (SafePath.IsReparsePoint(legacyDirectory))
        {
            return 0;
        }

        Directory.CreateDirectory(paths.Directory);
        int copied = 0;
        foreach ((string source, Func<DataPaths, string> target) in Files)
        {
            string sourcePath = Path.Combine(legacyDirectory, source);
            if (File.Exists(sourcePath) && !SafePath.IsReparsePoint(sourcePath))
            {
                File.Copy(sourcePath, target(paths), overwrite: false);
                copied++;
            }
        }

        return copied;
    }
}
