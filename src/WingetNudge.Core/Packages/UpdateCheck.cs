using WingetNudge.Core.Preferences;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Tracking;

namespace WingetNudge.Core.Packages;

/// <summary>A package with an upgrade on offer and what the app knows about it.</summary>
/// <param name="Package">Winget data.</param>
/// <param name="AvailableSince">When the offered version became available, or first sighting when unknown.</param>
/// <param name="Source">Where <paramref name="AvailableSince"/> came from.</param>
public sealed record UpdateCandidate(PackageInfo Package, DateTimeOffset AvailableSince, PublishSource Source)
{
    /// <summary>Winget package id.</summary>
    public string Id => Package.Id;

    /// <summary>Display name.</summary>
    public string Name => Package.Name;

    /// <summary>Identity for the upgrade engine.</summary>
    public PackageRef Ref => new(Package.Id, Package.Name);
}

/// <summary>A package winget lists a newer version for but refuses to upgrade.</summary>
/// <param name="Package">Winget data, including the version it would install.</param>
/// <param name="Pin">The pin holding it, or <c>null</c> when nothing is pinned.</param>
public sealed record HeldBackPackage(PackageInfo Package, WingetPin? Pin)
{
    /// <summary>Winget package id.</summary>
    public string Id => Package.Id;

    /// <summary>Display name, falling back to the id.</summary>
    public string Name => Package.Name.Length > 0 ? Package.Name : Package.Id;

    /// <summary>Whether the app must refuse to upgrade it, which a blocking pin demands.</summary>
    public bool IsBlocked => Pin?.Blocks == true;

    /// <summary>Why winget will not upgrade it.</summary>
    public string Reason =>
        Pin is WingetPin pin
            ? pin.Describe()
            : "no installer matches how this is installed, so winget offers no upgrade path";
}

/// <summary>Updatable packages split by why they are or are not offered.</summary>
/// <param name="Normal">Offered packages, checked by default.</param>
/// <param name="Cooling">Packages whose offered version is still inside the cooldown window.</param>
/// <param name="Skipped">Packages whose offered version the user skipped.</param>
/// <param name="Muted">Packages the user hid.</param>
/// <param name="Failed">Packages whose last upgrade failed, with the recorded reason.</param>
/// <param name="HeldBack">
/// Packages winget names a newer version for but will not upgrade, with why: a pin, or no
/// installer matching how the package is installed.
/// </param>
public sealed record PackagePartition(
    IReadOnlyList<UpdateCandidate> Normal,
    IReadOnlyList<(UpdateCandidate Candidate, CoolingInfo Cooling)> Cooling,
    IReadOnlyList<UpdateCandidate> Skipped,
    IReadOnlyList<UpdateCandidate> Muted,
    IReadOnlyList<(UpdateCandidate Candidate, string Reason)> Failed,
    IReadOnlyList<HeldBackPackage> HeldBack
)
{
    /// <summary>Every package across all sections, which is what the picker has to show.</summary>
    public int Total => Normal.Count + Cooling.Count + Skipped.Count + Muted.Count + Failed.Count + HeldBack.Count;

    /// <summary>Splits packages into sections.</summary>
    /// <param name="updatable">Packages with an upgrade available.</param>
    /// <param name="snapshot">Preference state.</param>
    /// <param name="cooling">Cooling packages keyed by id.</param>
    /// <param name="tracking">Tracking data for availability dates.</param>
    /// <param name="fallback">Availability date for untracked packages.</param>
    /// <param name="heldBack">Packages winget will not upgrade, or <c>null</c> when none.</param>
    /// <returns>
    /// The partition. A package lands in the first matching section: failed, muted, skipped at
    /// the offered version, cooling, normal. A skip recorded for an older version is ignored.
    /// </returns>
    public static PackagePartition Build(
        IReadOnlyList<PackageInfo> updatable,
        PreferenceSnapshot snapshot,
        IReadOnlyDictionary<string, CoolingInfo> cooling,
        Dictionary<string, Dictionary<string, VersionObservation>> tracking,
        DateTimeOffset fallback,
        IReadOnlyList<HeldBackPackage>? heldBack = null
    )
    {
        List<UpdateCandidate> normal = [];
        List<(UpdateCandidate, CoolingInfo)> cool = [];
        List<UpdateCandidate> skipped = [];
        List<UpdateCandidate> muted = [];
        List<(UpdateCandidate, string)> failed = [];

        foreach (PackageInfo package in updatable)
        {
            VersionObservation? observation = VersionTracker.Observe(tracking, package);
            UpdateCandidate candidate = new(
                package,
                observation?.AvailableSince ?? fallback,
                observation?.Source ?? PublishSource.FirstSeen
            );
            PreferenceEntry? entry = snapshot.For(package.Id);

            if (entry is { State: PreferenceState.Failed })
            {
                failed.Add((candidate, entry.Reason ?? "unknown error"));
            }
            else if (entry is { State: PreferenceState.Muted })
            {
                muted.Add(candidate);
            }
            else if (
                entry is { State: PreferenceState.Skipped }
                && snapshot.IsSuppressed(package.Id, package.AvailableVersion)
            )
            {
                skipped.Add(candidate);
            }
            else if (cooling.TryGetValue(package.Id, out CoolingInfo? info))
            {
                cool.Add((candidate, info));
            }
            else
            {
                normal.Add(candidate);
            }
        }

        return new PackagePartition(normal, cool, skipped, muted, failed, heldBack ?? []);
    }
}

