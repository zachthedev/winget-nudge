using System.Text.RegularExpressions;
using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Tools;

/// <summary>Persists the registry of manually tracked tools and its probe cache.</summary>
/// <param name="paths">Data file locations.</param>
public sealed partial class ToolRegistry(DataPaths paths)
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex IdPattern();

    /// <summary>Whether the value is a well-formed tool id.</summary>
    /// <param name="id">Candidate id.</param>
    /// <returns><c>true</c> for an alphanumeric id with dots, dashes or underscores.</returns>
    public static bool IsValidId(string id) => IdPattern().IsMatch(id);

    /// <summary>Loads every registered tool. A corrupt file is deleted, since it is recreatable.</summary>
    /// <returns>Tools keyed by id.</returns>
    public Dictionary<string, ToolDefinition> Load() =>
        JsonFile.Read<Dictionary<string, ToolDefinition>>(paths.ToolRegistry, deleteIfCorrupt: true)
        ?? new Dictionary<string, ToolDefinition>(StringComparer.Ordinal);

    /// <summary>Adds or replaces a tool.</summary>
    /// <param name="id">Registry key.</param>
    /// <param name="definition">Tool definition.</param>
    /// <exception cref="ArgumentException">The id is malformed.</exception>
    public void Register(string id, ToolDefinition definition)
    {
        if (!IsValidId(id))
        {
            throw new ArgumentException($"'{id}' is not a valid tool id.", nameof(id));
        }

        ToolDefinitionValidator.Ensure(definition);
        Dictionary<string, ToolDefinition> registry = Load();
        registry[id] = definition;
        JsonFile.Write(paths.ToolRegistry, registry);
    }

    /// <summary>Removes a tool and its cached probe result.</summary>
    /// <param name="id">Registry key.</param>
    /// <returns><c>true</c> when an entry was removed.</returns>
    public bool Unregister(string id)
    {
        Dictionary<string, ToolDefinition> registry = Load();
        if (!registry.Remove(id))
        {
            return false;
        }

        JsonFile.Write(paths.ToolRegistry, registry);

        Dictionary<string, ToolCacheEntry> cache = LoadCache();
        if (cache.Remove(id))
        {
            JsonFile.Write(paths.ToolCache, cache);
        }

        return true;
    }

    /// <summary>Loads the latest-version cache. A corrupt file is deleted.</summary>
    /// <returns>Cache entries keyed by tool id.</returns>
    public Dictionary<string, ToolCacheEntry> LoadCache() =>
        JsonFile.Read<Dictionary<string, ToolCacheEntry>>(paths.ToolCache, deleteIfCorrupt: true)
        ?? new Dictionary<string, ToolCacheEntry>(StringComparer.Ordinal);

    /// <summary>Writes one cache entry.</summary>
    /// <param name="id">Tool id.</param>
    /// <param name="entry">Probe result.</param>
    public void SaveCache(string id, ToolCacheEntry entry)
    {
        Dictionary<string, ToolCacheEntry> cache = LoadCache();
        cache[id] = entry;
        JsonFile.Write(paths.ToolCache, cache);
    }
}
