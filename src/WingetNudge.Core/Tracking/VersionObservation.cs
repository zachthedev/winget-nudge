namespace WingetNudge.Core.Tracking;

/// <summary>Where a version's availability date came from.</summary>
public enum PublishSource
{
    /// <summary>No publish date resolved yet; the first-seen time stands in.</summary>
    FirstSeen,

    /// <summary>Oldest commit touching the version's folder in microsoft/winget-pkgs.</summary>
    WingetPkgs,

    /// <summary>The <c>ReleaseDate</c> field of the installer manifest.</summary>
    Manifest,
}

/// <summary>What the app knows about one package version's age.</summary>
/// <param name="FirstSeen">When a check on this machine first observed the version.</param>
/// <param name="Published">When the version became available, or <c>null</c> while unresolved.</param>
/// <param name="Source">Which lookup produced <paramref name="Published"/>.</param>
public sealed record VersionObservation(DateTimeOffset FirstSeen, DateTimeOffset? Published, PublishSource Source)
{
    /// <summary>The date the cooldown counts from: published when known, first-seen otherwise.</summary>
    public DateTimeOffset AvailableSince => Published ?? FirstSeen;

    /// <summary>Observation seeded from a first sighting.</summary>
    /// <param name="firstSeen">Sighting time.</param>
    /// <returns>An unresolved observation.</returns>
    public static VersionObservation Seen(DateTimeOffset firstSeen) => new(firstSeen, null, PublishSource.FirstSeen);
}

/// <summary>On-disk shape of <c>version-tracking.json</c>.</summary>
public sealed record TrackingFile
{
    /// <summary>Schema version this app writes.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Schema version.</summary>
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Package id to version to observation.</summary>
    public Dictionary<string, Dictionary<string, VersionObservation>> Packages { get; init; } =
        new(StringComparer.Ordinal);
}
