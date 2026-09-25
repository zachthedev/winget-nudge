using System.ComponentModel;
using System.Diagnostics;
using WingetNudge.Core.Packages;

namespace WingetNudge.Services;

/// <summary>
/// Starts other processes: this app elevated, and shells for tool upgrades. On the demo
/// inventory every method returns without starting anything, because nothing the demo lists is
/// what the computer has installed.
/// </summary>
public static class Launcher
{
    /// <summary>Verb that opens the upgrade window.</summary>
    public const string UpgradeVerb = "upgrade";

    /// <summary>Verb that opens the picker.</summary>
    public const string PickerVerb = "picker";

    /// <summary>
    /// Starts an elevated instance that upgrades the given packages.
    /// </summary>
    /// <param name="packages">Packages to upgrade.</param>
    /// <exception cref="Win32Exception">The user declined the elevation prompt.</exception>
    /// <exception cref="ArgumentException">A package id is malformed.</exception>
    public static void StartElevatedUpgrade(IReadOnlyList<PackageRef> packages)
    {
        if (AppServices.Current.IsDemo)
        {
            return;
        }

        ProcessStartInfo startInfo = new(AppServices.Current.ExecutablePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = AppServices.Current.AppDirectory,
        };
        startInfo.ArgumentList.Add(UpgradeVerb);
        foreach (PackageRef package in packages)
        {
            PackageIdValidator.Ensure(package.Id);
            startInfo.ArgumentList.Add("--id");
            startInfo.ArgumentList.Add(package.Id);
            startInfo.ArgumentList.Add("--name");
            startInfo.ArgumentList.Add(PackageIdValidator.SanitizeName(package.Name));
        }

        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>Starts a non-elevated instance of this app with the given verb.</summary>
    /// <param name="verb">Command-line verb.</param>
    public static void StartSelf(string verb)
    {
        if (AppServices.Current.IsDemo)
        {
            return;
        }

        ProcessStartInfo startInfo = new(AppServices.Current.ExecutablePath)
        {
            UseShellExecute = false,
            WorkingDirectory = AppServices.Current.AppDirectory,
        };
        startInfo.ArgumentList.Add(verb);
        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>Win32 error when the user dismisses the elevation prompt.</summary>
    public const int ElevationDeclinedError = 1223;

    /// <summary>Whether an exception from a <c>runas</c> launch means the user declined.</summary>
    /// <param name="exception">Exception from <see cref="Process.Start(ProcessStartInfo)"/>.</param>
    /// <returns><c>true</c> for the cancellation code; other codes are real launch failures.</returns>
    public static bool IsElevationDeclined(Win32Exception exception) =>
        exception.NativeErrorCode == ElevationDeclinedError;

    /// <summary>
    /// Opens a file with its default handler at the user's normal integrity level. The upgrade
    /// window runs elevated, so a direct shell execute would open the handler as admin; handing
    /// the path to the running desktop shell instead keeps it out of that context.
    /// </summary>
    /// <param name="path">Full path of an existing file.</param>
    /// <exception cref="Win32Exception">The shell could not be started.</exception>
    public static void OpenFileDeElevated(string path)
    {
        if (AppServices.Current.IsDemo || path.Length == 0 || !File.Exists(path))
        {
            return;
        }

        // The shell resolves the handler from the extension, so only this app's own logs go there.
        string root = Path.GetFullPath(AppServices.Current.Paths.InstallerLogDirectory);
        string full = Path.GetFullPath(path);
        if (
            !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !full.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
        )
        {
            return;
        }

        ProcessStartInfo startInfo = new(ExplorerPath()) { UseShellExecute = false };
        startInfo.ArgumentList.Add(path);
        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>Opens a PowerShell window that runs a command and stays open.</summary>
    /// <param name="command">Command text.</param>
    public static void StartTerminalCommand(string command)
    {
        if (AppServices.Current.IsDemo)
        {
            return;
        }

        ProcessStartInfo startInfo = new(PowerShellPath())
        {
            UseShellExecute = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        startInfo.ArgumentList.Add("-NoExit");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(command);
        Process.Start(startInfo)?.Dispose();
    }

    /// <summary>The desktop shell, resolved from the Windows directory rather than PATH.</summary>
    /// <returns>Full path of <c>explorer.exe</c>.</returns>
    private static string ExplorerPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// The installed PowerShell 7 executable, so the launch never searches the working
    /// directory for a bare name.
    /// </summary>
    /// <returns>An absolute path when found, else the bare name for PATH lookup.</returns>
    public static string PowerShellPath()
    {
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string candidate = Path.Combine(programs, "PowerShell", "7", "pwsh.exe");
        return File.Exists(candidate) ? candidate : "pwsh.exe";
    }
}
