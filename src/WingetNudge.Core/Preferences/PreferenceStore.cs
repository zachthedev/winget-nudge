using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Preferences;

/// <summary>Why a package is held back from notifications.</summary>
public enum PreferenceState
{
    /// <summary>The user hid the package until they unmute it.</summary>
    Muted,

    /// <summary>The last upgrade failed; the package is hidden until the entry expires.</summary>
    Failed,

    /// <summary>The user skipped one version; the package returns when a newer one appears.</summary>
    Skipped,
}

/// <summary>One package's preference entry.</summary>
/// <param name="State">Why the package is held back.</param>
/// <param name="Since">When the entry was written.</param>
/// <param name="Reason">Failure description, present only for <see cref="PreferenceState.Failed"/>.</param>
/// <param name="Version">Skipped version, present only for <see cref="PreferenceState.Skipped"/>.</param>
public sealed record PreferenceEntry(
    PreferenceState State,
    DateTimeOffset Since,
    string? Reason = null,
    string? Version = null
);

/// <summary>Snapshot of every preference entry.</summary>
/// <param name="All">Entries keyed by package id.</param>
public sealed record PreferenceSnapshot(IReadOnlyDictionary<string, PreferenceEntry> All)
{
    /// <summary>Looks up a package's entry.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <returns>The entry, or <c>null</c>.</returns>
    public PreferenceEntry? For(string packageId) =>
        All.TryGetValue(packageId, out PreferenceEntry? entry) ? entry : null;

    /// <summary>
    /// Whether a package is held back from notifications and Update All: muted, failed, or
    /// skipped at exactly the version on offer.
    /// </summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="availableVersion">Version on offer.</param>
    /// <returns><c>true</c> when the package is suppressed.</returns>
    public bool IsSuppressed(string packageId, string? availableVersion) =>
        For(packageId) switch
        {
            null => false,
            { State: PreferenceState.Skipped } skipped => string.Equals(
                skipped.Version,
                availableVersion,
                StringComparison.Ordinal
            ),
            _ => true,
        };
}

/// <summary>Read-modify-write access to <c>preferences.json</c>.</summary>
/// <param name="paths">Data file locations.</param>
/// <param name="clock">Time source.</param>
/// <param name="expiryDays">
/// Days a failed entry stays before it is pruned, or <c>null</c> to read the user's setting.
/// </param>
public sealed class PreferenceStore(DataPaths paths, TimeProvider clock, int? expiryDays = null)
{
    private int? _expiryDays = expiryDays;

    // Reading the setting is a file read, so it happens once rather than on every Load.
    private int ExpiryDays => _expiryDays ??= Settings.Load(paths).FailedExpiryDays;

    /// <summary>
    /// Loads preferences, pruning failed entries past their expiry and writing the file back
    /// when anything was pruned.
    /// </summary>
    /// <remarks>
    /// The prune write is bookkeeping, so a failed one never fails the load. The snapshot leaves the
    /// expired entries out either way, the file keeps them until a later write succeeds, and
    /// <c>diagnostics.log</c> records the failure.
    /// </remarks>
    /// <param name="failures">Receives a failed prune write, for a caller that reports it.</param>
    /// <returns>The current preference snapshot.</returns>
    public PreferenceSnapshot Load(ICollection<StateWriteFailure>? failures = null)
    {
        Dictionary<string, PreferenceEntry> entries = ReadFile();
        DateTimeOffset cutoff = clock.GetUtcNow().AddDays(-ExpiryDays);
        if (!entries.Values.Any(entry => IsExpired(entry, cutoff)))
        {
            return new PreferenceSnapshot(entries);
        }

        Dictionary<string, PreferenceEntry> pruned = entries
            .Where(pair => !IsExpired(pair.Value, cutoff))
            .ToDictionary(StringComparer.Ordinal);
        DiagnosticsLog.Attempt(
            paths,
            clock,
            failures ?? [],
            paths.Preferences,
            "drop expired failed entries",
            () =>
                pruned = Update(current =>
                {
                    int removed = 0;
                    foreach (string id in current.Keys.ToArray())
                    {
                        if (IsExpired(current[id], cutoff))
                        {
                            current.Remove(id);
                            removed++;
                        }
                    }

                    return removed > 0;
                })
        );
        return new PreferenceSnapshot(pruned);
    }

    /// <summary>Marks a package muted or failed.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="state">New state.</param>
    /// <param name="reason">Failure description for the failed state.</param>
    /// <param name="failures">
    /// Receives a failed write, which <c>diagnostics.log</c> also records, or <c>null</c> to let it throw.
    /// </param>
    public void Set(
        string packageId,
        PreferenceState state,
        string? reason = null,
        ICollection<StateWriteFailure>? failures = null
    )
    {
        PreferenceEntry entry = new(state, clock.GetUtcNow(), reason);
        DiagnosticsLog.Attempt(
            paths,
            clock,
            failures,
            paths.Preferences,
            $"record {packageId} as {state.ToString().ToLowerInvariant()}",
            () =>
                Update(current =>
                {
                    current[packageId] = entry;
                    return true;
                })
        );
    }

    /// <summary>Skips one version of a package.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="version">Version to skip.</param>
    public void SkipVersion(string packageId, string version)
    {
        PreferenceEntry entry = new(PreferenceState.Skipped, clock.GetUtcNow(), Version: version);
        Update(current =>
        {
            current[packageId] = entry;
            return true;
        });
    }

    /// <summary>Removes a package's entry so it is offered again.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="failures">
    /// Receives a failed write, which <c>diagnostics.log</c> also records, or <c>null</c> to let it throw.
    /// </param>
    public void Clear(string packageId, ICollection<StateWriteFailure>? failures = null) =>
        DiagnosticsLog.Attempt(
            paths,
            clock,
            failures,
            paths.Preferences,
            $"clear the entry for {packageId}",
            () => Update(current => current.Remove(packageId))
        );

    private static bool IsExpired(PreferenceEntry entry, DateTimeOffset cutoff) =>
        entry.State == PreferenceState.Failed && entry.Since <= cutoff;

    private Dictionary<string, PreferenceEntry> ReadFile() =>
        JsonFile.Read<Dictionary<string, PreferenceEntry>>(paths.Preferences, deleteIfCorrupt: false)
        ?? new Dictionary<string, PreferenceEntry>(StringComparer.Ordinal);

    // Each write reads the file afresh under its write lock, so an entry another process wrote since
    // this store last read survives. The change reports whether it changed anything.
    private Dictionary<string, PreferenceEntry> Update(Func<Dictionary<string, PreferenceEntry>, bool> change) =>
        JsonFile.Update<Dictionary<string, PreferenceEntry>>(
            paths.Preferences,
            deleteIfCorrupt: false,
            current =>
            {
                Dictionary<string, PreferenceEntry> entries =
                    current ?? new Dictionary<string, PreferenceEntry>(StringComparer.Ordinal);
                return change(entries) ? entries : null;
            }
        ) ?? new Dictionary<string, PreferenceEntry>(StringComparer.Ordinal);
}
