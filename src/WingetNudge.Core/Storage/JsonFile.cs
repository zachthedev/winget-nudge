using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WingetNudge.Core.Storage;

/// <summary>Reads and writes the app's JSON state files with one shared serializer configuration.</summary>
public static class JsonFile
{
    private const int ReadRetries = 3;

    // A holder keeps a write lock for one read, change and write, a few milliseconds, or at most the
    // replace's retries of about 0.6 s. Two seconds covers that several times over.
    private static readonly TimeSpan DefaultLockTimeout = TimeSpan.FromSeconds(2);

    // A thread holds at most one write lock, so no two updates can wait on each other.
    [ThreadStatic]
    private static bool _holdsWriteLock;

    // Waits between attempts at a replace or a set-aside: 511 ms in all, about 0.6 s once each sleep
    // rounds up to the ~15 ms timer tick. A replace fails with access denied while any other handle is
    // open on the target, whatever its share mode. It fails with a sharing violation while a handle
    // without delete sharing is open on the temporary. A real-time scanner holds a freshly written file
    // open for a few milliseconds.
    private static readonly int[] ReplaceBackoffMilliseconds = [1, 2, 4, 8, 16, 32, 64, 128, 256];

    /// <summary>Serializer options shared by every state file.</summary>
    public static JsonSerializerOptions Options { get; } =
        new(JsonSerializerDefaults.General)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

    /// <summary>
    /// Reads a JSON file, returning <c>null</c> when it is absent or unreadable.
    /// </summary>
    /// <typeparam name="T">Deserialized shape.</typeparam>
    /// <param name="path">File to read.</param>
    /// <param name="deleteIfCorrupt">
    /// When true, a file that fails to parse is deleted so the next write starts clean. Use for
    /// state that is recreatable. When false the corrupt file is renamed aside with a unique
    /// <c>.corrupt</c> name so user data survives for repair, and the newest
    /// <see cref="CorruptCopiesKept"/> copies of each file stay.
    /// </param>
    /// <remarks>
    /// A corrupt file is set aside only under its write lock, after a second read there finds it still
    /// corrupt. Every writer of a state file holds that lock from its read to its write, so the move
    /// never takes a file that a writer replaced after the first read. When the lock cannot be had,
    /// or another handle holds the file, the file stays and the read returns <c>null</c> at once.
    /// </remarks>
    /// <returns>The parsed value, or <c>null</c> when the file is missing or corrupt.</returns>
    public static T? Read<T>(string path, bool deleteIfCorrupt)
        where T : class => ReadCore<T>(path, deleteIfCorrupt, holdsLock: false, beforeWrite: false);

