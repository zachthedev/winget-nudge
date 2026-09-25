using System.CommandLine;
using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.Win32;
using WingetNudge.Cli;
using WingetNudge.Core.Actions;
using WingetNudge.Core.Packages;
using WingetNudge.Services;
using WinRT;

namespace WingetNudge;

/// <summary>Process entry point: notification activation, command-line verbs, then the XAML app.</summary>
public static class Program
{
    /// <summary>Window the XAML app opens, set by a verb or an activation before the app starts.</summary>
    public static StartupMode? Mode { get; private set; }

    [STAThread]
    private static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        WinRtActivation.Initialize();
        AttachParentConsole();

        // The demo inventory shows no notifications, so it leaves the computer's notification
        // registration pointing at whichever build last registered it.
        bool notifies = !AppServices.Current.IsDemo;
        if (notifies)
        {
            AppNotificationManager.Default.NotificationInvoked += OnNotificationInvoked;
            UpdateNotifier.Register();
        }

        try
        {
            return RunAsync(args).GetAwaiter().GetResult();
        }
        finally
        {
            if (notifies)
            {
                AppNotificationManager.Default.Unregister();
            }

            AppServices.Current.Dispose();
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
#if DEBUG
        // Every verb acts on the computer: register writes scheduled tasks, upgrade drives winget,
        // check shows a notification. The demo inventory is not the computer's, so a verb run
        // against it would act on the wrong thing. It opens the picker and nothing else.
        if (AppServices.Current.IsDemo && args.Length > 0)
        {
            await Console.Error.WriteLineAsync(
                $"{DemoInventory.EnvironmentVariable} is set, and the demo inventory runs the picker only."
            );
            return 2;
        }
#endif

        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (
            activation.Kind == ExtendedActivationKind.AppNotification
            && activation.Data is AppNotificationActivatedEventArgs notification
        )
        {
            Mode = ActionDispatcher.Resolve(NudgeAction.Parse(notification.Argument));
        }
        else
        {
            int code = await Commands.Build(mode => Mode = mode).Parse(args).InvokeAsync();
            if (code != 0)
            {
                return code;
            }
        }

        if (Mode is null)
        {
            return 0;
        }

        if (Mode is StartupMode.Upgrade && !ProcessIdentity.IsElevated)
        {
            return RelaunchElevated((StartupMode.Upgrade)Mode);
        }

        Application.Start(static parameters =>
        {
            DispatcherQueueSynchronizationContext context = new(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static int RelaunchElevated(StartupMode.Upgrade upgrade)
    {
        try
        {
            Launcher.StartElevatedUpgrade(upgrade.Packages);
            return 0;
        }
        catch (Win32Exception exception)
        {
            return Launcher.IsElevationDeclined(exception) ? 1 : 2;
        }
    }

    /// <summary>
    /// Fires when a notification button is clicked while this process is alive; a fresh process
    /// receives the click through activation arguments instead.
    /// </summary>
    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        NudgeAction action = NudgeAction.Parse(args.Argument);
        App? app = Application.Current as App;
        if (app is null)
        {
            return;
        }

        StartupMode? mode = ActionDispatcher.Resolve(action);
        if (mode is not null)
        {
            app.Dispatcher.TryEnqueue(() => app.Open(mode));
        }
    }

    /// <summary>
    /// Connects to the launching terminal's console so verbs can print. A GUI-subsystem process
    /// gets no console of its own.
    /// </summary>
    private static void AttachParentConsole()
    {
        if (!PInvoke.AttachConsole(PInvoke.ATTACH_PARENT_PROCESS))
        {
            return;
        }

        Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
    }
}
