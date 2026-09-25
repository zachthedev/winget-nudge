using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WingetNudge.Core.Tests.Support;

/// <summary>
/// Directory junctions, the link any process running as the user can plant. A junction needs no
/// privilege, where a symbolic link needs Developer Mode or SeCreateSymbolicLinkPrivilege. .NET has
/// no API that creates one, so this sets the reparse point itself.
/// </summary>
public static partial class Junction
{
    private const uint MountPointTag = 0xA0000003;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint ShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;

    /// <summary>
    /// Creates an empty directory at <paramref name="link"/> and makes it a junction to
    /// <paramref name="target"/>, which need not exist.
    /// </summary>
    /// <remarks>
    /// A junction already at <paramref name="link"/> is retargeted without a word. A refused call leaves the empty
    /// directory it made at <paramref name="link"/>. The open asks to write attributes alone and shares everything,
    /// as another process would: no share mode governs that access, so the call converts an empty directory the code
    /// under test holds open.
    /// </remarks>
    /// <param name="link">The junction's path.</param>
    /// <param name="target">The directory the junction redirects to.</param>
    /// <exception cref="IOException">
    /// The directory could not be opened or made a junction. The Win32 error is the exception's HResult.
    /// </exception>
    public static void Create(string link, string target)
    {
        // A REPARSE_DATA_BUFFER with its mount point member: the tag, the data length and a
        // reserved word, then the offset and length of each name, then the two names, each ending
        // in a null. The substitute name is the NT path the junction resolves. The print name is
        // what a listing shows.
        string full = Path.GetFullPath(target);
        byte[] substitute = Encoding.Unicode.GetBytes(@"\??\" + full);
        byte[] print = Encoding.Unicode.GetBytes(full);
        int dataLength = 8 + substitute.Length + 2 + print.Length + 2;
        byte[] buffer = new byte[8 + dataLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0), MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), (ushort)dataLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), (ushort)substitute.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), (ushort)(substitute.Length + 2));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), (ushort)print.Length);
        substitute.CopyTo(buffer, 16);
        print.CopyTo(buffer, 16 + substitute.Length + 2);

        Directory.CreateDirectory(link);
        using SafeFileHandle handle = CreateFile(
            link,
            FileWriteAttributes,
            ShareAll,
            IntPtr.Zero,
            OpenExisting,
            BackupSemantics | OpenReparsePoint,
            IntPtr.Zero
        );
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"'{link}' could not be opened: {Marshal.GetPInvokeErrorMessage(error)}",
                Win32HResult(error)
            );
        }

        if (!DeviceIoControl(handle, SetReparsePoint, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
        {
            int error = Marshal.GetLastPInvokeError();
            throw new IOException(
                $"'{link}' could not be made a junction to '{full}': {Marshal.GetPInvokeErrorMessage(error)}",
                Win32HResult(error)
            );
        }
    }

    // A Win32 error as .NET carries it on an IOException, so a caller tells a refusal's reason without its wording.
    private static int Win32HResult(int error) => unchecked((int)0x80070000) | (error & 0xFFFF);

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        byte[] inBuffer,
        int inBufferSize,
        IntPtr outBuffer,
        int outBufferSize,
        out int bytesReturned,
        IntPtr overlapped
    );
}