    private static T? ReadCore<T>(string path, bool deleteIfCorrupt, bool holdsLock, bool beforeWrite)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                // A rename holds the file it moves with delete access until it finishes, and a read that
                // does not share delete is refused for that long.
                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete
                );
                return JsonSerializer.Deserialize<T>(stream, Options);
            }
            catch (FileNotFoundException)
            {
                // Set aside under the write lock since the check above.
                return null;
            }
            catch (JsonException) when (holdsLock)
            {
                // The write that follows would take the only copy of a file that could not move aside. A
                // holder, a denied directory and a full disk all refuse the move, so the reason given is the
                // move's own error.
                if (
                    SetAside(path, deleteIfCorrupt, beforeWrite) is Exception refused
                    && beforeWrite
                    && !deleteIfCorrupt
                )
                {
                    throw new IOException(
                        $"{Path.GetFileName(path)} is corrupt and could not be moved aside for repair, so nothing was "
                            + $"saved: {refused.Message}",
                        refused
                    );
                }

                return null;
            }
            catch (JsonException)
            {
                return RereadUnderLock(
                    path,
                    () => ReadCore<T>(path, deleteIfCorrupt, holdsLock: true, beforeWrite: false)
                );
            }
            catch (IOException) when (attempt < ReadRetries)
            {
                // Another program holds the file without sharing it; the next attempt may find it gone.
                Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>
    /// Reads a file again under its write lock, where setting a corrupt file aside is safe.
    /// </summary>
    /// <typeparam name="T">What the read returns.</typeparam>
    /// <param name="path">Data file the lock guards.</param>
    /// <param name="read">The read, which sets the file aside when it is still corrupt.</param>
    /// <returns>What the read returns, or <c>null</c> when the lock cannot be had.</returns>
    internal static T? RereadUnderLock<T>(string path, Func<T?> read)
        where T : class
    {
        // A change on this thread holds another file's lock, and a second lock would nest.
        if (_holdsWriteLock)
        {
            return null;
        }

        try
        {
            return Locked(path, read);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Serializes a value to a file, creating the parent directory when missing. The content
    /// goes to a temporary file first and replaces the target in one move, so a reader never
    /// sees a half-written file. A move that another handle refuses is retried for about 0.6 s, except
    /// over a read-only target or a directory, which fail at once. A failed write leaves the target's
    /// previous content in place.
    /// </summary>
    /// <remarks>
    /// Every caller holds the file's write lock, as <see cref="Update"/> does, so a reader's set-aside
    /// never moves what this wrote.
    /// </remarks>
    /// <typeparam name="T">Serialized shape.</typeparam>
    /// <param name="path">File to write.</param>
    /// <param name="value">Value to serialize.</param>
    /// <exception cref="IOException">
    /// The parent directory is a reparse point, or a handle without delete sharing still holds the
    /// temporary file once the retries run out.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Another handle still holds the target once the retries run out, the target is read-only or a
    /// directory, or the file system denies the write.
    /// </exception>
    public static void Write<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            SafePath.EnsureNotReparsePoint(directory);
        }

        string temporary = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, value, Options);
            }

            Replace(temporary, path);
        }
        finally
        {
            // A failed move would otherwise leave the serialized value sitting in the temporary,
            // where nothing ever collects it.
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Nothing further to try; the caller already has the original failure.
                }
            }
        }
    }

    /// <summary>Extension every half-written file carries while <see cref="Write"/> runs.</summary>
    public const string TemporarySuffix = ".tmp";

    /// <summary>
    /// Suffix of the file whose exclusive handle is a data file's write lock, as in
    /// <c>preferences.json.lock</c>.
    /// </summary>
    public const string LockSuffix = ".lock";

    /// <summary>
    /// Reads a JSON file, applies a change and writes the result, holding the file's write lock
    /// from the read to the write, so no other process writes the file in between.
    /// </summary>
    /// <remarks>
    /// The change runs under the lock, so it must not read or write a state file, and it must not
    /// call <see cref="Update"/>. Readers take the lock only to set a corrupt file aside: the write is
    /// one move, so a reader always sees a whole file.
    /// </remarks>
    /// <typeparam name="T">Serialized shape.</typeparam>
    /// <param name="path">File to update.</param>
    /// <param name="deleteIfCorrupt">How a corrupt file is set aside, as <see cref="Read"/> takes it.</param>
    /// <param name="change">
    /// Maps the current value, <c>null</c> when the file is missing or corrupt, to the value to write,
    /// or to <c>null</c> to leave the file as it is.
    /// </param>
    /// <param name="lockTimeout">How long to wait for another process's write; two seconds when omitted.</param>
    /// <returns>The value written, or the current value when the change wrote nothing.</returns>
    /// <exception cref="IOException">
    /// Another process held the write lock past the timeout, the parent directory is a reparse point, a
    /// corrupt file that keeps a repair copy could not move aside before the write, or
    /// <see cref="Write"/> failed.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException"><see cref="Write"/> failed.</exception>
    /// <exception cref="InvalidOperationException">The calling thread already holds a write lock.</exception>
    public static T? Update<T>(string path, bool deleteIfCorrupt, Func<T?, T?> change, TimeSpan? lockTimeout = null)
        where T : class =>
        Locked(
            path,
            () =>
            {
                T? current = ReadCore<T>(path, deleteIfCorrupt, holdsLock: true, beforeWrite: true);
                T? next = change(current);
                if (next is not null)
                {
                    Write(path, next);
                }

                return next ?? current;
            },
            lockTimeout
        );

    /// <summary>
    /// Runs a body under a data file's write lock, for a store whose file <see cref="Read"/> cannot
    /// parse on its own.
    /// </summary>
    /// <typeparam name="TResult">What the body returns.</typeparam>
    /// <param name="path">Data file the lock guards.</param>
    /// <param name="body">Reads, changes and writes that one file, and nothing else.</param>
    /// <param name="lockTimeout">How long to wait for another process's write; two seconds when omitted.</param>
    /// <returns>What the body returns.</returns>
    /// <exception cref="IOException">
    /// Another process held the write lock past the timeout, or the parent directory is a reparse point.
    /// </exception>
    /// <exception cref="InvalidOperationException">The calling thread already holds a write lock.</exception>
    internal static TResult Locked<TResult>(string path, Func<TResult> body, TimeSpan? lockTimeout = null)
    {
        if (_holdsWriteLock)
        {
            throw new InvalidOperationException(
                $"An update of {Path.GetFileName(path)} started inside another update. A change must not "
                    + "read or write a state file."
            );
        }

        if (Path.GetDirectoryName(path) is string directory)
        {
            Directory.CreateDirectory(directory);

            // A junction here would put the lock file, and the writes it guards, in another directory.
            SafePath.EnsureNotReparsePoint(directory);
        }

        using FileStream held = AcquireWriteLock(path, lockTimeout ?? DefaultLockTimeout);
        _holdsWriteLock = true;
        try
        {
            return body();
        }
        finally
        {
            _holdsWriteLock = false;
        }
    }

    private static FileStream AcquireWriteLock(string path, TimeSpan timeout)
    {
        string lockPath = path + LockSuffix;
        Stopwatch waited = Stopwatch.StartNew();
        while (true)
        {
            if (ExclusiveFile.TryOpen(lockPath) is FileStream held)
            {
                return held;
            }

            if (waited.Elapsed >= timeout)
            {
                throw new IOException(
                    $"{Path.GetFileName(path)} stayed busy in another Winget Nudge process for "
                        + $"{timeout.TotalSeconds:0.#} s, so the change was not saved."
                );
            }

            // Sleep(1) waits one timer tick, about 15 ms. A fixed short wait gives every waiter the same
            // chance at a lock that another process takes and drops in quick succession.
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Deletes temporary files left behind by a write that never finished, which a process kill
    /// or a power loss mid-replace produces.
    /// </summary>
    /// <remarks>
    /// Age is the test rather than the process id in the name, because a process id is reused
    /// and a live write finishes in milliseconds. Anything older than the window is abandoned.
    /// </remarks>
    /// <param name="directory">Data directory to sweep.</param>
    /// <param name="olderThan">How stale a file must be before it counts as abandoned.</param>
    /// <returns>Number of files removed.</returns>
    public static int SweepTemporaries(string directory, TimeSpan olderThan)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            // A junction here would aim these deletes at whatever directory it points to.
            SafePath.EnsureNotReparsePoint(directory);
        }
        catch (IOException)
        {
            return 0;
        }

        int removed = 0;
        DateTime cutoff = DateTime.UtcNow - olderThan;
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, $"*{TemporarySuffix}"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                    {
                        continue;
                    }

                    File.Delete(path);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Another process holds it, so it is not abandoned after all.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The directory went away or is unreadable; nothing to clean.
        }

        return removed;
    }

    /// <summary>
    /// Moves a finished temporary file over the target, retrying while another handle holds either one.
    /// </summary>
    /// <param name="temporary">The temporary file.</param>
    /// <param name="path">The target it replaces.</param>
    /// <exception cref="IOException">
    /// A handle without delete sharing still holds the temporary file once the retries run out.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Another handle still holds the target once the retries run out, the target is read-only or a
    /// directory, or the file system denies the move.
    /// </exception>
    internal static void Replace(string temporary, string path) =>
        WhileHeld(path, () => File.Move(temporary, path, overwrite: true));

    // Retries a move or delete of the file while another handle refuses it.
    private static void WhileHeld(string path, Action act)
    {
        foreach (int delay in ReplaceBackoffMilliseconds)
        {
            try
            {
                act();
                return;
            }
            catch (Exception exception)
                when (exception is UnauthorizedAccessException || exception.HResult == ExclusiveFile.SharingViolation)
            {
                if (exception is UnauthorizedAccessException && RefusesEveryReplace(path))
                {
                    throw;
                }

                Thread.Sleep(delay);
            }
        }

        act();
    }

    // A read-only target or a directory in its place refuses every attempt, so waiting only delays the
    // error. An ACL denial reads the same as a held target from here, so it keeps the full wait.
    private static bool RefusesEveryReplace(string path)
    {
        try
        {
            return (File.GetAttributes(path) & (FileAttributes.ReadOnly | FileAttributes.Directory)) != 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A target that vanished since the move, or whose attributes cannot be read, proves nothing
            // permanent.
            return false;
        }
    }

    /// <summary>
    /// Deletes a corrupt file, or moves it to a <c>.corrupt</c> name no other copy holds and drops
    /// the oldest copies past <see cref="CorruptCopiesKept"/>. The caller holds the file's write lock.
    /// </summary>
    /// <param name="path">The corrupt file.</param>
    /// <param name="delete">Whether the file is recreatable, so it goes rather than moving aside.</param>
    /// <param name="beforeWrite">
    /// Whether the caller replaces the file next. Only then does a move wait out another handle, since
    /// the write would take the only copy. A reader tries once and leaves the file to the next writer.
    /// </param>
    /// <returns>The error that kept the file at the path, or <c>null</c> when it no longer stands there.</returns>
    internal static Exception? SetAside(string path, bool delete, bool beforeWrite)
    {
        try
        {
            // A junction in the path would aim this delete or rename at another directory.
            if (Path.GetDirectoryName(path) is string directory)
            {
                SafePath.EnsureNotReparsePoint(directory);
            }

            if (delete)
            {
                File.Delete(path);
                return null;
            }

            DateTime madeAt = DateTime.UtcNow;
            string aside =
                $"{path}.{madeAt.ToString(StampFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}{CorruptSuffix}";
            if (beforeWrite)
            {
                WhileHeld(path, () => File.Move(path, aside, overwrite: false));
            }
            else
            {
                File.Move(path, aside, overwrite: false);
            }

            DropOldCopies(path, aside, madeAt);
            return null;
        }
        catch (FileNotFoundException)
        {
            // Gone already, so no write can take it.
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file stays; the next read reports it again.
            return exception;
        }
    }

    /// <summary>Repair copies of one state file that stay: the newest, and the two before it.</summary>
    internal const int CorruptCopiesKept = 3;

    private const string CorruptSuffix = ".corrupt";

    // A copy's name carries the time it was made to the tick, so copies order by age.
    private const string StampFormat = "yyyyMMddHHmmssfffffff";

    // The name earlier builds gave a copy: the time it was made, to the second.
    private const string SecondsStampFormat = "yyyyMMddHHmmss";

    // Only copies this app named count, ordered by the time in the name. A hand-named copy never goes
    // and never takes a slot. A copy stamped after the one just made came from a clock that ran ahead,
    // or this clock now runs behind. It may be the newest real copy, so it stays and takes no slot.
    private static void DropOldCopies(string path, string made, DateTime madeAt)
    {
        if (Path.GetDirectoryName(path) is not string directory)
        {
            return;
        }

        string prefix = Path.GetFileName(path) + ".";
        string[] older;
        try
        {
            older =
            [
                .. Directory
                    .EnumerateFiles(directory, prefix + "*" + CorruptSuffix)
                    .Where(copy => !string.Equals(copy, made, StringComparison.OrdinalIgnoreCase))
                    .Select(copy => (Path: copy, Stamp: MadeAt(Path.GetFileName(copy), prefix)))
                    .Where(copy => copy.Stamp <= madeAt)
                    .OrderByDescending(static copy => copy.Stamp)
                    .Skip(CorruptCopiesKept - 1)
                    .Select(static copy => copy.Path),
            ];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A copy that stays costs disk space only; the next set-aside tries it again.
            return;
        }

        foreach (string copy in older)
        {
            try
            {
                File.Delete(copy);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // As above: the next set-aside tries it again.
            }
        }
    }

    // When this app made the copy, or null for a name it did not give: the stamp and a GUID, or the
    // stamp to the second alone.
    private static DateTime? MadeAt(string name, string prefix)
    {
        if (
            !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(CorruptSuffix, StringComparison.OrdinalIgnoreCase)
        )
        {
            return null;
        }

        string middle = name[prefix.Length..^CorruptSuffix.Length];
        int dash = middle.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0 && !Guid.TryParseExact(middle[(dash + 1)..], "N", out _))
        {
            return null;
        }

        return DateTime.TryParseExact(
            dash >= 0 ? middle[..dash] : middle,
            dash >= 0 ? StampFormat : SecondsStampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTime stamp
        )
            ? stamp
            : null;
    }
}
