using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Preferences;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Upgrade;
using WingetNudge.Services;

namespace WingetNudge.Views;

/// <summary>Runs the upgrade engine and shows per-package progress. Runs elevated.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A Window has no dispose seam; the token source is disposed when the window closes."
)]
public sealed partial class UpgradeWindow : Window, IUpgradeInteraction
{
    private readonly IReadOnlyList<PackageRef> _packages;
    private readonly ObservableCollection<UpgradeItem> _items = [];
    private readonly Dictionary<string, UpgradeItem> _byId = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _cancellation = new();
    private bool _started;
    private bool _finished;

    /// <summary>Creates the window for a set of packages.</summary>
    /// <param name="packages">Packages to upgrade, in order.</param>
    public UpgradeWindow(IReadOnlyList<PackageRef> packages)
    {
        InitializeComponent();
        _packages = packages;
        WindowChrome.Apply(this, 620, 560, resizable: true, title: "Upgrading");

        foreach (PackageRef package in packages)
        {
            UpgradeItem item = new(package);
            _items.Add(item);
            _byId[package.Id] = item;
        }

        PackageList.ItemsSource = _items;
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
        };
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_started)
        {
            return;
        }

        _started = true;

        // Two scheduled tasks and a manual launch can all reach this window, and two winget
        // upgrades running together fight over the same installers and the same outcome log.
        RunLockAttempt attempt = RunLock.Acquire(AppServices.Current.Paths, RunLock.Upgrade);
        if (attempt is RunLockAttempt.Unavailable unavailable)
        {
            PhaseText.Text = "Cannot start";
            SummaryText.Text = unavailable.Reason;
            FinishUi();
            return;
        }

        if (attempt is not RunLockAttempt.Taken taken)
        {
            PhaseText.Text = "Another upgrade is already running";
            SummaryText.Text = "nothing was installed; close this and try again when it finishes";
            FinishUi();
            return;
        }

        using RunLock run = taken.Lock;

        UpgradeEngine engine = AppServices.Current.CreateUpgradeEngine(this);
        UpgradeSummary summary;
        try
        {
            summary = await Task.Run(() => engine.RunAsync(_packages, _cancellation.Token));
        }
        catch (OperationCanceledException)
        {
            // The token reaches winget's install operation, so a cancel mid-download lands here
            // rather than returning a summary.
            PhaseText.Text = "Canceled";
            SummaryText.Text = "stopped before the current package finished";
            FinishUi();
            return;
        }
        catch (Exception exception)
        {
            PhaseText.Text = "Upgrade failed";
            SummaryText.Text = exception.Message;
            FinishUi();
            return;
        }

        if (summary.AbortReason is string reason)
        {
            PhaseText.Text = "Stopped";
            SummaryText.Text = reason;
            FinishUi();
            return;
        }

        PhaseText.Text = summary.Canceled ? "Canceled" : "Done";
        SummaryText.Text = $"{summary.Upgraded} upgraded, {summary.Skipped} skipped, {summary.Failed} failed";
        FinishUi();
    }

    private void FinishUi()
    {
        _finished = true;
        PhaseBar.IsIndeterminate = false;
        PhaseBar.Value = 100;
        ActionButton.Content = "Close";
    }

    private void OnActionClick(object sender, RoutedEventArgs args)
    {
        if (_finished)
        {
            Close();
            return;
        }

        _cancellation.Cancel();
        ActionButton.IsEnabled = false;
        PhaseText.Text = "Canceling after the current package";
    }

    private void OnMuteClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: UpgradeItem item })
        {
            try
            {
                AppServices.Current.Preferences.Set(item.Id, PreferenceState.Muted, "muted after a failed upgrade");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The link stays live, so the user can try again once whatever holds the file lets go.
                item.Status = $"{item.Status}\nCould not mute: {exception.Message}";
                return;
            }

            item.Muted();
        }
    }

    private void OnLogClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: UpgradeItem item })
        {
            Launcher.OpenFileDeElevated(item.LogPath);
        }
    }

    private void OnNotesClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: UpgradeItem item })
        {
            DetailTitle.Text = item.Name;
            DetailHost.Content = new ReleaseNotesView(item.Changelog);
            DetailPage.Visibility = Visibility.Visible;
            ProgressPage.Visibility = Visibility.Collapsed;
            BackButton.Focus(FocusState.Programmatic);
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs args)
    {
        DetailPage.Visibility = Visibility.Collapsed;
        ProgressPage.Visibility = Visibility.Visible;
        // The browser is worth dropping rather than leaving parked behind the progress list.
        DetailHost.Content = null;
    }

    // ///// IUpgradeInteraction /////

    /// <inheritdoc/>
    public Task<CloseAppsDecision> AskCloseAppsAsync(
        string packageId,
        IReadOnlyList<string> processNames,
        CancellationToken cancellationToken
    )
    {
        TaskCompletionSource<CloseAppsDecision> completion = new();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                CloseAppsDialog dialog = new(packageId, processNames) { XamlRoot = Content.XamlRoot };
                completion.SetResult(await dialog.ShowForDecisionAsync());
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        return completion.Task;
    }

    /// <inheritdoc/>
    public void Report(UpgradeEvent upgradeEvent)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            switch (upgradeEvent)
            {
                case UpgradeEvent.Phase phase:
                    PhaseText.Text = phase.Text;
                    break;
                case UpgradeEvent.Changelogs changelogs:
                    foreach (UpgradeItem item in _items)
                    {
                        changelogs.Notes.TryGetValue(item.Id, out string? notes);
                        item.SetChangelog(notes);
                    }

                    break;
                case UpgradeEvent.Started started:
                    PhaseText.Text = $"Upgrading {started.Package.Name}";
                    if (_byId.TryGetValue(started.Package.Id, out UpgradeItem? startedItem))
                    {
                        startedItem.Start();
                        PackageList.ScrollIntoView(startedItem);
                    }

                    break;
                case UpgradeEvent.Progress progress:
                    if (_byId.TryGetValue(progress.PackageId, out UpgradeItem? progressItem))
                    {
                        progressItem.Apply(progress.Snapshot);
                    }

                    break;
                case UpgradeEvent.Message message:
                    if (_byId.TryGetValue(message.PackageId, out UpgradeItem? messageItem))
                    {
                        messageItem.Status = message.Text;
                    }

                    break;
                case UpgradeEvent.Finished finished:
                    if (_byId.TryGetValue(finished.PackageId, out UpgradeItem? finishedItem))
                    {
                        finishedItem.Finish(finished.Result, finished.Detail, finished.LogPath);
                    }

                    break;
                default:
                    throw new System.Diagnostics.UnreachableException();
            }
        });
    }
}
