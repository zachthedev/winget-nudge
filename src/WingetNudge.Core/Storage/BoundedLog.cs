using System.Globalization;
using System.Text;

namespace WingetNudge.Core.Storage;

/// <summary>
/// A text log of timestamped entries, bounded by age and by size. An entry starts on a line that
/// opens with its round-trip timestamp, and every line after it up to the next such line belongs to it.
/// </summary>
internal static class BoundedLog
{
    /// <summary>
    /// Largest a log grows before its oldest entries go, whatever their age. An app stuck in a crash
    /// loop writes many entries a day, so the age window alone does not bound the file.
    /// </summary>
    internal const int MaxBytes = 1024 * 1024;

    /// <summary>
    /// Appends an entry, then drops the entries past the retention window and the oldest past the size
    /// cap. The newest entry always stays.
    /// </summary>
    /// <remarks>
    /// A failure to write is swallowed: a log that cannot be written has nowhere left to report to,
    /// and it must not fail the operation it records. A log that is itself a link is refused that way,
    /// without reading the file it names.
    /// </remarks>
    /// <param name="paths">Data file locations.</param>
    /// <param name="path">The log file.</param>
    /// <param name="clock">Time source for the timestamp and the window.</param>
    /// <param name="text">The entry after its timestamp. It may span lines.</param>
    /// <param name="retentionDays">Days an entry stays, or <c>null</c> to read the user's setting.</param>
    /// <param name="maxBytes">Size cap for the whole file.</param>
    /// <returns><c>true</c> when the entry was written.</returns>
    internal static bool Append(
        DataPaths paths,
        string path,
        TimeProvider clock,
        string text,
        int? retentionDays = null,
        int maxBytes = MaxBytes
    )
    {
        try
        {
            int days = retentionDays ?? Settings.Load(paths).LogRetentionDays;
            DateTimeOffset now = clock.GetUtcNow();
            string entry = string.Create(
                CultureInfo.InvariantCulture,
                $"{now:o} {text.ReplaceLineEndings(Environment.NewLine)}"
            );
            return JsonFile.Locked(path, () => Rewrite(path, now.AddDays(-days), entry, maxBytes));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool Rewrite(string path, DateTimeOffset cutoff, string entry, int maxBytes)
    {
        List<string> kept = [.. Entries(path).Where(existing => existing.Stamp > cutoff).Select(static e => e.Text)];
        kept.Add(entry);
        long total = kept.Sum(Size);
        int first = 0;
        while (total > maxBytes && first < kept.Count - 1)
        {
            total -= Size(kept[first]);
            first++;
        }

        string temporary = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}{JsonFile.TemporarySuffix}";
        try
        {
            File.WriteAllLines(temporary, kept.Skip(first));
            JsonFile.Replace(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return true;
    }

    private static long Size(string entry) => Encoding.UTF8.GetByteCount(entry) + Environment.NewLine.Length;

    // Lines before the first timestamp cannot be aged, so they go.
    private static List<(DateTimeOffset Stamp, string Text)> Entries(string path)
    {
        List<(DateTimeOffset Stamp, string Text)> entries = [];
        FileStream stream;
        try
        {
            // A link planted at the log would copy the file it names into a log any user process reads.
            stream = SafePath.OpenFile(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            return entries;
        }

        using StreamReader reader = new(stream);
        DateTimeOffset? stamp = null;
        StringBuilder text = new();
        while (reader.ReadLine() is string line)
        {
            if (StampOf(line) is DateTimeOffset next)
            {
                if (stamp is DateTimeOffset previous)
                {
                    entries.Add((previous, text.ToString()));
                }

                stamp = next;
                text.Clear().Append(line);
            }
            else if (stamp is not null)
            {
                text.Append(Environment.NewLine).Append(line);
            }
        }

        if (stamp is DateTimeOffset last)
        {
            entries.Add((last, text.ToString()));
        }

        return entries;
    }

    // Only the exact round-trip form opens an entry. A lenient parse reads a line of an exception
    // message that starts with "10:00" or "1/2" as a new entry, which the window then ages on its own.
    private static DateTimeOffset? StampOf(string line)
    {
        int space = line.IndexOf(' ', StringComparison.Ordinal);
        return
            space > 0
            && DateTimeOffset.TryParseExact(
                line.AsSpan(0, space),
                "o",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset stamp
            )
            ? stamp
            : null;
    }
}
