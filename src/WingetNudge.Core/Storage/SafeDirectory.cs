using Microsoft.Win32.SafeHandles;
using Windows.Wdk.Foundation;
using Windows.Wdk.Storage.FileSystem;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Ntdll = Windows.Wdk.PInvoke;
using Win32 = Windows.Win32.PInvoke;

namespace WingetNudge.Core.Storage;

/// <summary>
/// A directory held open by the handles <see cref="SafePath.OpenDirectory"/> verified, and the files opened relative to
/// the last of them.
/// </summary>
/// <remarks>
/// A name opens inside the directory the handle names, whatever its path names by then. A directory that became a link
/// after the walk refuses every relative open, so nothing opens in the link's target. The walk's handles on every
/// component above stay open with it, so none of them can be renamed or deleted until this instance is disposed.
/// </remarks>
/// <param name="chain">
/// The walk's handles, from its start down to this directory, none sharing delete. This instance owns them.
/// </param>
/// <param name="path">The directory's full path, for messages and listings.</param>
internal sealed class SafeDirectory(IReadOnlyList<SafeFileHandle> chain, string path) : IDisposable
{
    private readonly IReadOnlyList<SafeFileHandle> _chain = chain;
    private readonly SafeFileHandle _handle = chain[^1];

    /// <summary>
    /// What every walk handle asks for. Listing and traversing take part in sharing, so each handle's share mode stops
    /// another handle deleting or renaming the component it names.
    /// </summary>
    internal const FILE_ACCESS_RIGHTS DirectoryAccess =
        FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY
        | FILE_ACCESS_RIGHTS.FILE_TRAVERSE
        | FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES
        | FILE_ACCESS_RIGHTS.SYNCHRONIZE;

    /// <summary>The directory's full path.</summary>
    public string Path { get; } = path;

    /// <summary>
    /// Opens a file in the directory relative to its handle, without following a link at the name, and refuses the file
    /// when it is itself a symbolic link, junction or other reparse point.
    /// </summary>
    /// <param name="name">One file name, with no directory in it.</param>
    /// <param name="mode">
    /// <see cref="FileMode.Open"/>, <see cref="FileMode.OpenOrCreate"/> or <see cref="FileMode.Create"/>.
    /// </param>
    /// <param name="access">What the handle may do.</param>
    /// <param name="share">What other handles may do while this one is open.</param>
    /// <returns>The open file.</returns>
    /// <exception cref="ArgumentException">The name is empty, a dot entry, or holds a directory.</exception>
    /// <exception cref="IOException">
    /// The file or the directory is a reparse point, or the open failed. The Win32 error is the exception's HResult.
    /// </exception>
    /// <exception cref="FileNotFoundException">The mode is <see cref="FileMode.Open"/> and the file is missing.</exception>
    /// <exception cref="UnauthorizedAccessException">The file system denies the open.</exception>
    public FileStream OpenFile(string name, FileMode mode, FileAccess access, FileShare share)
    {
        // A separator would open through subdirectories the walk never verified.
        if (name.Length == 0 || name is "." or ".." || System.IO.Path.GetFileName(name) != name)
        {
            throw new ArgumentException($"'{name}' is not a single file name.", nameof(name));
        }

        NTCREATEFILE_CREATE_DISPOSITION disposition = mode switch
        {
            FileMode.Open => NTCREATEFILE_CREATE_DISPOSITION.FILE_OPEN,
            FileMode.OpenOrCreate => NTCREATEFILE_CREATE_DISPOSITION.FILE_OPEN_IF,
            FileMode.Create => NTCREATEFILE_CREATE_DISPOSITION.FILE_OVERWRITE_IF,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "Only Open, OpenOrCreate and Create are supported."
            ),
        };
        FILE_ACCESS_RIGHTS rights = access switch
        {
            FileAccess.Read => FILE_ACCESS_RIGHTS.FILE_GENERIC_READ,
            FileAccess.Write => FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE,
            _ => FILE_ACCESS_RIGHTS.FILE_GENERIC_READ | FILE_ACCESS_RIGHTS.FILE_GENERIC_WRITE,
        };
        string file = System.IO.Path.Combine(Path, name);
        if (
            OpenRelative(
                _handle,
                name,
                rights,
                (FILE_SHARE_MODE)(uint)share,
                disposition,
                NTCREATEFILE_CREATE_OPTIONS.FILE_NON_DIRECTORY_FILE,
                out WIN32_ERROR error
            )
            is not SafeFileHandle opened
        )
        {
            throw error switch
            {
                // The directory became a link after the walk, and the open refused to resolve through it.
                WIN32_ERROR.ERROR_CANT_RESOLVE_FILENAME => SafePath.ReparseRefusal(Path),

                // A directory link at the name refuses a file open with access denied, which names the wrong cause
                // in diagnostics.log.
                WIN32_ERROR.ERROR_ACCESS_DENIED when SafePath.IsReparsePoint(file) => SafePath.ReparseRefusal(file),
                _ => SafePath.OpenFailure(file, (int)error),
            };
        }

