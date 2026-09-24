using System.Text.Json;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Tracking;

/// <summary>A package version still inside the cooldown window.</summary>
/// <param name="AvailableSince">When the version became available.</param>
/// <param name="Source">Where that date came from.</param>
/// <param name="RemainingHours">Whole hours until the window closes, rounded up.</param>
public sealed record CoolingInfo(DateTimeOffset AvailableSince, PublishSource Source, int RemainingHours);

/// <summary>
/// Per-version availability tracking, the basis of the cooldown gate that keeps a version out
/// of notifications until it has been public for a while.
/// </summary>
/// <param name="paths">Data file locations.</param>
/// <param name="clock">Time source.</param>
public sealed class VersionTracker(DataPaths paths, TimeProvider clock)
{
    /// <summary>Reads the tracking file, accepting the schema-1 map of first-seen timestamps.</summary>
    /// <returns>Package id to version to observation; empty when the file is missing or corrupt.</returns>
    public Dictionary<string, Dictionary<string, VersionObservation>> Load() => Load(holdsLock: false);

    // A corrupt file is deleted only under its write lock, the way JsonFile.Read sets one aside, so the
    // delete never takes a file that a reconcile wrote after the first read.
    private Dictionary<string, Dictionary<string, VersionObservation>> Load(bool holdsLock)
    {
        if (!File.Exists(paths.VersionTracking))
        {
            return new Dictionary<string, Dictionary<string, VersionObservation>>(StringComparer.Ordinal);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(paths.VersionTracking));
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("packages", out JsonElement packages))
            {
                return packages.Deserialize<Dictionary<string, Dictionary<string, VersionObservation>>>(
                        JsonFile.Options
                    ) ?? new Dictionary<string, Dictionary<string, VersionObservation>>(StringComparer.Ordinal);
            }

