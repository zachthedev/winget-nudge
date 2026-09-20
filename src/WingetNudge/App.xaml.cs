using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WingetNudge.Services;
using WingetNudge.Views;

namespace WingetNudge;

/// <summary>XAML application host. Opens the window <see cref="Program.Mode"/> names.</summary>
public sealed partial class App : Application
{
    private readonly List<Window> _windows = [];

    /// <summary>Initializes the XAML application.</summary>
    public App()
    {
        InitializeComponent();
        Dispatcher = DispatcherQueue.GetForCurrentThread();
        UnhandledException += OnUnhandledException;
    }

    /// <summary>
    /// Writes the failure to <c>crash.log</c> in the data directory and keeps the process alive; a
    /// notification-launched window has no console to surface it on.
    /// </summary>
    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        try
        {
            string path = Path.Combine(AppServices.Current.Paths.Directory, "crash.log");
            Directory.CreateDirectory(AppServices.Current.Paths.Directory);
            Core.Storage.SafePath.EnsureNotReparsePoint(AppServices.Current.Paths.Directory);
            File.AppendAllText(
                path,
                $"{DateTimeOffset.Now:o} {args.Message}{Environment.NewLine}{args.Exception}{Environment.NewLine}{Environment.NewLine}"
            );
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing left to report to.
        }

        args.Handled = true;
    }

    /// <summary>UI thread dispatcher.</summary>
    public DispatcherQueue Dispatcher { get; }

    /// <inheritdoc/>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
#if DEBUG
        if (AppServices.Current.IsDemo)
        {
            DemoInventory.UseDefaultAccent(Resources);
        }
#endif
        Open(Program.Mode ?? new StartupMode.Picker());
    }

    /// <summary>Opens the window for a startup mode and keeps it alive.</summary>
    /// <param name="mode">Which window to open.</param>
    public void Open(StartupMode mode)
    {
        Window window = mode switch
        {
            StartupMode.Picker => new PickerWindow(),
            StartupMode.Upgrade upgrade => new UpgradeWindow(upgrade.Packages),
            _ => throw new System.Diagnostics.UnreachableException(),
        };
        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);
        window.Activate();
    }
}
