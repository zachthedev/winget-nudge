using System.Runtime.InteropServices;

namespace WingetNudge.Services;

/// <summary>
/// Installs winget's undocked reg-free WinRT hook (<c>winrtact.dll</c>, shipped with the WinGet
/// PowerShell module) so the winmd beside the executable serves as marshaling metadata for the
/// out-of-process winget server. Without it every interface query on a winget object fails.
/// </summary>
public static partial class WinRtActivation
{
    /// <summary>Hooks metadata resolution for this process. Call before any winget object is created.</summary>
    public static void Initialize() => winrtact_Initialize();

    [LibraryImport("winrtact.dll", EntryPoint = "winrtact_Initialize")]
    private static partial void winrtact_Initialize();
}
