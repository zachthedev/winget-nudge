using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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
    /// <param name="directory">Directory about to be written to.</param>
    /// <exception cref="IOException">The directory or an ancestor redirects elsewhere.</exception>
    public static void EnsureNotReparsePoint(string directory)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? current = Path.GetFullPath(directory);
        while (current is not null && !current.Equals(profile, StringComparison.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(current))
            {
                throw new IOException($"'{current}' {ReparsePointCause}; refusing to write through it.");
            }

            if (Path.GetPathRoot(current) is string root && root.Equals(current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
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
            if ((File.GetAttributes(handle) & FileAttributes.ReparsePoint) != 0)
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

    private static IOException ReparseRefusal(string path) =>
        new($"'{path}' {ReparsePointCause}; refusing to open through it.");

    // The exception types FileStream raises for the same errors, which every caller already catches.
    private static Exception OpenFailure(string path, int error)
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
