using System.ComponentModel;
using WingetNudge.Core.Actions;
using WingetNudge.Core.Packages;

namespace WingetNudge.Services;

/// <summary>Turns a notification action into a window or a process launch.</summary>
public static class ActionDispatcher
{
    /// <summary>
    /// Resolves the eligible packages and starts the elevated upgrade for all of them.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Number of packages handed to the upgrade, or 0 when nothing was eligible.</returns>
    /// <exception cref="Win32Exception">The user declined the elevation prompt.</exception>
    public static async Task<int> UpgradeAllAsync(CancellationToken cancellationToken)
    {
        AppServices services = AppServices.Current;
        IReadOnlyList<PackageInfo> all = await services.Winget.GetInstalledAsync(cancellationToken);
        PackagePartition partition = await services.UpdateCheck.PartitionAsync(
            all,
            cancellationToken
        );
        if (partition.Normal.Count == 0)
        {
            return 0;
        }

        PackageRef[] packages = partition
            .Normal.Select(static candidate => candidate.Ref)
            .ToArray();
        Launcher.StartElevatedUpgrade(packages);
        return packages.Length;
    }

    /// <summary>Maps a parsed notification action to the window to open.</summary>
    /// <param name="action">Parsed notification action.</param>
    /// <returns>The window to open, or <c>null</c> when the action is unknown.</returns>
    public static StartupMode? Resolve(NudgeAction action) =>
        action switch
        {
            NudgeAction.OpenPicker => new StartupMode.Picker(),
            NudgeAction.Unknown => null,
            _ => throw new System.Diagnostics.UnreachableException(),
        };
}
