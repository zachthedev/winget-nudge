namespace WingetNudge.Core.Storage;

/// <summary>
/// Opens a lock file for one handle alone. The open handle is the whole lock: Windows refuses every
/// other open until it closes, and closes it when its process dies, so a crash frees the lock the
/// same way a clean exit does.
/// </summary>
internal static class ExclusiveFile
{
    /// <summary>ERROR_SHARING_VIOLATION, as .NET carries it on an <see cref="IOException"/>.</summary>
    internal const int SharingViolation = unchecked((int)0x80070020);

    /// <summary>ERROR_LOCK_VIOLATION, as .NET carries it on an <see cref="IOException"/>.</summary>
    internal const int LockViolation = unchecked((int)0x80070021);

    /// <summary>Opens the file for this handle alone, creating it when missing.</summary>
    /// <remarks>
    /// A sharing or lock violation is another handle holding the file, which is an answer rather
    /// than an error. Every other failure is a path or a directory the open cannot use, so it
    /// reaches the caller as thrown. The name opens relative to the verified directory, and a link at
    /// the name is refused rather than followed, so the elevated upgrade window never creates a file
    /// outside the data directory. The handle shares nothing, so while it is open nothing deletes or
    /// renames the file, and the directory holding it can neither move nor become a link.
    /// </remarks>
    /// <param name="directory">The directory the lock file sits in.</param>
    /// <param name="name">The lock file's name.</param>
    /// <returns>The handle, or <c>null</c> when another handle holds the file.</returns>
    /// <exception cref="IOException">
    /// The file or the directory is a reparse point, or the file cannot be opened for any other reason.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The file system denies the open.</exception>
    internal static FileStream? TryOpen(SafeDirectory directory, string name)
    {
        try
        {
            return directory.OpenFile(name, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception) when (exception.HResult is SharingViolation or LockViolation)
        {
            return null;
        }
    }
}
