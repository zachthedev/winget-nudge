using System.Security.Principal;

namespace WingetNudge.Core.Packages;

/// <summary>Facts about the account the current process runs as.</summary>
public static class ProcessIdentity
{
    /// <summary>Whether the current process holds the administrator role.</summary>
    public static bool IsElevated
    {
        get
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }
}
