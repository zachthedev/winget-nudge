using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Wdk.Storage.FileSystem;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace WingetNudge.Core.Storage;

/// <summary>
/// Guards against reparse points in directories the app writes to. The elevated upgrade
/// process writes under a profile directory that any process running as the user can replace
/// with a junction before the first write.
/// </summary>
public static class SafePath
{
    /// <summary>
    /// The words every refusal of a reparse point carries, which tell a caller the link is the cause rather than a
    /// denied open.
    /// </summary>
    internal const string ReparsePointCause = "is a reparse point";

    // ERROR_DIRECTORY, as OpenFailure carries it for a walk component that is a file.
    private const int NotADirectory = unchecked((int)0x8007010B);

    // Every walk handle refuses a delete, so while it is open nothing renames or deletes the component it names.
    private const FILE_SHARE_MODE HeldShare = FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE;

    /// <summary>
    /// Runs after a walk verifies one component and before it opens the next, when set, and receives the verified
    /// component's full path. It does not run for the directory the walk ends at.
    /// </summary>
    /// <remarks>
    /// A test sets it to act as another process in the middle of the walk, where a component that could be renamed
    /// would leave the path naming another directory than the handles do. The value flows only with the context that
    /// set it, so tests running in parallel never see each other's.
    /// </remarks>
    internal static AsyncLocal<Action<string>?> BetweenSteps { get; } = new();

    /// <summary>
    /// Runs after <see cref="OpenDirectory"/> verifies a directory and before anything opens relative to it, when set,
    /// and receives the directory's full path.
    /// </summary>
    /// <remarks>
    /// A test sets it to act as another process at the one moment a check by path would pass and a path-based open
    /// would then follow a link. The value flows only with the context that set it, so tests running in parallel never
    /// see each other's.
    /// </remarks>
    internal static AsyncLocal<Action<string>?> BetweenCheckAndOpen { get; } = new();

    /// <summary>Whether the path exists and is a symbolic link, junction or other reparse point.</summary>
    /// <param name="path">File or directory.</param>
    /// <returns><c>true</c> when the entry redirects elsewhere.</returns>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Throws when the directory, or any ancestor of it below the user's profile, is a reparse
    /// point.
    /// </summary>
    /// <remarks>
    /// The check is <see cref="OpenDirectory"/>'s walk, closed at once. It ends without a refusal at a component that
    /// is missing or is a plain file, and refuses one that is a reparse point of any kind. A path used after it resolves
    /// anew, so a file that has to land in the directory opens through <see cref="OpenDirectory"/> instead.
    /// </remarks>
    /// <param name="directory">Directory about to be written to.</param>
    /// <exception cref="IOException">The directory or an ancestor redirects elsewhere, or cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">The file system denies opening a component.</exception>
    public static void EnsureNotReparsePoint(string directory)
    {
        try
        {
            Walk(directory, create: false).Dispose();
        }
        catch (IOException exception)
            when (exception is DirectoryNotFoundException || exception.HResult == NotADirectory)
        {
            // Nothing below a missing component or a file can redirect a write.
        }
    }

    /// <summary>
    /// Opens a directory by handle one component at a time below the user's profile, and refuses a component that is a
    /// symbolic link, junction or other reparse point.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The profile opens by path, as the user's own. Each component below it opens relative to the handle above it,
    /// never through a link, so the walk verifies the directory the final handle names. A directory outside the profile
    /// walks down from its volume root.
    /// </para>
    /// <para>
    /// Every handle the walk opens, from the start down, shares reading and writing but not deleting, and stays open
    /// until the returned directory is disposed. From the first step on, nothing renames or deletes a component of the
    /// path, so the path names the directory the handles verified. A file opened through
    /// <see cref="SafeDirectory.OpenFile"/> lands in that directory.
    /// </para>
    /// </remarks>
    /// <param name="directory">The directory.</param>
    /// <param name="create">Whether to create a missing component under its verified parent.</param>
    /// <returns>The open directory, which the caller disposes.</returns>
    /// <exception cref="IOException">
    /// A component is a reparse point or a file, or cannot be opened. The Win32 error is the exception's HResult.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// A component is missing and <paramref name="create"/> is <c>false</c>.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">The file system denies opening or creating a component.</exception>
    internal static SafeDirectory OpenDirectory(string directory, bool create)
    {
        SafeDirectory opened = Walk(directory, create);
        if (BetweenCheckAndOpen.Value is Action<string> hook)
        {
            try
            {
                hook(opened.Path);
            }
            catch
            {
                opened.Dispose();
                throw;
            }
        }

        return opened;
    }

