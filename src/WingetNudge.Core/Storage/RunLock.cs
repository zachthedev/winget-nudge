namespace WingetNudge.Core.Storage;

/// <summary>
/// Keeps one kind of run to a single process at a time, across every process the user is running.
/// </summary>
/// <remarks>
/// <para>
/// Both scheduled tasks and a manual launch can start at the same moment, and Task Scheduler's
/// multiple-instances policy applies to one task rather than across two. The exclusion therefore
/// has to live in the app.
/// </para>
/// <para>
/// An exclusively opened file in the data directory carries it rather than a named mutex, for two
/// reasons. Windows closes a process's handles when it dies, so a crash frees the lock the same
/// way a clean exit does. And a file handle has no thread affinity, so a lock taken before an
/// <c>await</c> is released correctly on whatever thread the continuation lands on; a mutex
/// released off its owning thread throws instead. The file lives under <c>%LOCALAPPDATA%</c> with
/// the rest of the app's state, which scopes the lock to one user with no <c>Local\</c> or
/// <c>Global\</c> name to choose between. The elevated upgrade window runs as the same user, so it
/// contends on the same file.
/// </para>
/// </remarks>
public sealed class RunLock : IDisposable
{
    /// <summary>Name of the lock a package upgrade holds.</summary>
    public const string Upgrade = "upgrade";

    /// <summary>Name of the lock the headless check holds.</summary>
    public const string Check = "check";

    private readonly FileStream _handle;

    private RunLock(FileStream handle) => _handle = handle;

    /// <summary>Takes the named lock when it is free.</summary>
    /// <remarks>
    /// Every state the file system can be in comes back as an answer rather than as a throw. Both
    /// call sites run where a throw is invisible: the elevated upgrade window calls this before
    /// the window has any way to report, and the check verb is what Task Scheduler runs.
    /// </remarks>
    /// <param name="paths">Data file locations.</param>
    /// <param name="name">Which run to lock, from this type's constants.</param>
    /// <returns>
    /// <see cref="RunLockAttempt.Taken"/> with the holder to dispose,
    /// <see cref="RunLockAttempt.Held"/> when another process of the same kind holds it, or
    /// <see cref="RunLockAttempt.Unavailable"/> when the file cannot be opened at all.
    /// </returns>
    /// <exception cref="ArgumentException">The name is neither run name.</exception>
    public static RunLockAttempt Acquire(DataPaths paths, string name)
    {
        // Outside the catch: a name that is not a run is a caller's mistake rather than a state of
        // the machine, and a caller cannot recover from it by standing down.
        string file = paths.RunLockFile(name);

        try
        {
            // Inside, because a junction on the data directory and a directory that cannot be
            // created both reach the caller the same way an unopenable file does. The directory
            // closes once the lock is open, which then holds it in place.
            using SafeDirectory directory = SafePath.OpenDirectory(paths.Directory, create: true);

            // A second run holding the file is the one answer that means stand down.
            return ExclusiveFile.TryOpen(directory, Path.GetFileName(file)) is FileStream handle
                ? new RunLockAttempt.Taken(new RunLock(handle))
                : new RunLockAttempt.Held();
        }
        catch (Exception exception)
            when (exception
                    is IOException
                        or UnauthorizedAccessException
                        or NotSupportedException
                        or ArgumentException
                        or System.Security.SecurityException
            )
        {
            return new RunLockAttempt.Unavailable($"the run lock {file} cannot be opened: {exception.Message}");
        }
    }

    /// <summary>Releases the lock.</summary>
    public void Dispose() => _handle.Dispose();
}

/// <summary>What an attempt to take a run lock came back with.</summary>
public abstract record RunLockAttempt
{
    private RunLockAttempt() { }

    /// <summary>The lock is this process's until the holder is disposed.</summary>
    /// <param name="Lock">The holder to dispose.</param>
    public sealed record Taken(RunLock Lock) : RunLockAttempt;

    /// <summary>Another process of the same kind holds the lock.</summary>
    public sealed record Held : RunLockAttempt;

    /// <summary>
    /// The lock file cannot be opened, so nothing says whether a run of this kind is under way.
    /// </summary>
    /// <param name="Reason">What stopped the open, in words a caller shows the user.</param>
    public sealed record Unavailable(string Reason) : RunLockAttempt;
}