            return ReadLegacy(root);
        }
        catch (FileNotFoundException)
        {
            // Deleted under the write lock since the check above.
            return new Dictionary<string, Dictionary<string, VersionObservation>>(StringComparer.Ordinal);
        }
        catch (JsonException) when (holdsLock)
        {
            _ = JsonFile.SetAside(paths.VersionTracking, delete: true, beforeWrite: false);
            return new Dictionary<string, Dictionary<string, VersionObservation>>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return JsonFile.RereadUnderLock(paths.VersionTracking, () => Load(holdsLock: true))
                ?? new Dictionary<string, Dictionary<string, VersionObservation>>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Reconciles tracked (id, version) pairs with the current winget inventory.
    /// </summary>
    /// <remarks>
    /// Entries matching neither a package's installed version nor its available upgrade are
    /// dropped. New pairs are added: installed versions are seeded one hour past the cooldown
    /// threshold, since the user already has them on disk; newly observed upgrade versions get
    /// the current time. On a true first run every entry is seeded.
    /// </remarks>
    /// <param name="packages">The unfiltered inventory so installed versions are recorded too.</param>
    /// <returns>The reconciled tracking data.</returns>
    public Dictionary<string, Dictionary<string, VersionObservation>> Reconcile(IReadOnlyList<PackageInfo> packages)
    {
        int cooldownHours = Settings.Load(paths).CooldownHours;
        DateTimeOffset now = clock.GetUtcNow();

        // The tracking file carries a legacy shape that JsonFile.Read cannot parse, so the reconcile
        // takes the file's write lock directly and reads it afresh inside.
        return JsonFile.Locked(paths.VersionTracking, () => ReconcileLocked(packages, cooldownHours, now));
    }

    private Dictionary<string, Dictionary<string, VersionObservation>> ReconcileLocked(
        IReadOnlyList<PackageInfo> packages,
        int cooldownHours,
        DateTimeOffset now
    )
    {
        bool firstRun = !File.Exists(paths.VersionTracking);
        Dictionary<string, Dictionary<string, VersionObservation>> tracking = Load(holdsLock: true);
        DateTimeOffset seed = now.AddHours(-(cooldownHours + 1));

        // ///// Current snapshot: id -> version -> installed? /////

        Dictionary<string, Dictionary<string, bool>> current = new(StringComparer.Ordinal);
        foreach (PackageInfo package in packages)
        {
            Dictionary<string, bool> versions = new(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(package.InstalledVersion))
            {
                versions[package.InstalledVersion] = true;
            }

            if (!string.IsNullOrWhiteSpace(package.AvailableVersion))
            {
                versions.TryAdd(package.AvailableVersion, false);
            }

            if (versions.Count > 0)
            {
                current[package.Id] = versions;
            }
        }

        // ///// Prune /////

        foreach (string id in tracking.Keys.ToArray())
        {
            if (!current.TryGetValue(id, out Dictionary<string, bool>? liveVersions))
            {
                tracking.Remove(id);
                continue;
            }

            Dictionary<string, VersionObservation> tracked = tracking[id];
            foreach (string version in tracked.Keys.ToArray())
            {
                if (!liveVersions.ContainsKey(version))
                {
                    tracked.Remove(version);
                }
            }

            if (tracked.Count == 0)
            {
                tracking.Remove(id);
            }
        }

        // ///// Seed /////

        foreach ((string id, Dictionary<string, bool> versions) in current)
        {
            if (!tracking.TryGetValue(id, out Dictionary<string, VersionObservation>? tracked))
            {
                tracked = new Dictionary<string, VersionObservation>(StringComparer.Ordinal);
                tracking[id] = tracked;
            }

            foreach ((string version, bool installed) in versions)
            {
                if (!tracked.ContainsKey(version))
                {
                    tracked[version] = VersionObservation.Seen(firstRun || installed ? seed : now);
                }
            }
        }

        Save(tracking);
        return tracking;
    }

    /// <summary>
    /// Fills in publish dates for available versions that lack one, persisting each success.
    /// Failed lookups stay unresolved and are retried on the next call.
    /// </summary>
    /// <param name="packages">Packages with an available upgrade.</param>
    /// <param name="resolver">Publish date lookup.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated tracking data.</returns>
    public async Task<Dictionary<string, Dictionary<string, VersionObservation>>> ResolvePublishDatesAsync(
        IReadOnlyList<PackageInfo> packages,
        IReleaseDateResolver resolver,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, Dictionary<string, VersionObservation>> tracking = Load();
        List<(string Id, string Version, ResolvedDate Resolved)> dates = [];

        foreach (PackageInfo package in packages)
        {
            if (
                package.AvailableVersion is null
                || !tracking.TryGetValue(package.Id, out Dictionary<string, VersionObservation>? tracked)
                || !tracked.TryGetValue(package.AvailableVersion, out VersionObservation? observation)
                || observation.Published is not null
            )
            {
                continue;
            }

            ResolvedDate? resolved = await resolver
                .ResolveAsync(package.Id, package.AvailableVersion, cancellationToken)
                .ConfigureAwait(false);
            if (resolved is not null)
            {
                dates.Add((package.Id, package.AvailableVersion, resolved));
            }
        }

        if (dates.Count == 0)
        {
            return tracking;
        }

        // The lookups take seconds, and no lock waits across them, so the dates land on a fresh read
        // rather than on the copy loaded before the first lookup.
        return JsonFile.Locked(paths.VersionTracking, () => ApplyDates(dates));
    }

    private Dictionary<string, Dictionary<string, VersionObservation>> ApplyDates(
        List<(string Id, string Version, ResolvedDate Resolved)> dates
    )
    {
        Dictionary<string, Dictionary<string, VersionObservation>> tracking = Load(holdsLock: true);
        bool changed = false;
        foreach ((string id, string version, ResolvedDate resolved) in dates)
        {
            if (
                tracking.TryGetValue(id, out Dictionary<string, VersionObservation>? tracked)
                && tracked.TryGetValue(version, out VersionObservation? observation)
                && observation.Published is null
            )
            {
                tracked[version] = observation with { Published = resolved.Date, Source = resolved.Source };
                changed = true;
            }
        }

        if (changed)
        {
            Save(tracking);
        }

        return tracking;
    }

    /// <summary>
    /// Returns the packages whose available version is still inside the cooldown window,
    /// measured from the publish date when known and the first sighting otherwise.
    /// </summary>
    /// <param name="packages">Packages with an available upgrade.</param>
    /// <param name="tracking">Tracking data, or <c>null</c> to load it.</param>
    /// <returns>Cooling packages keyed by id.</returns>
    public IReadOnlyDictionary<string, CoolingInfo> GetCooling(
        IReadOnlyList<PackageInfo> packages,
        Dictionary<string, Dictionary<string, VersionObservation>>? tracking = null
    )
    {
        Dictionary<string, CoolingInfo> cooling = new(StringComparer.Ordinal);
        if (packages.Count == 0)
        {
            return cooling;
        }

        tracking ??= Load();
        if (tracking.Count == 0)
        {
            return cooling;
        }

        int cooldownHours = Settings.Load(paths).CooldownHours;
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset cutoff = now.AddHours(-cooldownHours);

        foreach (PackageInfo package in packages)
        {
            VersionObservation? observation = Observe(tracking, package);
            if (observation is null)
            {
                continue;
            }

            DateTimeOffset since = observation.AvailableSince;
            if (since > cutoff)
            {
                double remaining = (since.AddHours(cooldownHours) - now).TotalHours;
                cooling[package.Id] = new CoolingInfo(since, observation.Source, (int)Math.Ceiling(remaining));
            }
        }

        return cooling;
    }

    /// <summary>Finds the observation for a package's available version.</summary>
    /// <param name="tracking">Tracking data.</param>
    /// <param name="package">Package with an available upgrade.</param>
    /// <returns>The observation, or <c>null</c> when untracked.</returns>
    public static VersionObservation? Observe(
        Dictionary<string, Dictionary<string, VersionObservation>> tracking,
        PackageInfo package
    ) =>
        package.AvailableVersion is not null
        && tracking.TryGetValue(package.Id, out Dictionary<string, VersionObservation>? tracked)
        && tracked.TryGetValue(package.AvailableVersion, out VersionObservation? observation)
            ? observation
            : null;

    private void Save(Dictionary<string, Dictionary<string, VersionObservation>> tracking) =>
        JsonFile.Write(paths.VersionTracking, new TrackingFile { Packages = tracking });

    private static Dictionary<string, Dictionary<string, VersionObservation>> ReadLegacy(JsonElement root)
    {
        Dictionary<string, Dictionary<string, VersionObservation>> tracking = new(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return tracking;
        }

        foreach (JsonProperty package in root.EnumerateObject())
        {
            if (package.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            Dictionary<string, VersionObservation> versions = new(StringComparer.Ordinal);
            foreach (JsonProperty version in package.Value.EnumerateObject())
            {
                if (
                    version.Value.ValueKind == JsonValueKind.String
                    && version.Value.TryGetDateTimeOffset(out DateTimeOffset seen)
                )
                {
                    versions[version.Name] = VersionObservation.Seen(seen);
                }
            }

            if (versions.Count > 0)
            {
                tracking[package.Name] = versions;
            }
        }

        return tracking;
    }
}