/// <summary>The package half of a check.</summary>
/// <param name="All">Full winget inventory.</param>
/// <param name="Partition">Updatable packages split by section.</param>
/// <param name="Tracking">
/// Availability data the scan resolved. A re-section needs it to measure the cooldown, and it
/// travels with the scan so no service rebuilt in the meantime can hand back an empty copy.
/// </param>
/// <param name="WriteFailures">
/// Bookkeeping writes that failed while the scan carried on. <c>diagnostics.log</c> records each.
/// </param>
public sealed record PackageScan(
    IReadOnlyList<PackageInfo> All,
    PackagePartition Partition,
    Dictionary<string, Dictionary<string, VersionObservation>> Tracking,
    IReadOnlyList<StateWriteFailure> WriteFailures
);

/// <summary>What the check found.</summary>
/// <param name="All">Full winget inventory.</param>
/// <param name="Partition">Updatable packages split by section.</param>
/// <param name="Tools">Manual tools with an update.</param>
/// <param name="WriteFailures">
/// Bookkeeping writes that failed while the check carried on. <c>diagnostics.log</c> records each.
/// </param>
public sealed record UpdateCheckResult(
    IReadOnlyList<PackageInfo> All,
    PackagePartition Partition,
    IReadOnlyList<ToolStatus> Tools,
    IReadOnlyList<StateWriteFailure> WriteFailures
)
{
    /// <summary>Packages offered for the notification and Update All.</summary>
    public IReadOnlyList<UpdateCandidate> Eligible => Partition.Normal;

    /// <summary>Names to announce: eligible packages then tools.</summary>
    public IReadOnlyList<string> Names =>
        [.. Eligible.Select(static candidate => candidate.Name), .. Tools.Select(static tool => tool.Name)];

    /// <summary>
    /// One key per announceable update, identifying the exact version on offer. A package whose
    /// offered version moves on is news again; the same version sitting there is not.
    /// </summary>
    public IReadOnlyList<string> Keys =>
        [
            .. Eligible.Select(static candidate => $"{candidate.Id}@{candidate.Package.AvailableVersion}"),
            .. Tools.Select(static tool => $"tool:{tool.Name}@{tool.Latest}"),
        ];
}