    private static SafeDirectory Walk(string directory, bool create)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string profile = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        );
        string start =
            profile.Length > 0
            && (
                full.Equals(profile, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            )
                ? profile
                : Path.GetPathRoot(full)
                    ?? throw new ArgumentException($"'{directory}' has no root.", nameof(directory));
        string[] components = full[start.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);

        List<SafeFileHandle> chain = [OpenStart(start)];
        try
        {
            string reached = start;
            for (int index = 0; index < components.Length; index++)
            {
                string parent = reached;
                reached = Path.Combine(reached, components[index]);
                SafeFileHandle next =
                    SafeDirectory.OpenRelative(
                        chain[^1],
                        components[index],
                        SafeDirectory.DirectoryAccess,
                        HeldShare,
                        create
                            ? NTCREATEFILE_CREATE_DISPOSITION.FILE_OPEN_IF
                            : NTCREATEFILE_CREATE_DISPOSITION.FILE_OPEN,
                        NTCREATEFILE_CREATE_OPTIONS.FILE_DIRECTORY_FILE,
                        out WIN32_ERROR error
                    )
                    ?? throw error switch
                    {
                        // The step above became a link after its own check, and the open refused to resolve through it.
                        WIN32_ERROR.ERROR_CANT_RESOLVE_FILENAME => ReparseRefusal(parent),
                        WIN32_ERROR.ERROR_FILE_NOT_FOUND => OpenFailure(reached, (int)WIN32_ERROR.ERROR_PATH_NOT_FOUND),

                        // A file symbolic link opens as the file it is, since the open never follows it, so a link to a
                        // directory fails the directory open rather than reading as a reparse point.
                        WIN32_ERROR.ERROR_DIRECTORY when IsReparsePoint(reached) => WalkRefusal(reached),
                        _ => OpenFailure(reached, (int)error),
                    };
                chain.Add(next);
                if (IsReparsePoint(next))
                {
                    throw WalkRefusal(reached);
                }

                if (index < components.Length - 1)
                {
                    BetweenSteps.Value?.Invoke(reached);
                }
            }

            return new SafeDirectory(chain, full);
        }
        catch
        {
            SafeDirectory.Close(chain);
            throw;
        }
    }

    private static IOException WalkRefusal(string path) =>
        new($"'{path}' {ReparsePointCause}; refusing to write through it.");

    // The walk's trust starts at the profile or the volume root, so the start opens by path and follows a link.
    private static SafeFileHandle OpenStart(string start)
    {
        SafeFileHandle handle = PInvoke.CreateFile(
            start,
            (uint)SafeDirectory.DirectoryAccess,
            HeldShare,
            null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
            null
        );
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw OpenFailure(start, error);
        }

        return handle;
    }

    /// <summary>
    /// Opens a file without following a link at the path, and refuses the file when it is itself a
    /// symbolic link, junction or other reparse point.
    /// </summary>
    /// <remarks>
    /// The open never resolves a link, so a link planted at the path neither creates nor reads the file
    /// it names. The check reads the handle this call opened, so nothing swapped in after it gets past.
    /// </remarks>
    /// <param name="path">The file.</param>
    /// <param name="mode"><see cref="FileMode.Open"/> or <see cref="FileMode.OpenOrCreate"/>.</param>
    /// <param name="access">What the handle may do.</param>
    /// <param name="share">What other handles may do while this one is open.</param>
    /// <returns>The open file.</returns>
    /// <exception cref="IOException">
    /// The file is a reparse point, or the open failed. The Win32 error is the exception's HResult.
    /// </exception>
    /// <exception cref="FileNotFoundException">The mode is <see cref="FileMode.Open"/> and the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The file system denies the open.</exception>
    internal static FileStream OpenFile(string path, FileMode mode, FileAccess access, FileShare share)
    {
        FILE_CREATION_DISPOSITION disposition = mode switch
        {
            FileMode.Open => FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FileMode.OpenOrCreate => FILE_CREATION_DISPOSITION.OPEN_ALWAYS,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only Open and OpenOrCreate are supported."),
        };
        GENERIC_ACCESS_RIGHTS rights = access switch
        {
            FileAccess.Read => GENERIC_ACCESS_RIGHTS.GENERIC_READ,
            FileAccess.Write => GENERIC_ACCESS_RIGHTS.GENERIC_WRITE,
            _ => GENERIC_ACCESS_RIGHTS.GENERIC_READ | GENERIC_ACCESS_RIGHTS.GENERIC_WRITE,
        };
        SafeFileHandle handle = PInvoke.CreateFile(
            Path.GetFullPath(path),
            (uint)rights,
            (FILE_SHARE_MODE)(uint)share,
            null,
            disposition,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT,
            null
        );
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();

            // A directory link or a junction refuses a file open with access denied, which names the
            // wrong cause in diagnostics.log.
            if ((WIN32_ERROR)error == WIN32_ERROR.ERROR_ACCESS_DENIED && IsReparsePoint(path))
            {
                throw ReparseRefusal(path);
            }

            throw OpenFailure(path, error);
        }

        try
        {
            if (IsReparsePoint(handle))
            {
                throw ReparseRefusal(path);
            }

            return new FileStream(handle, access);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>Whether an open handle names a symbolic link, junction or other reparse point.</summary>
    /// <param name="handle">A handle opened with read-attributes access.</param>
    /// <returns><c>true</c> when the entry redirects elsewhere.</returns>
    internal static bool IsReparsePoint(SafeFileHandle handle) =>
        (File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0;

    /// <summary>The refusal of a file or directory that is a reparse point, found at an open.</summary>
    /// <param name="path">The entry that is the link.</param>
    /// <returns>The exception to throw.</returns>
    internal static IOException ReparseRefusal(string path) =>
        new($"'{path}' {ReparsePointCause}; refusing to open through it.");

    /// <summary>
    /// A failed open as the exception type FileStream raises for the same error, which every caller already catches.
    /// </summary>
    /// <param name="path">What the open named.</param>
    /// <param name="error">The Win32 error.</param>
    /// <returns>The exception to throw.</returns>
    internal static Exception OpenFailure(string path, int error)
    {
        string message = $"'{path}' could not be opened: {Marshal.GetPInvokeErrorMessage(error)}";
        return (WIN32_ERROR)error switch
        {
            WIN32_ERROR.ERROR_ACCESS_DENIED => new UnauthorizedAccessException(message),
            WIN32_ERROR.ERROR_FILE_NOT_FOUND => new FileNotFoundException(message, path),
            WIN32_ERROR.ERROR_PATH_NOT_FOUND => new DirectoryNotFoundException(message),
            _ => new IOException(message, unchecked((int)0x80070000) | (error & 0xFFFF)),
        };
    }
}