        try
        {
            if (SafePath.IsReparsePoint(opened))
            {
                throw SafePath.ReparseRefusal(file);
            }

            return new FileStream(opened, access);
        }
        catch
        {
            opened.Dispose();
            throw;
        }
    }

    /// <summary>Lists the files in the directory whose names match a pattern.</summary>
    /// <remarks>
    /// The listing reads the path, which follows a link the directory became after the walk. The handle then reads the
    /// directory itself, and a directory that is a link by then refuses the listing. One made a link and restored
    /// within the one listing can still list names from the link's target. Every file opens relative to the handle,
    /// so no content follows those names.
    /// </remarks>
    /// <param name="pattern">The names to list, as <see cref="DirectoryInfo.GetFiles(string)"/> takes them.</param>
    /// <returns>The files.</returns>
    /// <exception cref="IOException">The directory became a reparse point, or could not be listed.</exception>
    /// <exception cref="UnauthorizedAccessException">The file system denies the listing.</exception>
    public FileInfo[] GetFiles(string pattern)
    {
        FileInfo[] files = new DirectoryInfo(Path).GetFiles(pattern);
        return SafePath.IsReparsePoint(_handle) ? throw SafePath.ReparseRefusal(Path) : files;
    }

    /// <summary>Closes the directory's handle and every handle the walk held above it.</summary>
    public void Dispose() => Close(_chain);

    /// <summary>Closes a walk's handles, the deepest first.</summary>
    /// <param name="chain">The handles, from the walk's start down.</param>
    internal static void Close(IReadOnlyList<SafeFileHandle> chain)
    {
        for (int index = chain.Count - 1; index >= 0; index--)
        {
            chain[index].Dispose();
        }
    }

    /// <summary>
    /// Opens one name relative to a directory handle, never following a link at the name.
    /// </summary>
    /// <param name="directory">The directory the name resolves in.</param>
    /// <param name="name">One path component.</param>
    /// <param name="access">What the new handle may do.</param>
    /// <param name="share">What other handles may do while the new one is open.</param>
    /// <param name="disposition">Whether to open, create or overwrite.</param>
    /// <param name="options">Whether the name has to be a directory or a file.</param>
    /// <param name="error">The Win32 error when the open failed.</param>
    /// <returns>The new handle, or <c>null</c> when the open failed.</returns>
    internal static unsafe SafeFileHandle? OpenRelative(
        SafeFileHandle directory,
        string name,
        FILE_ACCESS_RIGHTS access,
        FILE_SHARE_MODE share,
        NTCREATEFILE_CREATE_DISPOSITION disposition,
        NTCREATEFILE_CREATE_OPTIONS options,
        out WIN32_ERROR error
    )
    {
        fixed (char* characters = name)
        {
            ushort length = checked((ushort)(name.Length * sizeof(char)));
            UNICODE_STRING objectName = new()
            {
                Length = length,
                MaximumLength = length,
                Buffer = characters,
            };
            OBJECT_ATTRIBUTES attributes = new()
            {
                Length = (uint)sizeof(OBJECT_ATTRIBUTES),
                RootDirectory = new HANDLE(directory.DangerousGetHandle()),
                ObjectName = &objectName,

                // Win32 names match whatever their case, and CreateFile asks for the same.
                Attributes = OBJECT_ATTRIBUTE_FLAGS.OBJ_CASE_INSENSITIVE,
            };
            NTSTATUS status = Ntdll.NtCreateFile(
                out HANDLE opened,
                access | FILE_ACCESS_RIGHTS.SYNCHRONIZE | FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES,
                in attributes,
                out _,
                null,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
                share,
                disposition,
                // Synchronous, as FileStream expects of a handle it did not open.
                options
                    | NTCREATEFILE_CREATE_OPTIONS.FILE_OPEN_REPARSE_POINT
                    | NTCREATEFILE_CREATE_OPTIONS.FILE_SYNCHRONOUS_IO_NONALERT
            );
            if (status.Value < 0)
            {
                error = (WIN32_ERROR)Win32.RtlNtStatusToDosError(status);
                return null;
            }

            error = WIN32_ERROR.NO_ERROR;
            return new SafeFileHandle((IntPtr)opened.Value, ownsHandle: true);
        }
    }
}
