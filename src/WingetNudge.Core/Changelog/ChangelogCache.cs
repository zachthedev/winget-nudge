using WingetNudge.Core.Packages;
using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Changelog;

/// <summary>One package version's notes and when they were last read.</summary>
/// <param name="Notes">Formatted release notes.</param>
/// <param name="LastUsed">When an entry was last served or written, for pruning.</param>
public sealed record CachedChangelog(string Notes, DateTimeOffset LastUsed);

/// <summary>
/// Release notes already fetched, keyed by package id and the installed-to-available range they
/// cover.
/// </summary>
/// <remarks>
/// Notes for a published range never change, so an entry never expires. The cache is what lets a
/// re-section, a settings change or a second run of the picker show notes without reaching GitHub
/// again, which also keeps the 60 requests an hour an unauthenticated caller gets from running
/// out. Only a lookup that fully succeeded belongs here; a partial one would stand in for the real
/// notes for good.
/// </remarks>
/// <param name="paths">Data file locations.</param>
/// <param name="clock">Time source.</param>
/// <param name="maxEntries">Entries kept; the least recently used are dropped past this.</param>
public sealed class ChangelogCache(
    DataPaths paths,
    TimeProvider clock,
    int maxEntries = ChangelogCache.DefaultMaxEntries
)
{
    /// <summary>Entries kept on disk before the least recently used are dropped.</summary>
    public const int DefaultMaxEntries = 500;

    private readonly Lock _gate = new();
    private Dictionary<string, CachedChangelog>? _entries;

    /// <summary>Reads the notes held for a package's installed-to-available range.</summary>
    /// <param name="package">Package with its installed and available versions.</param>
    /// <returns>The notes, or <c>null</c> when nothing is cached.</returns>
    public string? Get(PackageInfo package)
    {
        if (Key(package) is not string key)
        {
            return null;
        }

        lock (_gate)
        {
            Dictionary<string, CachedChangelog> entries = Entries();
            if (!entries.TryGetValue(key, out CachedChangelog? cached))
            {
                return null;
            }

            entries[key] = cached with { LastUsed = clock.GetUtcNow() };
            return cached.Notes;
        }
    }

    /// <summary>Records notes for packages and writes the cache to disk.</summary>
    /// <param name="notes">Each package with the formatted notes for its range.</param>
    public void Store(IReadOnlyList<(PackageInfo Package, string Notes)> notes)
    {
        if (notes.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            Dictionary<string, CachedChangelog> entries = Entries();
            DateTimeOffset now = clock.GetUtcNow();
            bool added = false;
            foreach ((PackageInfo package, string text) in notes)
            {
                if (Key(package) is string key)
                {
                    entries[key] = new CachedChangelog(text, now);
                    added = true;
                }
            }

            if (!added)
            {
                return;
            }

            Save(entries);
        }
    }

    /// <summary>Loads the entries once.</summary>
    private Dictionary<string, CachedChangelog> Entries() =>
        _entries ??= Valid(
            JsonFile.Read<Dictionary<string, CachedChangelog?>>(paths.ChangelogCache, deleteIfCorrupt: true)
        );

    /// <summary>
    /// Keeps the usable entries. A file that parses but holds a null entry is valid JSON in the
    /// wrong shape, so those entries are dropped rather than trusted.
    /// </summary>
    private static Dictionary<string, CachedChangelog> Valid(Dictionary<string, CachedChangelog?>? read)
    {
        Dictionary<string, CachedChangelog> entries = new(StringComparer.Ordinal);
        foreach ((string key, CachedChangelog? entry) in read ?? [])
        {
            if (entry is { Notes: not null })
            {
                entries[key] = entry;
            }
        }

        return entries;
    }

    private void Prune(Dictionary<string, CachedChangelog> entries)
    {
        if (entries.Count <= maxEntries)
        {
            return;
        }

        foreach (
            string key in entries
                .OrderBy(static pair => pair.Value.LastUsed)
                .Take(entries.Count - maxEntries)
                .Select(static pair => pair.Key)
                .ToArray()
        )
        {
            entries.Remove(key);
        }
    }

    // This process loaded the cache once, and another process may have stored notes since, so its
    // entries merge into a fresh read rather than replacing the file. Of two copies of one entry, the
    // more recently used wins.
    private void Save(Dictionary<string, CachedChangelog> entries)
    {
        Dictionary<string, CachedChangelog>? merged = null;
        try
        {
            JsonFile.Update<Dictionary<string, CachedChangelog?>>(
                paths.ChangelogCache,
                deleteIfCorrupt: true,
                current =>
                {
                    merged = Valid(current);
                    foreach ((string key, CachedChangelog entry) in entries)
                    {
                        if (
                            !merged.TryGetValue(key, out CachedChangelog? existing)
                            || existing.LastUsed < entry.LastUsed
                        )
                        {
                            merged[key] = entry;
                        }
                    }

                    Prune(merged);
                    return merged.ToDictionary(
                        static pair => pair.Key,
                        static CachedChangelog? (pair) => pair.Value,
                        StringComparer.Ordinal
                    );
                }
            );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Notes are recreatable; a failed write costs one refetch next time.
        }

        if (merged is not null)
        {
            _entries = merged;
        }
    }

    private static string? Key(PackageInfo package) =>
        string.IsNullOrEmpty(package.AvailableVersion)
            ? null
            : $"{package.Id}@{package.InstalledVersion}>{package.AvailableVersion}";
}
