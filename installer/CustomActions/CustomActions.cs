using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using WixToolset.Dtf.WindowsInstaller;

namespace WingetNudge.CustomActions;

/// <summary>Custom actions the Winget Nudge MSI runs.</summary>
public static class CustomActions
{
    private const int ErrorInvalidData = 13;

    // KUSER_SHARED_DATA, which the kernel maps read-only at this address in every process, and the
    // offsets of its NtBuildNumber and NtMajorVersion fields.
    private const long SharedUserData = 0x7FFE0000;
    private const long NtBuildNumberOffset = 0x260;
    private const long NtMajorVersionOffset = 0x26C;

    /// <summary>
    /// Sets <c>WINDOWSAPPRUNTIMEFOUND</c> to the highest version of the <c>WindowsAppRuntimePackage</c>
    /// framework registered for the installing user that targets <c>WindowsAppRuntimeArchitecture</c>
    /// and reaches <c>WindowsAppRuntimeMinVersion</c>. The launch condition refuses setup while the
    /// public property is empty.
    /// </summary>
    /// <remarks>
    /// Runs immediate, as the user who started setup, because framework packages register per user. The
    /// three inputs are private properties, which a command line cannot override. A value of
    /// <c>WINDOWSAPPRUNTIMEFOUND</c> given on the command line is left in place, so it skips the check.
    /// </remarks>
    /// <param name="session">The install session.</param>
    /// <returns>
    /// <see cref="ActionResult.Success"/> whatever it finds, including when the packaging API fails,
    /// which it logs and leaves the launch condition to refuse. <see cref="ActionResult.Failure"/> only
    /// when the package authors no runtime package, architecture or version.
    /// </returns>
    [CustomAction]
    public static ActionResult FindWindowsAppRuntime(Session session)
    {
        string packageName = session["WindowsAppRuntimePackage"];
        string architecture = session["WindowsAppRuntimeArchitecture"];
        string floorText = session["WindowsAppRuntimeMinVersion"];
        if (packageName.Length == 0 || architecture.Length == 0 || !Version.TryParse(floorText, out Version? floor))
        {
            session.Log(
                $"FindWindowsAppRuntime: the package authors package \"{packageName}\", architecture \"{architecture}\" and minimum version \"{floorText}\"; all three are required."
            );
            return ActionResult.Failure;
        }

        string family = RuntimeRequirement.FamilyName(packageName);
        string[] fullNames;
        try
        {
            fullNames = PackagesInFamily(family);
        }
        catch (Win32Exception exception)
        {
            session.Log(
                $"FindWindowsAppRuntime: GetPackagesByPackageFamily({family}) failed with {exception.NativeErrorCode}: {exception.Message}"
            );
            return ActionResult.Success;
        }

        foreach (string fullName in fullNames)
        {
            session.Log($"FindWindowsAppRuntime: registered {fullName}");
        }

        Version? found = RuntimeRequirement.HighestSatisfying(fullNames, packageName, architecture, floor);
        if (found is null)
        {
            session.Log($"FindWindowsAppRuntime: no {architecture} {family} at or above {floor}.");
            return ActionResult.Success;
        }

        session["WINDOWSAPPRUNTIMEFOUND"] = found.ToString();
        session.Log($"FindWindowsAppRuntime: {family} {found} {architecture} satisfies {floor}.");
        return ActionResult.Success;
    }

    /// <summary>
    /// Sets <c>WindowsBuildFound</c> to the build of the running Windows when it reaches
    /// <c>WindowsMinBuild</c>. The launch condition refuses setup while the property is empty.
    /// </summary>
    /// <remarks>
    /// The build comes from KUSER_SHARED_DATA, a page the kernel writes and every process reads with a plain
    /// load. No API sits in that path, so no compatibility layer or shim can lower the build it reports, and
    /// a skip would have nothing to correct. Both properties are private, so a command line can neither lower
    /// the floor nor skip the check.
    /// </remarks>
    /// <param name="session">The install session.</param>
    /// <returns>
    /// <see cref="ActionResult.Success"/> whatever build it finds, and the launch condition refuses a build
    /// below the floor. <see cref="ActionResult.Failure"/> only when the package authors no floor.
    /// </returns>
    [CustomAction]
    public static ActionResult FindWindowsBuild(Session session)
    {
        string floorText = session["WindowsMinBuild"];
        if (WindowsRequirement.ParseFloor(floorText) is not int floor)
        {
            session.Log(
                $"FindWindowsBuild: the package authors minimum build \"{floorText}\", which is not a build number."
            );
            return ActionResult.Failure;
        }

        uint major = ReadSharedUserData(NtMajorVersionOffset);
        uint build = WindowsRequirement.SharedDataBuild(major, ReadSharedUserData(NtBuildNumberOffset));
        if (!WindowsRequirement.Satisfies(build, floor))
        {
            session.Log($"FindWindowsBuild: Windows {major} build {build} is below build {floor}.");
            return ActionResult.Success;
        }

        session["WindowsBuildFound"] = build.ToString(CultureInfo.InvariantCulture);
        session.Log($"FindWindowsBuild: Windows {major} build {build} reaches build {floor}.");
        return ActionResult.Success;
    }

    /// <summary>A 32-bit field of KUSER_SHARED_DATA.</summary>
    /// <param name="offset">The field's offset in the page.</param>
    /// <returns>The field's value.</returns>
    private static unsafe uint ReadSharedUserData(long offset) => *(uint*)(SharedUserData + offset);

    /// <summary>Full names of the packages in a family, registered for the current user.</summary>
    /// <param name="family">Package family name.</param>
    /// <returns>The full names, empty when the family has none registered.</returns>
    /// <exception cref="Win32Exception">The packaging API failed.</exception>
    private static unsafe string[] PackagesInFamily(string family)
    {
        uint count = 0;
        uint length = 0;
        int result = GetPackagesByPackageFamily(family, ref count, null, ref length, null);
        RuntimeRequirement.Listing listing = RuntimeRequirement.ReadSizingCall(result, count);
        if (listing == RuntimeRequirement.Listing.Failed)
        {
            // A success that reports packages without a buffer to hold them breaks the API's contract.
            throw new Win32Exception(result == RuntimeRequirement.ErrorSuccess ? ErrorInvalidData : result);
        }

        if (listing == RuntimeRequirement.Listing.Empty)
        {
            return [];
        }

        IntPtr[] names = new IntPtr[count];
        char[] buffer = new char[length];
        fixed (IntPtr* namesPointer = names)
        fixed (char* bufferPointer = buffer)
        {
            result = GetPackagesByPackageFamily(family, ref count, namesPointer, ref length, bufferPointer);
            if (result != RuntimeRequirement.ErrorSuccess)
            {
                throw new Win32Exception(result);
            }

            string[] fullNames = new string[count];
            for (int index = 0; index < count; index++)
            {
                fullNames[index] = Marshal.PtrToStringUni(names[index]) ?? "";
            }

            return fullNames;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern unsafe int GetPackagesByPackageFamily(
        string packageFamilyName,
        ref uint count,
        IntPtr* packageFullNames,
        ref uint bufferLength,
        char* buffer
    );
}
