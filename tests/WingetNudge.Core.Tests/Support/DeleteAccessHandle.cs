using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WingetNudge.Core.Tests.Support;

/// <summary>
/// A handle on a directory with delete access and full sharing, as another program can hold one. While it is open,
/// every open of that directory that refuses to share delete meets a sharing violation.
/// </summary>
public static partial class DeleteAccessHandle
{
    private const uint Delete = 0x00010000;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;

    /// <summary>Opens an existing directory with delete access, sharing everything.</summary>
    /// <param name="directory">The directory.</param>
    /// <returns>The handle, which the caller disposes.</returns>
    /// <exception cref="IOException">
    /// The directory could not be opened. The Win32 error is the exception's HResult.
    /// </exception>
    public static SafeFileHandle Open(string directory)
    {
        SafeFileHandle handle = CreateFile(
            directory,
            Delete,
            ShareAll,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            throw new IOException(
                $"'{directory}' could not be opened: {Marshal.GetPInvokeErrorMessage(error)}",
                unchecked((int)0x80070000) | (error & 0xFFFF)
            );
        }

        return handle;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16
    )]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );
}
