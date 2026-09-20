using System.Runtime.InteropServices;
using Microsoft.Management.Deployment;

namespace WingetNudge.Core.Packages;

/// <summary>
/// The Windows Package Manager COM server could not be reached, so no package can be queried or
/// upgraded until it is fixed.
/// </summary>
public sealed class WingetUnavailableException : Exception
{
    /// <summary>Creates the exception with no detail.</summary>
    public WingetUnavailableException()
        : base("The Windows Package Manager is unavailable.") { }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">What could not be reached and what to do about it.</param>
    public WingetUnavailableException(string message)
        : base(message) { }

    /// <summary>Creates the exception over a COM failure.</summary>
    /// <param name="message">What could not be reached and what to do about it.</param>
    /// <param name="innerException">The COM failure underneath.</param>
    public WingetUnavailableException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>
/// Creates winget COM objects by CLSID against the out-of-process server, then wraps them in
/// the CsWinRT projection. An unpackaged process has no manifest-free activation for these
/// classes, so plain construction cannot reach the server. An elevated process cannot reach
/// the packaged server through COM at all, so it goes through winget's manual activation in
/// <c>winrtact.dll</c>, which launches the server itself and marshals the object back over RPC.
/// </summary>
public static partial class WingetActivation
{
    private const uint LocalServer = 0x4;
    private static readonly bool UseManualActivation = ProcessIdentity.IsElevated;
    private static readonly Guid IUnknownIid = new("00000000-0000-0000-C000-000000000046");

    // COM's answers when the winget server is not registered, cannot launch, or died mid-call.
    private static readonly int[] UnavailableResults =
    [
        unchecked((int)0x80040154), // REGDB_E_CLASSNOTREG
        unchecked((int)0x80080005), // CO_E_SERVER_EXEC_FAILURE
        unchecked((int)0x800706BA), // RPC_S_SERVER_UNAVAILABLE
        unchecked((int)0x800706BE), // RPC_S_CALL_FAILED
        unchecked((int)0x80070490), // ERROR_NOT_FOUND
    ];

    private static readonly Guid PackageManagerClsid = new("C53A4F16-787E-42A4-B304-29EFFB4BF597");
    private static readonly Guid FindPackagesOptionsClsid = new("572DED96-9C60-4526-8F92-EE7D91D38C1A");
    private static readonly Guid CreateCompositePackageCatalogOptionsClsid = new(
        "526534B8-7E46-47C8-8416-B1685C327D37"
    );
    private static readonly Guid InstallOptionsClsid = new("1095F097-EB96-453B-B4E6-1613637F3B14");
    private static readonly Guid PackageMatchFilterClsid = new("D02C9DAF-99DC-429C-B503-4E504E4AB000");

    /// <summary>Creates the package manager.</summary>
    /// <returns>A connected package manager.</returns>
    public static PackageManager CreatePackageManager() => Create(PackageManagerClsid, PackageManager.FromAbi);

    /// <summary>Creates empty find options.</summary>
    /// <returns>Find options.</returns>
    public static FindPackagesOptions CreateFindPackagesOptions() =>
        Create(FindPackagesOptionsClsid, FindPackagesOptions.FromAbi);

    /// <summary>Creates empty composite catalog options.</summary>
    /// <returns>Composite catalog options.</returns>
    public static CreateCompositePackageCatalogOptions CreateCompositeOptions() =>
        Create(CreateCompositePackageCatalogOptionsClsid, CreateCompositePackageCatalogOptions.FromAbi);

    /// <summary>Creates empty install options.</summary>
    /// <returns>Install options.</returns>
    public static InstallOptions CreateInstallOptions() => Create(InstallOptionsClsid, InstallOptions.FromAbi);

    /// <summary>Creates an empty match filter.</summary>
    /// <returns>Match filter.</returns>
    public static PackageMatchFilter CreatePackageMatchFilter() =>
        Create(PackageMatchFilterClsid, PackageMatchFilter.FromAbi);

    private static T Create<T>(Guid clsid, Func<IntPtr, T> fromAbi)
    {
        int hr;
        IntPtr unknown;
        try
        {
            hr = UseManualActivation
                ? WinGetServerManualActivation_CreateInstance(in clsid, in IUnknownIid, 0, out unknown)
                : CoCreateInstance(in clsid, IntPtr.Zero, LocalServer, in IUnknownIid, out unknown);
        }
        catch (DllNotFoundException exception)
        {
            throw new WingetUnavailableException(
                "winrtact.dll is missing, so an elevated process cannot reach winget. "
                    + "Update App Installer from the Microsoft Store.",
                exception
            );
        }

        if (Array.IndexOf(UnavailableResults, hr) >= 0)
        {
            throw new WingetUnavailableException(
                $"The Windows Package Manager COM server could not be reached (0x{hr:X8}). "
                    + "Update App Installer from the Microsoft Store, then try again.",
                Marshal.GetExceptionForHR(hr)
            );
        }

        Marshal.ThrowExceptionForHR(hr);
        try
        {
            return fromAbi(unknown);
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out IntPtr ppv
    );

    [LibraryImport("winrtact.dll")]
    private static partial int WinGetServerManualActivation_CreateInstance(
        in Guid rclsid,
        in Guid riid,
        uint flags,
        out IntPtr ppv
    );
}
