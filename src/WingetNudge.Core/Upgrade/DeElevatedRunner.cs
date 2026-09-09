using System.Text.RegularExpressions;
using Microsoft.Win32.TaskScheduler;
using WingetNudge.Core.Packages;

namespace WingetNudge.Core.Upgrade;

/// <summary>Runs a winget upgrade at the user's normal integrity level.</summary>
public interface IDeElevatedUpgrader
{
    /// <summary>Upgrades one package without elevation.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Winget's exit code, where zero means the upgrade landed.</returns>
    /// <exception cref="Exception">The de-elevation mechanism refused.</exception>
    Task<long> UpgradeAsync(string packageId, CancellationToken cancellationToken);
}

/// <summary>
/// Runs a winget upgrade at the user's normal integrity level from an elevated process, for
/// installers that refuse to run elevated. A one-shot scheduled task is the only route that
/// drops elevation reliably.
/// </summary>
public sealed partial class DeElevatedRunner : IDeElevatedUpgrader
{
    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NonAlphanumeric();

    /// <summary>Runs <c>winget upgrade --force</c> for one package without elevation.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The task's last result, which is winget's exit code.</returns>
    /// <exception cref="Exception">Task Scheduler refused the task.</exception>
    public async System.Threading.Tasks.Task<long> UpgradeAsync(
        string packageId,
        CancellationToken cancellationToken
    )
    {
        PackageIdValidator.Ensure(packageId);
        string taskName = $"WingetNudge-DeElev-{NonAlphanumeric().Replace(packageId, "")}";
        string arguments =
            $"upgrade --id {packageId} --force --accept-source-agreements --accept-package-agreements";

        using TaskService service = new();
        TaskDefinition definition = service.NewTask();
        definition.Actions.Add(new ExecAction(WingetExecutable(), arguments));
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = TaskRunLevel.LUA;
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;

        using Microsoft.Win32.TaskScheduler.Task task = service.RootFolder.RegisterTaskDefinition(
            taskName,
            definition
        );
        try
        {
            task.Run();
            await System
                .Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                .ConfigureAwait(false);
            while (task.State == TaskState.Running)
            {
                await System
                    .Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2), cancellationToken)
                    .ConfigureAwait(false);
            }

            return task.LastTaskResult;
        }
        finally
        {
            service.RootFolder.DeleteTask(taskName, exceptionOnNotExists: false);
        }
    }

    /// <summary>Path of the user's winget app-execution alias, or the bare name for PATH lookup.</summary>
    /// <returns>Executable to launch.</returns>
    public static string WingetExecutable()
    {
        string alias = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps",
            "winget.exe"
        );
        return File.Exists(alias) ? alias : "winget.exe";
    }
}
