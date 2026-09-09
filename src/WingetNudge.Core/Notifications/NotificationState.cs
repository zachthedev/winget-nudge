using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Notifications;

/// <summary>What the last notification announced.</summary>
/// <param name="Announced">Package and tool keys, each an id at a version.</param>
/// <param name="ShownAt">When the notification was shown.</param>
public sealed record AnnouncedUpdates(IReadOnlyList<string> Announced, DateTimeOffset ShownAt)
{
    /// <summary>Nothing announced yet.</summary>
    public static AnnouncedUpdates Empty { get; } = new([], DateTimeOffset.MinValue);
}

/// <summary>
/// Remembers what the last notification named, so a quiet background check announces only what
/// the user has not been told about.
/// </summary>
/// <remarks>
/// A background check runs on an interval, and the same eligible set survives across many runs.
/// Comparing against what was announced is what keeps an interval short enough to be useful from
/// repeating itself every time it fires.
/// </remarks>
/// <param name="paths">Data file locations.</param>
/// <param name="clock">Time source.</param>
public sealed class NotificationState(DataPaths paths, TimeProvider clock)
{
    /// <summary>Reads what the last notification announced.</summary>
    /// <returns>
    /// The recorded set, empty when nothing was ever announced or the file parses into the wrong
    /// shape, such as <c>{}</c>.
    /// </returns>
    public AnnouncedUpdates Load() =>
        JsonFile.Read<AnnouncedUpdates>(paths.NotificationState, deleteIfCorrupt: true)
            is { Announced: not null } announced
            ? announced
            : AnnouncedUpdates.Empty;

    /// <summary>
    /// Whether a set contains anything the last notification did not name. A set that only
    /// shrank is not news.
    /// </summary>
    /// <param name="keys">Keys for the currently eligible updates.</param>
    /// <returns><c>true</c> when at least one key is new.</returns>
    public bool HasNews(IReadOnlyList<string> keys)
    {
        if (keys.Count == 0)
        {
            return false;
        }

        HashSet<string> announced = Load().Announced.ToHashSet(StringComparer.Ordinal);
        return keys.Any(key => !announced.Contains(key));
    }

    /// <summary>Records the keys a notification just announced.</summary>
    /// <param name="keys">Keys for the announced updates.</param>
    public void Record(IReadOnlyList<string> keys)
    {
        try
        {
            JsonFile.Write(
                paths.NotificationState,
                new AnnouncedUpdates([.. keys], clock.GetUtcNow())
            );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Losing the record costs one repeated notification, never a missed one.
        }
    }
}
