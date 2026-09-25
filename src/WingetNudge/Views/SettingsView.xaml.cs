using System.Globalization;
using Humanizer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tools;
using WingetNudge.Services;

namespace WingetNudge.Views;

/// <summary>
/// The settings page, shown in place of the list rather than in a window of its own. Every
/// change saves as it is made and re-registers the scheduled tasks, so there is no Save button
/// to forget.
/// </summary>
public sealed partial class SettingsView : UserControl, IDisposable
{
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private bool _loading = true;
    private CancellationTokenSource? _pending;
    private bool _savePending;
    private bool _scheduleChanged;
    private int _toolCount;

    /// <summary>
    /// Creates the view and fills it from the saved settings, or says in its message bar why it could not.
    /// </summary>
    public SettingsView()
    {
        InitializeComponent();

        FrequencyBox.ItemsSource = new[] { "Never", "Every day", "Every week" };
        CheckDayBox.ItemsSource = Enum.GetValues<DayOfWeek>().Select(static day => day.ToString()).ToArray();

        // The tool count feeds a summary line, so it has to be in hand before the form fills.
        LoadTools();
        try
        {
            Load(AppServices.Current.Settings);
            _loading = false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another program can hold settings.json past the read's wait. The form then stays unfilled, and a
            // save would write the controls' defaults over the saved settings, so the page stays loading and
            // saves nothing. Opening Settings again builds a new page and reads the file again.
            MessageBar.Severity = InfoBarSeverity.Error;
            MessageBar.Message = $"Could not read settings, so changes here are not saved: {exception.Message}";
            MessageBar.IsOpen = true;
        }
    }

    /// <summary>
    /// Raised once a save lands that changes what the picker would show, such as the cooldown
    /// or the set of registered tools. A visit that changes nothing raises nothing, so leaving
    /// settings never costs a reload.
    /// </summary>
    public event EventHandler? ListAffected;

    /// <summary>
    /// Saves anything still waiting on the save delay, then stops the timer. The host calls this
    /// as the page leaves. A NumberBox commits its value when it loses focus, which the click
    /// that leaves the page causes, so an edit is often still queued at this point.
    /// </summary>
    public void Dispose()
    {
        bool flush = _savePending;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
        if (flush)
        {
            Apply();
        }
    }

    // ///// Tools /////

