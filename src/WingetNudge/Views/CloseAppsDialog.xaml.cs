using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WingetNudge.Core.Upgrade;

namespace WingetNudge.Views;

/// <summary>
/// Lists the apps holding a package's files and polls until they exit or the user decides.
/// </summary>
public sealed partial class CloseAppsDialog : ContentDialog
{
    private readonly IReadOnlyList<string> _processNames;

    // Rows leave one at a time; replacing the source would replay the list's entrance animation
    // on every poll.
    private readonly ObservableCollection<string> _running;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _closedOnTheirOwn;

    /// <summary>Creates the dialog.</summary>
    /// <param name="packageId">Package about to upgrade.</param>
    /// <param name="processNames">Blocking process names.</param>
    public CloseAppsDialog(string packageId, IReadOnlyList<string> processNames)
    {
        InitializeComponent();
        _processNames = processNames;
        _running = [.. processNames];
        HeaderText.Text =
            $"The following applications should be closed before upgrading {packageId}:";
        ProcessList.ItemsSource = _running;
        _timer.Tick += OnTick;
    }

    /// <summary>Shows the dialog and maps the result to an engine decision.</summary>
    /// <returns>The user's decision, or <see cref="CloseAppsDecision.AlreadyClosed"/> when the apps exited.</returns>
    public async Task<CloseAppsDecision> ShowForDecisionAsync()
    {
        _timer.Start();
        ContentDialogResult result = await ShowAsync();
        _timer.Stop();

        // A button the user pressed outranks a poll that landed in the same instant.
        return result switch
        {
            ContentDialogResult.Primary => CloseAppsDecision.Close,
            ContentDialogResult.Secondary => CloseAppsDecision.Skip,
            _ => _closedOnTheirOwn ? CloseAppsDecision.AlreadyClosed : CloseAppsDecision.Skip,
        };
    }

    private void OnTick(object? sender, object args)
    {
        HashSet<string> still = new(
            BlockingProcessDetector.StillRunning(_processNames),
            StringComparer.OrdinalIgnoreCase
        );
        for (int index = _running.Count - 1; index >= 0; index--)
        {
            if (!still.Contains(_running[index]))
            {
                _running.RemoveAt(index);
            }
        }

        if (_running.Count > 0)
        {
            return;
        }

        StatusText.Text = "All applications closed.";
        _closedOnTheirOwn = true;
        _timer.Stop();
        Hide();
    }
}