/// <summary>
/// The shared query behind the notification, Update All and the picker: inventory, tracking
/// reconciliation, publish-date resolution, preference and cooldown filtering, and manual tool
/// probes.
/// </summary>
/// <param name="source">Package inventory.</param>
/// <param name="preferences">Muted, failed and skipped state.</param>
/// <param name="tracker">Availability tracking.</param>
/// <param name="releaseDates">Publish date lookup.</param>
/// <param name="tools">Manual tool probes.</param>
/// <param name="clock">Time source.</param>
/// <param name="pins">Winget pin reader, or <c>null</c> to read App Installer's database.</param>
public sealed class UpdateCheck(
    IPackageSource source,
    PreferenceStore preferences,
    VersionTracker tracker,
    IReleaseDateResolver releaseDates,
    ToolProber tools,
    TimeProvider clock,
    WingetPinReader? pins = null
)
{
    /// <summary>Runs the full check.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Inventory, partition and tool statuses.</returns>
    public async Task<UpdateCheckResult> RunAsync(CancellationToken cancellationToken)
    {
        // Tool probes run their own processes and HTTP calls, which share nothing with the
        // winget query; starting them first overlaps the two waits. Each keeps its own list of
        // failed writes, since the two run at once.
        List<StateWriteFailure> toolFailures = [];
        Task<IReadOnlyList<ToolStatus>> probes = RunToolsAsync(toolFailures, cancellationToken);
        PackageScan scan = await RunPackagesAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ToolStatus> statuses = await probes.ConfigureAwait(false);
        return new UpdateCheckResult(scan.All, scan.Partition, statuses, [.. scan.WriteFailures, .. toolFailures]);
    }

    /// <summary>
    /// Runs the package half of the check on its own, so a caller with something to show can
    /// show it before the tool probes finish.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Inventory, partition and the bookkeeping writes that failed.</returns>
    public async Task<PackageScan> RunPackagesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<PackageInfo> all = await source.GetInstalledAsync(cancellationToken).ConfigureAwait(false);
        List<StateWriteFailure> failures = [];
        (Dictionary<string, Dictionary<string, VersionObservation>> tracking, PreferenceSnapshot snapshot) =
            await ResolveTrackingAsync(all, failures, cancellationToken).ConfigureAwait(false);
        return new PackageScan(all, Repartition(all, tracking, snapshot), tracking, failures);
    }

    /// <summary>Probes the manual tools on their own.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Only the tools with an update available.</returns>
    public Task<IReadOnlyList<ToolStatus>> RunToolsAsync(CancellationToken cancellationToken) =>
        RunToolsAsync([], cancellationToken);

    /// <summary>Probes the manual tools on their own.</summary>
    /// <param name="failures">Receives each cache write that failed, which <c>diagnostics.log</c> also records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Only the tools with an update available.</returns>
    public async Task<IReadOnlyList<ToolStatus>> RunToolsAsync(
        ICollection<StateWriteFailure> failures,
        CancellationToken cancellationToken
    )
    {
        IReadOnlyList<ToolStatus> statuses = await tools
            .GetStatusesAsync(failures, cancellationToken)
            .ConfigureAwait(false);
        return statuses.Where(static status => status.UpdateAvailable).ToArray();
    }

    /// <summary>
    /// Reconciles tracking against the inventory, resolves publish dates for the offered
    /// versions, drops stale skips, and partitions the updatable packages.
    /// </summary>
    /// <param name="all">Full winget inventory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The partition.</returns>
    public async Task<PackagePartition> PartitionAsync(
        IReadOnlyList<PackageInfo> all,
        CancellationToken cancellationToken
    )
    {
        // A failed bookkeeping write reaches diagnostics.log alone here, since the partition has no
        // place to carry it.
        (Dictionary<string, Dictionary<string, VersionObservation>> tracking, PreferenceSnapshot snapshot) =
            await ResolveTrackingAsync(all, [], cancellationToken).ConfigureAwait(false);
        return Repartition(all, tracking, snapshot);
    }

    // Every write here is bookkeeping: tracking, pruning and stale skips. A failed one joins the
    // failures rather than ending the scan, so one write cannot cost a notification or a picker list.
    // The snapshot returned is the scan's one preference load, so a prune that fails is attempted and
    // logged once per scan. It leaves out the stale skips the scan cleared, as the file does.
    private async Task<(
        Dictionary<string, Dictionary<string, VersionObservation>> Tracking,
        PreferenceSnapshot Snapshot
    )> ResolveTrackingAsync(
        IReadOnlyList<PackageInfo> all,
        ICollection<StateWriteFailure> failures,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, Dictionary<string, VersionObservation>> reconciled = tracker.Reconcile(all, failures);
        PackageInfo[] updatable = all.Where(static package => package.IsUpdateAvailable).ToArray();
        Dictionary<string, Dictionary<string, VersionObservation>> tracking = await tracker
            .ResolvePublishDatesAsync(updatable, releaseDates, reconciled, failures, cancellationToken)
            .ConfigureAwait(false);

        PreferenceSnapshot snapshot = preferences.Load(failures);
        HashSet<string> stale = new(StringComparer.Ordinal);
        foreach (PackageInfo package in updatable)
        {
            if (
                snapshot.For(package.Id) is { State: PreferenceState.Skipped }
                && !snapshot.IsSuppressed(package.Id, package.AvailableVersion)
            )
            {
                preferences.Clear(package.Id, failures);
                stale.Add(package.Id);
            }
        }

        Dictionary<string, PreferenceEntry> kept = snapshot
            .All.Where(pair => !stale.Contains(pair.Key))
            .ToDictionary(StringComparer.Ordinal);
        return (tracking, new PreferenceSnapshot(kept));
    }

    /// <summary>
    /// Re-sections the inventory from data already in hand: preferences, pins and the tracking
    /// already resolved. Nothing here reaches the network, so a mute or a skip re-sections at once.
    /// </summary>
    /// <param name="all">Full winget inventory.</param>
    /// <param name="tracking">Tracking data from a prior scan, <see cref="PackageScan.Tracking"/>.</param>
    /// <returns>The partition.</returns>
    public PackagePartition Repartition(
        IReadOnlyList<PackageInfo> all,
        Dictionary<string, Dictionary<string, VersionObservation>> tracking
    ) => Repartition(all, tracking, preferences.Load());

    private PackagePartition Repartition(
        IReadOnlyList<PackageInfo> all,
        Dictionary<string, Dictionary<string, VersionObservation>> tracking,
        PreferenceSnapshot snapshot
    )
    {
        PackageInfo[] updatable = all.Where(static package => package.IsUpdateAvailable).ToArray();
        IReadOnlyDictionary<string, CoolingInfo> cooling = tracker.GetCooling(updatable, tracking);
        IReadOnlyDictionary<string, WingetPin> pinned = (pins ?? new WingetPinReader()).Load();
        HeldBackPackage[] heldBack = all.Where(static package => package.IsHeldBack)
            .Select(package => new HeldBackPackage(
                package,
                pinned.TryGetValue(package.Id, out WingetPin? pin) ? pin : null
            ))
            .ToArray();
        return PackagePartition.Build(updatable, snapshot, cooling, tracking, clock.GetUtcNow(), heldBack);
    }
}