    private void LoadTools()
    {
        Dictionary<string, ToolDefinition> tools = AppServices.Current.Tools.Load();
        _toolCount = tools.Count;
        ToolList.ItemsSource = tools
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new ToolRow(pair.Key, pair.Value, EditTool, RemoveTool))
            .ToList();
    }

    private async void OnAddToolClick(object sender, RoutedEventArgs args) => await EditAsync(null, null);

    private async void EditTool(string id, ToolDefinition definition) => await EditAsync(id, definition);

    private async Task EditAsync(string? id, ToolDefinition? definition)
    {
        ToolEditorDialog dialog = new(id, definition) { XamlRoot = XamlRoot };
        await dialog.ShowAsync();
        if (dialog.SavedId is not null)
        {
            AppServices.Current.ReloadSettings();
            LoadTools();
            UpdateSummaries(AppServices.Current.Settings);
            ListAffected?.Invoke(this, EventArgs.Empty);
        }
    }

    private async void RemoveTool(string id)
    {
        ContentDialog confirm = new()
        {
            XamlRoot = XamlRoot,
            Title = $"Remove {id}?",
            Content =
                "Its version command, endpoint, both patterns and upgrade command are deleted. "
                + "This cannot be undone.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            AppServices.Current.Tools.Unregister(id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MessageBar.Severity = InfoBarSeverity.Error;
            MessageBar.Message = $"Could not remove {id}: {exception.Message}";
            MessageBar.IsOpen = true;
            return;
        }

        LoadTools();
        UpdateSummaries(AppServices.Current.Settings);
        ListAffected?.Invoke(this, EventArgs.Empty);
    }

    // ///// Reading and writing the form /////

    private void Load(Settings settings)
    {
        FrequencyBox.SelectedIndex = (int)settings.Frequency;
        CheckDayBox.SelectedIndex = (int)settings.CheckDay;
        CheckTimePicker.SelectedTime = new TimeSpan(settings.CheckHour, settings.CheckMinute, 0);
        LogonCheck.IsChecked = settings.CheckAtLogon;
        UnlockCheck.IsChecked = settings.CheckAtUnlock;
        LogonDelayBox.Value = settings.LogonDelayMinutes;
        BackgroundHoursBox.Value = settings.BackgroundCheckHours;
        NotifyEveryToggle.IsOn = settings.NotifyOnNewUpdates;
        CooldownBox.Value = settings.CooldownHours;
        FailedExpiryBox.Value = settings.FailedExpiryDays;
        ToolCacheBox.Value = settings.ToolCacheHours;
        // The stored token is encrypted; showing a placeholder keeps it out of the UI tree.
        TokenBox.PlaceholderText = settings.HasGitHubToken ? "Saved" : "Not set";
        LogRetentionBox.Value = settings.LogRetentionDays;
        InstallerLogsBox.Value = settings.InstallerLogsPerPackage;
        UpdateSummaries(settings);
    }

    private Settings Read()
    {
        Settings current = AppServices.Current.Settings;
        TimeSpan time = CheckTimePicker.SelectedTime ?? new TimeSpan(current.CheckHour, 0, 0);
        return new Settings
        {
            Frequency =
                FrequencyBox.SelectedIndex >= 0 ? (CheckFrequency)FrequencyBox.SelectedIndex : current.Frequency,
            CheckDay = CheckDayBox.SelectedIndex >= 0 ? (DayOfWeek)CheckDayBox.SelectedIndex : current.CheckDay,
            CheckHour = time.Hours,
            CheckMinute = time.Minutes,
            CheckAtLogon = LogonCheck.IsChecked == true,
            CheckAtUnlock = UnlockCheck.IsChecked == true,
            LogonDelayMinutes = Whole(LogonDelayBox.Value, current.LogonDelayMinutes),
            BackgroundCheckHours = Whole(BackgroundHoursBox.Value, current.BackgroundCheckHours),
            NotifyOnNewUpdates = NotifyEveryToggle.IsOn,
            CooldownHours = Whole(CooldownBox.Value, current.CooldownHours),
            FailedExpiryDays = Whole(FailedExpiryBox.Value, current.FailedExpiryDays),
            ToolCacheHours = Whole(ToolCacheBox.Value, current.ToolCacheHours),
            LogRetentionDays = Whole(LogRetentionBox.Value, current.LogRetentionDays),
            InstallerLogsPerPackage = Whole(InstallerLogsBox.Value, current.InstallerLogsPerPackage),
            ProtectedGitHubToken = current.ProtectedGitHubToken,
        };
    }

    /// <summary>A NumberBox reports NaN while its text is empty or mid-edit.</summary>
    /// <param name="value">Value the box reports.</param>
    /// <param name="fallback">Value to keep when the box has none.</param>
    /// <returns>A whole number.</returns>
    private static int Whole(double value, int fallback) => double.IsNaN(value) ? fallback : (int)value;

    private void UpdateSummaries(Settings settings)
    {
        SchedulePanel.Visibility =
            settings.Frequency == CheckFrequency.Never ? Visibility.Collapsed : Visibility.Visible;

        List<string> events = [];
        string time = new DateTime(2000, 1, 1, settings.CheckHour, settings.CheckMinute, 0).ToString(
            "h:mm tt",
            CultureInfo.CurrentCulture
        );
        switch (settings.Frequency)
        {
            case CheckFrequency.Weekly:
                events.Add($"{settings.CheckDay}s at {time}");
                break;
            case CheckFrequency.Daily:
                events.Add($"daily at {time}");
                break;
            case CheckFrequency.Never:
                break;
            default:
                throw new System.Diagnostics.UnreachableException();
        }

        if (settings.CheckAtLogon)
        {
            events.Add("after sign-in");
        }

        if (settings.CheckAtUnlock)
        {
            events.Add("after unlocking");
        }

        TriggerSummary.Text = events.Count == 0 ? "Never announces on its own" : events.Humanize();
        BackgroundSummary.Text =
            settings.BackgroundCheckHours == 0
                ? "Off"
                : $"Every {"hour".ToQuantity(settings.BackgroundCheckHours)}, "
                    + (settings.NotifyOnNewUpdates ? "notifies on anything new" : "silent");
        OfferSummary.Text =
            $"Waits {"hour".ToQuantity(settings.CooldownHours)}, retries a failure after "
            + $"{"day".ToQuantity(settings.FailedExpiryDays)}";
        ToolSummary.Text =
            $"{"tool".ToQuantity(_toolCount)}, re-checked every " + $"{"hour".ToQuantity(settings.ToolCacheHours)}";
        HistorySummary.Text =
            $"Log kept {"day".ToQuantity(settings.LogRetentionDays)}, "
            + $"{"installer log".ToQuantity(settings.InstallerLogsPerPackage)} per package";
    }

    // ///// Change handling /////

    private void OnFrequencyChanged(object sender, SelectionChangedEventArgs args) => Schedule(reregister: true);

    private void OnTimeChanged(object sender, TimePickerSelectedValueChangedEventArgs args) =>
        Schedule(reregister: true);

    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        Schedule(reregister: ReferenceEquals(sender, LogonDelayBox) || ReferenceEquals(sender, BackgroundHoursBox));

    private void OnToggled(object sender, RoutedEventArgs args) => Schedule(reregister: false);

    private void OnSettingChanged(object sender, RoutedEventArgs args) =>
        Schedule(
            reregister: ReferenceEquals(sender, LogonCheck)
                || ReferenceEquals(sender, UnlockCheck)
                || ReferenceEquals(sender, CheckDayBox)
        );

    /// <summary>
    /// Queues a save. A PasswordChanged fires per character, and re-registering a scheduled
    /// task deletes it before rebuilding, so the writes are coalesced rather than run per stroke.
    /// </summary>
    /// <param name="reregister">Whether the change alters when a check runs.</param>
    private async void Schedule(bool reregister)
    {
        if (_loading)
        {
            return;
        }

        _scheduleChanged |= reregister;
        _savePending = true;
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = new CancellationTokenSource();
        CancellationToken token = _pending.Token;
        try
        {
            await Task.Delay(SaveDelay, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            Apply();
        }
    }

    private void Apply()
    {
        _savePending = false;
        if (_loading)
        {
            return;
        }

        AppServices services = AppServices.Current;
        Settings previous = services.Settings;
        Settings settings = Read();
        if (TokenBox.Password.Length > 0)
        {
            settings = settings.WithGitHubToken(TokenBox.Password);
        }

        if (settings == previous)
        {
            return;
        }

        try
        {
            settings.Save(services.Paths);
            services.ReloadSettings();
            // The demo inventory is not this machine's, so its settings schedule nothing.
            if (_scheduleChanged && !services.IsDemo)
            {
                services.Registrar.RegisterScheduledTask(services.Settings);
                services.Registrar.RegisterBackgroundTask(services.Settings);
                _scheduleChanged = false;
            }

            if (TokenBox.Password.Length > 0)
            {
                TokenBox.Password = "";
                TokenBox.PlaceholderText = "Saved";
            }

            MessageBar.IsOpen = false;
            UpdateSummaries(services.Settings);
            if (AffectsList(previous, services.Settings))
            {
                ListAffected?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            )
        {
            MessageBar.Severity = InfoBarSeverity.Error;
            MessageBar.Message = $"Could not save: {exception.Message}";
            MessageBar.IsOpen = true;
        }
    }

    /// <summary>
    /// Whether a save changes which section a package lands in, or where its notes come from.
    /// A schedule edit changes neither, so the list behind this page is left alone.
    /// </summary>
    /// <param name="before">Settings as they were.</param>
    /// <param name="after">Settings as saved.</param>
    /// <returns><c>true</c> when the list has to be rebuilt.</returns>
    private static bool AffectsList(Settings before, Settings after) =>
        before.CooldownHours != after.CooldownHours
        || before.FailedExpiryDays != after.FailedExpiryDays
        || before.ToolCacheHours != after.ToolCacheHours
        || before.ProtectedGitHubToken != after.ProtectedGitHubToken;
}
