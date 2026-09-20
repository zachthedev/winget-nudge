using System.ComponentModel;
using Humanizer;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Tracking;
using WingetNudge.Services;

namespace WingetNudge.Views;

/// <summary>The app's main window: every package with an upgrade, grouped by why it is or is not offered.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A Window has no dispose seam; the token source is disposed when the window closes."
)]
public sealed partial class PickerWindow : Window
{
    private readonly List<PickerItem> _items = [];
    private readonly List<ToolItem> _toolItems = [];
    private CancellationTokenSource? _loadCancellation;
    private PackageScan? _scan;
    private IReadOnlyList<ToolStatus> _tools = [];
    private SettingsView? _settings;
    private FocusOrigin? _detailOrigin;
    private bool _activatedOnce;

    /// <summary>Creates the picker and starts the inventory query.</summary>
    public PickerWindow()
    {
        InitializeComponent();
        WindowChrome.Apply(this, 680, 700, resizable: true);
        // Widths taken from what real rows run to, so the list holds still when data lands.
        SkeletonRows.ItemsSource = new List<SkeletonRow>
        {
            new(88, 118, 96, 106),
            new(36, 0, 80, 106),
            new(125, 154, 148, 106),
            new(148, 126, 100, 106),
            new(198, 40, 92, 106),
        };
        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _settings?.Dispose();
            _loadCancellation?.Cancel();
            _loadCancellation?.Dispose();
            _loadCancellation = null;
        };
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_activatedOnce)
        {
            return;
        }

        _activatedOnce = true;
        await LoadAsync();
    }

    // ///// Loading /////

    /// <summary>
    /// Fills the window in the order the data arrives: packages first, then the manual tools,
    /// then the release notes. Each stage replaces its own placeholder, so the window is usable
    /// before the slowest of the three finishes.
    /// </summary>
    private async Task LoadAsync()
    {
        _loadCancellation?.Cancel();
        _loadCancellation?.Dispose();
        _loadCancellation = new CancellationTokenSource();
        CancellationToken token = _loadCancellation.Token;

        ResetList();
        UpdateCheck check = AppServices.Current.UpdateCheck;

        // Tool probes share nothing with the winget query, so they run alongside it and land
        // in their own section whenever they are ready.
        Task<IReadOnlyList<ToolStatus>> tools = Task.Run(() => check.RunToolsAsync(token), token);
        Task<PackageScan> packages = Task.Run(() => check.RunPackagesAsync(token), token);

        PackageScan scan;
        try
        {
            scan = await packages;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
            when (exception
                    is InvalidOperationException
                        or System.Runtime.InteropServices.COMException
                        or ObjectDisposedException
            )
        {
            ShowTerminal($"Query failed: {exception.Message}");
            return;
        }

        _scan = scan;
        _tools = [];
        Render(scan.Partition);
        await FillToolsAsync(tools, token);
        await FillChangelogsAsync(scan.Partition, token);
    }

    /// <summary>
    /// Re-sections the packages already in hand. Muting or skipping moves a row between
    /// sections, and doing that without another winget query keeps it instant.
    /// </summary>
    private async void OnPreferenceChanged()
    {
        if (_scan is not PackageScan scan)
        {
            return;
        }

        Dictionary<string, bool> selection = SelectionSnapshot();

        // A settings save rebuilds UpdateCheck, so the tracking comes from the scan rather than
        // from whichever instance is current. Without it every cooling package reads as ready.
        UpdateCheck check = AppServices.Current.UpdateCheck;
        PackagePartition repartitioned;
        try
        {
            // Re-sectioning reads preferences and pins only; it must never reach the network.
            repartitioned = await Task.Run(() => check.Repartition(scan.All, scan.Tracking));
        }
        catch (Exception exception)
            when (exception is InvalidOperationException or System.Runtime.InteropServices.COMException or IOException)
        {
            ShowError($"Could not re-read preferences: {exception.Message}");
            return;
        }

        // Another re-section or a refresh may have replaced the scan while this one ran.
        if (!ReferenceEquals(_scan, scan))
        {
            return;
        }

        _scan = scan with { Partition = repartitioned };
        Render(repartitioned, selection);

        // The notes are already in hand for every package that had them, so the rows fill from
        // the cache rather than going back to GitHub.
        _ = FillChangelogsAsync(repartitioned, _loadCancellation?.Token ?? CancellationToken.None);
    }

    /// <summary>
    /// Rebuilds every section from the partition. The rebuild destroys whatever row control has
    /// focus, so where focus sat is noted first and handed back once the replacement rows exist.
    /// </summary>
    /// <param name="partition">Packages split by section.</param>
    /// <param name="selection">
    /// Checked state of rows already on screen, keyed by <see cref="ISelectableRow.Key"/>. A row
    /// the user has seen keeps its state; a row arriving for the first time keeps its default.
    /// </param>
    private void Render(PackagePartition partition, Dictionary<string, bool>? selection = null)
    {
        FocusOrigin? focus = CaptureFocus();
        Rebuild(partition, selection);
        if (focus is FocusOrigin.InRow or FocusOrigin.InList)
        {
            RestoreFocus(focus);
        }
    }

    private void Rebuild(PackagePartition partition, Dictionary<string, bool>? selection)
    {
        _items.Clear();
        _toolItems.Clear();
        Sections.Children.Clear();
        RefreshButton.IsEnabled = true;
        SummarySkeleton.Visibility = Visibility.Collapsed;
        UpdateSummary(partition);

        if (partition.Total == 0 && _tools.Count == 0)
        {
            ShowTerminal("Everything is up to date.");
            return;
        }

        SkeletonPulse.Stop();
        LoadingPanel.Visibility = Visibility.Collapsed;

        TimeProvider clock = AppServices.Current.Clock;
        AddPackageSection(
            "Ready",
            partition.Normal.Select(candidate => new PickerItem(
                candidate,
                true,
                null,
                false,
                clock,
                ShowError,
                onPreferenceChanged: OnPreferenceChanged,
                actions: RowActions.SkipAndMute
            ))
        );

        // Tools sit with what is on offer rather than after the held-back sections, since a
        // tool with an update is something to act on, not something being explained away.
        AddToolSection();

        AddPackageSection(
            "Too new",
            partition.Cooling.Select(entry => new PickerItem(
                entry.Candidate,
                false,
                $"offered in {TimeSpan.FromHours(entry.Cooling.RemainingHours).Humanize()}",
                false,
                clock,
                ShowError,
                onPreferenceChanged: OnPreferenceChanged,
                actions: RowActions.SkipAndMute,
                noteGlyph: NoteGlyphs.Cooling
            ))
        );
        AddPackageSection(
            "Skipped",
            partition.Skipped.Select(candidate => new PickerItem(
                candidate,
                false,
                $"skipped {candidate.Package.AvailableVersion}",
                false,
                clock,
                ShowError,
                onPreferenceChanged: OnPreferenceChanged,
                actions: RowActions.SkipAndMute,
                noteGlyph: NoteGlyphs.Skipped
            ))
        );
        AddPackageSection(
            "Muted",
            partition.Muted.Select(candidate => new PickerItem(
                candidate,
                false,
                "hidden from notifications",
                true,
                clock,
                ShowError,
                onPreferenceChanged: OnPreferenceChanged,
                actions: RowActions.Mute,
                noteGlyph: NoteGlyphs.Muted
            ))
        );
        AddPackageSection(
            "Previously failed",
            partition.Failed.Select(entry => new PickerItem(
                entry.Candidate,
                false,
                entry.Reason,
                false,
                clock,
                ShowError,
                onPreferenceChanged: OnPreferenceChanged,
                noteGlyph: NoteGlyphs.Failed,
                actions: RowActions.Failed,
                logPath: AppServices.Current.UpdateLog.LatestInstallerLog(entry.Candidate.Id) ?? ""
            ))
        );
        AddPackageSection(
            "Winget will not upgrade",
            partition.HeldBack.Select(held => new PickerItem(
                new UpdateCandidate(held.Package, clock.GetUtcNow(), PublishSource.FirstSeen),
                false,
                held.Reason,
                false,
                clock,
                ShowError,
                canSelect: false,
                onPreferenceChanged: OnPreferenceChanged,
                noteGlyph: NoteGlyphs.HeldBack,
                // A pin is winget's refusal, so muting adds nothing; only removing it helps.
                actions: held.IsBlocked ? RowActions.UnpinHint : RowActions.Mute
            ))
        );

        if (selection is not null)
        {
            foreach (ISelectableRow row in AllRows())
            {
                if (selection.TryGetValue(row.Key, out bool isChecked))
                {
                    row.IsChecked = isChecked;
                }
            }
        }

        UpdateSelectionState();
    }

    private async Task FillToolsAsync(Task<IReadOnlyList<ToolStatus>> probes, CancellationToken token)
    {
        try
        {
            _tools = await probes;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception exception)
        {
            ShowError($"Could not probe the manual tools: {exception.Message}");
            return;
        }

        if (token.IsCancellationRequested || _scan is not PackageScan scan)
        {
            return;
        }

        if (_tools.Count > 0)
        {
            Render(scan.Partition, SelectionSnapshot());
        }
        else
        {
            UpdateSummary(scan.Partition);
        }
    }

    private async Task FillChangelogsAsync(PackagePartition partition, CancellationToken token)
    {
        PackageInfo[] packages = _items
            .Select(item => item.Ref)
            .Join(
                AllCandidates(partition),
                static reference => reference.Id,
                static candidate => candidate.Id,
                static (_, candidate) => candidate.Package
            )
            .ToArray();
        IReadOnlyDictionary<string, string> notes;
        try
        {
            notes = await Task.Run(() => AppServices.Current.Changelogs.FetchAsync(packages, token), token);
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or HttpRequestException or ObjectDisposedException)
        {
            foreach (PickerItem item in _items)
            {
                item.SetChangelog(null);
            }

            return;
        }

        foreach (PickerItem item in _items)
        {
            item.SetChangelog(notes.TryGetValue(item.Ref.Id, out string? changelog) ? changelog : null);
        }
    }

    private IEnumerable<ISelectableRow> AllRows() => _items.Cast<ISelectableRow>().Concat(_toolItems);

    /// <summary>Checked state by row key. Winget can list one id twice, so the first wins.</summary>
    private Dictionary<string, bool> SelectionSnapshot()
    {
        Dictionary<string, bool> snapshot = new(StringComparer.Ordinal);
        foreach (ISelectableRow row in AllRows())
        {
            snapshot.TryAdd(row.Key, row.IsChecked);
        }

        return snapshot;
    }

    private static IEnumerable<UpdateCandidate> AllCandidates(PackagePartition partition) =>
        partition
            .Normal.Concat(partition.Cooling.Select(static entry => entry.Candidate))
            .Concat(partition.Skipped)
            .Concat(partition.Muted)
            .Concat(partition.Failed.Select(static entry => entry.Candidate));

    private void UpdateSummary(PackagePartition partition) => SummaryText.Text = Summarize(partition, _tools.Count);

    private static string Summarize(PackagePartition partition, int toolCount)
    {
        List<string> parts = [];
        if (partition.Normal.Count > 0)
        {
            parts.Add($"{partition.Normal.Count} ready");
        }

        if (toolCount > 0)
        {
            parts.Add("other tool".ToQuantity(toolCount));
        }

        if (partition.Cooling.Count > 0)
        {
            parts.Add($"{partition.Cooling.Count} too new");
        }

        if (partition.Skipped.Count > 0)
        {
            parts.Add($"{partition.Skipped.Count} skipped");
        }

        if (partition.Muted.Count > 0)
        {
            parts.Add($"{partition.Muted.Count} muted");
        }

        if (partition.HeldBack.Count > 0)
        {
            parts.Add($"{partition.HeldBack.Count} winget will not upgrade");
        }

        if (partition.Failed.Count > 0)
        {
            parts.Add($"{partition.Failed.Count} failed");
        }

        return parts.Count == 0 ? "up to date" : string.Join("   ", parts);
    }

    private void ResetList()
    {
        _items.Clear();
        _toolItems.Clear();
        Sections.Children.Clear();
        UpdateButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        MessageBar.IsOpen = false;
        LoadingPanel.Visibility = Visibility.Visible;
        SkeletonPanel.Visibility = Visibility.Visible;
        SkeletonPulse.Begin();
        LoadingText.Visibility = Visibility.Collapsed;
        SummaryText.Text = "";
        SummarySkeleton.Visibility = Visibility.Visible;
    }

    private void ShowTerminal(string message)
    {
        RefreshButton.IsEnabled = true;
        SummarySkeleton.Visibility = Visibility.Collapsed;
        LoadingPanel.Visibility = Visibility.Visible;
        SkeletonPulse.Stop();
        SkeletonPanel.Visibility = Visibility.Collapsed;
        LoadingText.Text = message;
        LoadingText.Visibility = Visibility.Visible;
        UpdateButton.IsEnabled = false;
    }

    // ///// Sections /////

    private void AddPackageSection(string title, IEnumerable<PickerItem> items)
    {
        List<PickerItem> list = items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _items.AddRange(list);
        AddSection($"{title} ({list.Count})", list, "PackageRowTemplate");
    }

    private void AddToolSection()
    {
        if (_tools.Count == 0)
        {
            return;
        }

        List<ToolItem> rows = _tools.Select(tool => new ToolItem(tool, ShowError)).ToList();
        _toolItems.AddRange(rows);
        AddSection($"Other tools ({rows.Count})", rows, "ToolRowTemplate");
    }

    /// <summary>
    /// Adds a header and its rows. The header selects or clears the rows beneath it when any
    /// of them can be selected, and is a plain title otherwise.
    /// </summary>
    /// <typeparam name="TRow">Row type the template binds to.</typeparam>
    /// <param name="header">Section title and count.</param>
    /// <param name="rows">The section's rows.</param>
    /// <param name="templateKey">Resource key of the row template.</param>
    private void AddSection<TRow>(string header, List<TRow> rows, string templateKey)
        where TRow : ISelectableRow
    {
        List<ISelectableRow> selectable = rows.Where(static row => row.CanSelect).Cast<ISelectableRow>().ToList();
        CheckBox? toggle = selectable.Count > 0 ? AddSectionToggle(header, selectable) : null;
        if (toggle is null)
        {
            AddSectionHeader(header);
        }

        foreach (TRow row in rows)
        {
            row.PropertyChanged += (_, changed) =>
            {
                if (changed.PropertyName != nameof(ISelectableRow.IsChecked))
                {
                    return;
                }

                toggle?.IsChecked = SectionState(selectable);
                UpdateSelectionState();
            };
        }

        Sections.Children.Add(
            new ItemsControl { ItemsSource = rows, ItemTemplate = (DataTemplate)Root.Resources[templateKey] }
        );
    }

    /// <summary>
    /// Adds a section header that selects or clears every row beneath it. It sits in the same
    /// column as the row checkboxes, so it reads as the parent of the rows it controls.
    /// </summary>
    /// <param name="text">Section title and count.</param>
    /// <param name="rows">Rows the header controls, all of them selectable.</param>
    /// <returns>The header, whose state the caller keeps in step with the rows.</returns>
    private CheckBox AddSectionToggle(string text, List<ISelectableRow> rows)
    {
        CheckBox toggle = new()
        {
            Content = new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Opacity = 0.7,
            },
            IsChecked = SectionState(rows),
            Margin = new Thickness(0, 8, 0, 0),
        };
        AutomationProperties.SetHelpText(toggle, "Selects or clears every row in this section");

        // Click fires only for the user's own toggle, never for the state the rows push back,
        // so the two cannot feed each other.
        toggle.Click += (_, _) =>
        {
            bool select = SectionState(rows) != true;
            foreach (ISelectableRow row in rows)
            {
                row.IsChecked = select;
            }

            toggle.IsChecked = SectionState(rows);
        };
        Sections.Children.Add(toggle);
        return toggle;
    }

    /// <summary>Checked when every row is, clear when none is, indeterminate between.</summary>
    /// <param name="rows">Rows under one header.</param>
    /// <returns>The header state.</returns>
    private static bool? SectionState(List<ISelectableRow> rows)
    {
        int selected = rows.Count(static row => row.IsChecked);
        return selected == 0 ? false
            : selected == rows.Count ? true
            : null;
    }

    private void AddSectionHeader(string text)
    {
        Sections.Children.Add(
            new TextBlock
            {
                Text = text,
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
                Opacity = 0.7,
                Margin = new Thickness(0, 12, 0, 2),
            }
        );
    }

    // ///// Actions /////

    /// <summary>The update button follows the selection: nothing checked, nothing to run.</summary>
    private void UpdateSelectionState()
    {
        int selected = AllRows().Count(static row => row.IsChecked);
        UpdateButton.IsEnabled = selected > 0;
        UpdateButton.Content = selected > 0 ? $"Update {selected} selected" : "Update selected";
    }

    /// <summary>
    /// Starts every checked upgrade: one elevated window for the packages, and one PowerShell
    /// window per tool, as the user. The picker closes only once all of them have started, so a
    /// declined elevation prompt leaves it open to try again.
    /// </summary>
    private void OnUpdateClick(object sender, RoutedEventArgs args)
    {
        PackageRef[] packages = _items.Where(static item => item.IsChecked).Select(static item => item.Ref).ToArray();
        ToolItem[] tools = _toolItems.Where(static tool => tool.IsChecked).ToArray();
        if (packages.Length == 0 && tools.Length == 0)
        {
            ShowError("Nothing selected.");
            return;
        }

        bool allStarted = true;
        if (packages.Length > 0)
        {
            try
            {
                Launcher.StartElevatedUpgrade(packages);
                // The elevated window owns these now. Clearing them means a retry after a tool
                // fails to start cannot hand the same packages to a second elevated run.
                foreach (PickerItem item in _items.Where(static item => item.IsChecked))
                {
                    item.IsChecked = false;
                }
            }
            catch (Win32Exception exception)
            {
                allStarted = false;
                ShowError(
                    Launcher.IsElevationDeclined(exception)
                        ? "Elevation was declined. The package upgrade needs administrator rights."
                        : $"Could not start the package upgrade: {exception.Message}"
                );
            }
        }

        foreach (ToolItem tool in tools)
        {
            // A started tool is unchecked, so trying again after a declined prompt does not
            // open its window a second time.
            if (tool.Run())
            {
                tool.IsChecked = false;
            }
            else
            {
                allStarted = false;
            }
        }

        if (allStarted)
        {
            Close();
        }
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs args)
    {
        await LoadAsync();
    }

    private void OnCloseClick(object sender, RoutedEventArgs args) => Close();

    // ///// Navigation /////

    /// <summary>
    /// Shows a page in place of the list. Settings and release notes belong to the same window
    /// because they are places to read and change things, not decisions blocking the list; the
    /// dialogs that remain are the ones an action genuinely waits on.
    /// </summary>
    /// <param name="title">Header text beside the back arrow.</param>
    /// <param name="content">The page.</param>
    private void ShowDetail(string title, UIElement content)
    {
        // Back returns focus to the control that opened the page: a row's notes link, or the
        // settings button. A row is remembered by key, since a settings save rebuilds the list
        // behind this page.
        _detailOrigin = CaptureFocus();
        DetailTitle.Text = title;
        DetailHost.Content = content;
        DetailPage.Visibility = Visibility.Visible;
        ListPage.Visibility = Visibility.Collapsed;
        BackButton.Focus(FocusState.Programmatic);
    }

    private void OnBackClick(object sender, RoutedEventArgs args)
    {
        DetailPage.Visibility = Visibility.Collapsed;
        ListPage.Visibility = Visibility.Visible;
        // The browser and the settings form both hold resources worth dropping on the way out.
        DetailHost.Content = null;
        _settings?.Dispose();
        _settings = null;
        // The back button loses focus with the page it sits on, so focus goes back to what
        // opened the page.
        RestoreFocus(_detailOrigin ?? new FocusOrigin.InList(FocusState.Programmatic));
        _detailOrigin = null;
    }

    private void OnNotesClick(object sender, RoutedEventArgs args)
    {
        if (sender is FrameworkElement { DataContext: PickerItem item })
        {
            ShowDetail($"{item.Name}  {item.VersionText}", new ReleaseNotesView(item.Changelog));
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs args)
    {
        SettingsView settings = new();
        // A cooldown or token change decides what counts as too new, so the list behind this
        // page is rebuilt when one lands. A schedule change alone leaves it untouched.
        settings.ListAffected += (_, _) => OnPreferenceChanged();
        _settings = settings;
        ShowDetail("Settings", settings);
    }

    // ///// Focus /////

    /// <summary>
    /// Where focus sat before the list changed under it, in terms that outlive the change. A
    /// rebuild replaces every row control, so one is named by its row key and its x:Name and
    /// found again in the replacement. A control outside the list survives the rebuild and is
    /// held as it is.
    /// </summary>
    /// <param name="State">How the control had focus, so a keyboard user keeps the focus visual.</param>
    private abstract record FocusOrigin(FocusState State)
    {
        /// <summary>A control inside a row.</summary>
        /// <param name="Key">The row's <see cref="ISelectableRow.Key"/>.</param>
        /// <param name="Name">The control's x:Name in the row template.</param>
        /// <param name="State">How the control had focus.</param>
        public sealed record InRow(string Key, string Name, FocusState State) : FocusOrigin(State);

        /// <summary>A control in the list but outside any row, which is a section header.</summary>
        /// <param name="State">How the control had focus.</param>
        public sealed record InList(FocusState State) : FocusOrigin(State);

        /// <summary>A control the list rebuild leaves in place.</summary>
        /// <param name="Control">The control itself.</param>
        /// <param name="State">How the control had focus.</param>
        public sealed record Elsewhere(Control Control, FocusState State) : FocusOrigin(State);
    }

    /// <summary>Describes the focused control, or <c>null</c> when no control has focus.</summary>
    private FocusOrigin? CaptureFocus()
    {
        XamlRoot? xamlRoot = Root.XamlRoot;
        if (xamlRoot is null || FocusManager.GetFocusedElement(xamlRoot) is not Control control)
        {
            return null;
        }

        // Focus() throws on Unfocused, so that state maps to Programmatic.
        FocusState state = control.FocusState == FocusState.Unfocused ? FocusState.Programmatic : control.FocusState;
        if (!IsInside(control, Sections))
        {
            return new FocusOrigin.Elsewhere(control, state);
        }

        return control.DataContext is ISelectableRow row
            ? new FocusOrigin.InRow(row.Key, control.Name, state)
            : new FocusOrigin.InList(state);
    }

    /// <summary>
    /// Hands focus back after the list was rebuilt or shown again. The same control is preferred,
    /// then the first focusable control in the same row, then the top of the list, then the
    /// Close button when the list itself is gone.
    /// </summary>
    /// <param name="origin">Where focus sat.</param>
    private void RestoreFocus(FocusOrigin origin)
    {
        // A freshly populated ItemsControl has no containers until it is measured. A list
        // collapsed behind a detail page is measured again once visible. One synchronous pass
        // covers both, so the containers exist before the lookup below.
        Root.UpdateLayout();
        bool landed = origin switch
        {
            FocusOrigin.Elsewhere elsewhere => elsewhere.Control.Focus(origin.State),
            FocusOrigin.InRow row => FocusRow(row.Key, row.Name, origin.State),
            _ => false,
        };
        if (!landed && !FocusFirst(Sections, origin.State))
        {
            CloseButton.Focus(origin.State);
        }
    }

    /// <summary>Focuses a named control in a row, or the row's first control that will take focus.</summary>
    /// <param name="key">The row's <see cref="ISelectableRow.Key"/>.</param>
    /// <param name="name">The control's x:Name in the row template.</param>
    /// <param name="state">How the control had focus.</param>
    /// <returns><c>true</c> when the row exists and a control in it took focus.</returns>
    private bool FocusRow(string key, string name, FocusState state)
    {
        DependencyObject? container = RowContainer(key);
        if (container is null)
        {
            return false;
        }

        // Every package row carries every named control, collapsed where its section does not
        // offer it. A collapsed control refuses focus, and the row's first control takes it.
        Control? same = Descendants(container).OfType<Control>().FirstOrDefault(control => control.Name == name);
        return (same is not null && same.Focus(state)) || FocusFirst(container, state);
    }

    /// <summary>The realized container of the row with <paramref name="key"/>, or <c>null</c>.</summary>
    private DependencyObject? RowContainer(string key)
    {
        foreach (ItemsControl section in Sections.Children.OfType<ItemsControl>())
        {
            foreach (object item in section.Items)
            {
                if (item is ISelectableRow row && row.Key == key)
                {
                    return section.ContainerFromItem(item);
                }
            }
        }

        return null;
    }

    /// <summary>Focuses the first control under <paramref name="root"/> that will take focus.</summary>
    /// <param name="root">Subtree to search, in tree order.</param>
    /// <param name="state">How the control had focus.</param>
    /// <returns><c>true</c> when a control took focus.</returns>
    private static bool FocusFirst(DependencyObject root, FocusState state)
    {
        foreach (Control control in Descendants(root).OfType<Control>())
        {
            if (control.Focus(state))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInside(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Every element under <paramref name="root"/> in the visual tree, depth first.</summary>
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (DependencyObject descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    // ///// Messages /////

    private void ShowError(string message)
    {
        MessageBar.Severity = InfoBarSeverity.Error;
        MessageBar.Message = message;
        MessageBar.IsOpen = true;
    }
}

/// <summary>
/// Segoe Fluent Icons glyphs naming why a row carries a note. Each reaches a <c>FontIcon</c>,
/// which is the only place a private-use codepoint renders.
/// </summary>
internal static class NoteGlyphs
{
    /// <summary>Clock: the version is still inside its cooldown.</summary>
    public const string Cooling = "\uE823";

    /// <summary>Cross: the user skipped this version.</summary>
    public const string Skipped = "\uE711";

    /// <summary>Crossed bell: the package is muted.</summary>
    public const string Muted = "\uE7ED";

    /// <summary>Error circle: the last upgrade failed.</summary>
    public const string Failed = "\uE783";

    /// <summary>Warning triangle: winget refuses to upgrade it.</summary>
    public const string HeldBack = "\uE7BA";
}
