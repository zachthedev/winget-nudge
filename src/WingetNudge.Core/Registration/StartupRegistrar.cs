using Microsoft.Win32.TaskScheduler;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;
using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Registration;

/// <summary>Scheduled check task and Start Menu shortcut.</summary>
/// <param name="executablePath">Full path of the app executable.</param>
/// <param name="displayName">Name shown in the Start Menu and Task Scheduler.</param>
/// <param name="taskPrefix">
/// Prefix both task names start with. Task Scheduler is one machine-wide namespace, so anything
/// other than the installed app passes its own prefix; the default names the real tasks.
/// </param>
public sealed class StartupRegistrar(
    string executablePath,
    string displayName,
    string taskPrefix = StartupRegistrar.DefaultTaskPrefix
)
{
    /// <summary>Prefix the installed app's task names start with.</summary>
    public const string DefaultTaskPrefix = "WingetNudge";

    /// <summary>Name of the task running the announcing check.</summary>
    public string CheckTaskName => $"{taskPrefix} Check";

    /// <summary>Name of the task running the quiet interval check.</summary>
    public string BackgroundTaskName => $"{taskPrefix} Background Check";

    /// <summary>Command-line verb the task runs.</summary>
    public const string CheckVerb = "check";

    /// <summary>Flag that keeps a check from announcing what the user already saw.</summary>
    public const string BackgroundFlag = "--background";

    /// <summary>Start Menu shortcut path for the current user.</summary>
    public string ShortcutPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft",
            "Windows",
            "Start Menu",
            "Programs",
            $"{displayName}.lnk"
        );

    /// <summary>
    /// Creates or replaces the check task from the events the user picked, under the current
    /// user at normal integrity, allowed on battery.
    /// </summary>
    /// <param name="settings">Schedule settings.</param>
    /// <returns>
    /// <c>true</c> when a task was registered, <c>false</c> when every trigger is off and the
    /// task was removed instead.
    /// </returns>
    public bool RegisterScheduledTask(Settings settings)
    {
        using TaskService service = new();
        service.RootFolder.DeleteTask(CheckTaskName, exceptionOnNotExists: false);

        TaskDefinition definition = NewDefinition(
            service,
            $"{displayName}: checks winget for package upgrades.",
            CheckVerb
        );
        foreach (Trigger trigger in BuildTriggers(settings))
        {
            definition.Triggers.Add(trigger);
        }

        if (definition.Triggers.Count == 0)
        {
            // A task with no trigger can never fire, and Task Scheduler accepts it silently.
            return false;
        }

        service.RootFolder.RegisterTaskDefinition(CheckTaskName, definition).Dispose();
        return true;
    }

    /// <summary>
    /// Creates or replaces the quiet interval task. It refreshes publish dates and the cooldown
    /// verdict between announcements, so a version that matures overnight is ready the moment
    /// the picker opens rather than at the next weekly check.
    /// </summary>
    /// <param name="settings">Schedule settings.</param>
    /// <returns>
    /// <c>true</c> when a task was registered, <c>false</c> when the interval is off and the
    /// task was removed instead.
    /// </returns>
    public bool RegisterBackgroundTask(Settings settings)
    {
        using TaskService service = new();
        service.RootFolder.DeleteTask(BackgroundTaskName, exceptionOnNotExists: false);
        if (settings.BackgroundCheckHours <= 0)
        {
            return false;
        }

        TaskDefinition definition = NewDefinition(
            service,
            $"{displayName}: quiet check that keeps release dates and the cooldown current.",
            $"{CheckVerb} {BackgroundFlag}"
        );
        definition.Triggers.Add(
            new DailyTrigger
            {
                StartBoundary = DateTime.Today,
                Repetition =
                {
                    Interval = TimeSpan.FromHours(settings.BackgroundCheckHours),
                    Duration = TimeSpan.FromDays(1),
                },
            }
        );
        service.RootFolder.RegisterTaskDefinition(BackgroundTaskName, definition).Dispose();
        return true;
    }

    /// <summary>Builds a task definition with the settings and principal both tasks share.</summary>
    /// <param name="service">Open task service.</param>
    /// <param name="description">Text shown in Task Scheduler.</param>
    /// <param name="arguments">Command line passed to the executable.</param>
    /// <returns>A definition with no triggers.</returns>
    private TaskDefinition NewDefinition(TaskService service, string description, string arguments)
    {
        TaskDefinition definition = service.NewTask();
        definition.RegistrationInfo.Description = description;
        definition.Actions.Add(new ExecAction(executablePath, arguments, Path.GetDirectoryName(executablePath)));

        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.StartWhenAvailable = true;
        definition.Settings.ExecutionTimeLimit = TimeSpan.FromHours(1);
        // A slow check must not stack a second process behind itself on the next interval.
        definition.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

        definition.Principal.UserId = Environment.UserName;
        definition.Principal.LogonType = TaskLogonType.InteractiveToken;
        definition.Principal.RunLevel = TaskRunLevel.LUA;
        return definition;
    }

    /// <summary>Builds one Task Scheduler trigger per event the user turned on.</summary>
    /// <param name="settings">Schedule settings.</param>
    /// <returns>The triggers, empty when the user turned everything off.</returns>
    public static IReadOnlyList<Trigger> BuildTriggers(Settings settings)
    {
        List<Trigger> triggers = [];
        DateTime start = DateTime.Today.AddHours(settings.CheckHour).AddMinutes(settings.CheckMinute);
        switch (settings.Frequency)
        {
            case CheckFrequency.Weekly:
                triggers.Add(new WeeklyTrigger(ToTaskDay(settings.CheckDay)) { StartBoundary = start });
                break;
            case CheckFrequency.Daily:
                triggers.Add(new DailyTrigger { StartBoundary = start });
                break;
            case CheckFrequency.Never:
                break;
            default:
                throw new System.Diagnostics.UnreachableException();
        }

        if (settings.CheckAtLogon)
        {
            triggers.Add(
                new LogonTrigger
                {
                    UserId = Environment.UserName,
                    Delay = TimeSpan.FromMinutes(settings.LogonDelayMinutes),
                }
            );
        }

        if (settings.CheckAtUnlock)
        {
            triggers.Add(
                new SessionStateChangeTrigger
                {
                    StateChange = TaskSessionStateChangeType.SessionUnlock,
                    UserId = Environment.UserName,
                    Delay = TimeSpan.FromMinutes(settings.LogonDelayMinutes),
                }
            );
        }

        return triggers;
    }

    /// <summary>Maps a weekday to the Task Scheduler flag.</summary>
    /// <param name="day">Weekday.</param>
    /// <returns>The matching flag.</returns>
    public static DaysOfTheWeek ToTaskDay(DayOfWeek day) =>
        day switch
        {
            DayOfWeek.Sunday => DaysOfTheWeek.Sunday,
            DayOfWeek.Monday => DaysOfTheWeek.Monday,
            DayOfWeek.Tuesday => DaysOfTheWeek.Tuesday,
            DayOfWeek.Wednesday => DaysOfTheWeek.Wednesday,
            DayOfWeek.Thursday => DaysOfTheWeek.Thursday,
            DayOfWeek.Friday => DaysOfTheWeek.Friday,
            DayOfWeek.Saturday => DaysOfTheWeek.Saturday,
            _ => throw new ArgumentOutOfRangeException(nameof(day)),
        };

    /// <summary>Removes both check tasks if present.</summary>
    public void UnregisterScheduledTasks()
    {
        using TaskService service = new();
        service.RootFolder.DeleteTask(CheckTaskName, exceptionOnNotExists: false);
        service.RootFolder.DeleteTask(BackgroundTaskName, exceptionOnNotExists: false);
    }

    /// <summary>Creates or replaces the Start Menu shortcut that opens the picker.</summary>
    public void CreateShortcut()
    {
        PInvoke
            .CoCreateInstance(typeof(ShellLink).GUID, null, CLSCTX.CLSCTX_INPROC_SERVER, out IShellLinkW link)
            .ThrowOnFailure();
        link.SetPath(executablePath);
        link.SetWorkingDirectory(Path.GetDirectoryName(executablePath) ?? "");
        link.SetIconLocation(executablePath, 0);
        link.SetDescription(displayName);

        IPersistFile file = (IPersistFile)link;
        file.Save(ShortcutPath, fRemember: true);
    }

    /// <summary>Deletes the Start Menu shortcut if present.</summary>
    public void DeleteShortcut()
    {
        if (File.Exists(ShortcutPath))
        {
            File.Delete(ShortcutPath);
        }
    }
}
