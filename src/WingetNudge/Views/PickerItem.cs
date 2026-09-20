using System.ComponentModel;
using System.Runtime.CompilerServices;
using Humanizer;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Preferences;
using WingetNudge.Core.Tracking;
using WingetNudge.Services;

namespace WingetNudge.Views;

/// <summary>
/// Per-row actions a section offers. A section drops an action that cannot mean anything in that
/// state: skipping a version needs a version on offer, and muting a pin-blocked package changes
/// nothing winget has not already refused.
/// </summary>
[Flags]
public enum RowActions
{
    /// <summary>No actions.</summary>
    None = 0,

    /// <summary>Skip or unskip the offered version.</summary>
    Skip = 1,

    /// <summary>Mute or unmute the package.</summary>
    Mute = 2,

    /// <summary>Open the installer log from the last failure.</summary>
    ViewLog = 4,

    /// <summary>Clear the failure so the package is offered again.</summary>
    Retry = 8,

    /// <summary>Explain that the pin has to be removed in winget.</summary>
    UnpinHint = 16,

    /// <summary>The default pair every offerable row carries.</summary>
    SkipAndMute = Skip | Mute,

    /// <summary>Everything a failed row offers.</summary>
    Failed = Skip | Mute | ViewLog | Retry,
}

/// <summary>A row a section header can select or clear, package and tool alike.</summary>
public interface ISelectableRow : INotifyPropertyChanged
{
    /// <summary>Identity that survives a re-render, unique across packages and tools.</summary>
    string Key { get; }

    /// <summary>Whether the row may be checked at all.</summary>
    bool CanSelect { get; }

    /// <summary>Whether the row is selected.</summary>
    bool IsChecked { get; set; }
}

/// <summary>One package row in the picker.</summary>
public sealed class PickerItem : ISelectableRow
{
    /// <summary>Segoe Fluent Icons Ringer, shown while notifications are on.</summary>
    private const string BellGlyph = "\uEA8F";

    /// <summary>Segoe Fluent Icons RingerSilent, the crossed bell shown while muted.</summary>
    private const string BellOffGlyph = "\uE7ED";

    private readonly UpdateCandidate _candidate;
    private readonly TimeProvider _clock;
    private readonly Action<string> _reportError;
    private readonly Action? _onPreferenceChanged;
    private readonly string _noteGlyph;

    /// <summary>Creates a row.</summary>
    /// <param name="candidate">The package and its availability date.</param>
    /// <param name="isChecked">Whether the row starts checked.</param>
    /// <param name="caption">Section-specific note such as a failure reason, or <c>null</c>.</param>
    /// <param name="isMuted">Whether the package is muted.</param>
    /// <param name="clock">Time source for the age text.</param>
    /// <param name="reportError">Receives errors from row actions.</param>
    /// <param name="canSelect">
    /// Whether the row may be checked. A package winget refuses to upgrade is shown but never
    /// offered, so a blocking pin cannot be walked past by accident.
    /// </param>
    /// <param name="onPreferenceChanged">
    /// Runs after a mute or skip lands, so the host can move the row into its new section.
    /// </param>
    /// <param name="noteGlyph">
    /// Segoe Fluent Icons glyph introducing the note, which says what kind of note it is. It
    /// reaches a <c>FontIcon</c>, never a text run, since the codepoints are private-use and
    /// the UI text font has nothing at them. Empty for a row with nothing to add.
    /// </param>
    /// <param name="actions">Which per-row actions this section offers.</param>
    /// <param name="logPath">Installer log for a failed row, or empty.</param>
    public PickerItem(
        UpdateCandidate candidate,
        bool isChecked,
        string? caption,
        bool isMuted,
        TimeProvider clock,
        Action<string> reportError,
        bool canSelect = true,
        Action? onPreferenceChanged = null,
        string noteGlyph = "",
        RowActions actions = RowActions.SkipAndMute,
        string logPath = ""
    )
    {
        Actions = actions;
        LogPath = logPath;
        _candidate = candidate;
        _clock = clock;
        _reportError = reportError;
        _onPreferenceChanged = onPreferenceChanged;
        _noteGlyph = noteGlyph;
        CanSelect = canSelect;
        IsChecked = canSelect && isChecked;
        IsMuted = isMuted;
        Age = AgeText(candidate.AvailableSince, candidate.Source, clock.GetUtcNow());
        SetNote(caption ?? "");
    }

    /// <summary>Whether the row may be checked.</summary>
    public bool CanSelect { get; }

    /// <summary>Which per-row actions this section offers.</summary>
    public RowActions Actions { get; }

    /// <summary>Installer log for a failed row, or empty.</summary>
    public string LogPath { get; }

    /// <summary>Whether the skip-this-version button is shown.</summary>
    public Visibility SkipVisibility => Show(RowActions.Skip);

    /// <summary>Whether the mute button is shown.</summary>
    public Visibility MuteVisibility => Show(RowActions.Mute);

    /// <summary>Whether the retry button is shown.</summary>
    public Visibility RetryVisibility => Show(RowActions.Retry);

    /// <summary>Whether the installer log link is shown.</summary>
    public Visibility LogVisibility =>
        LogPath.Length > 0 ? Show(RowActions.ViewLog) : Visibility.Collapsed;

    /// <summary>Whether the unpin hint is shown.</summary>
    public Visibility UnpinVisibility => Show(RowActions.UnpinHint);

    /// <summary>Command that removes the winget pin, for the hint's tooltip.</summary>
    public string UnpinHint => $"Remove the pin with: winget pin remove {Ref.Id}";

    private Visibility Show(RowActions action) =>
        Actions.HasFlag(action) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Display name of the entry winget matched in Apps and Features, when it differs from the
    /// package name. A mismatch is how a wrong correlation shows itself.
    /// </summary>
    public string InstalledName => _candidate.Package.InstalledName ?? "";

    /// <summary>Whether the installed entry is worth naming.</summary>
    public Visibility InstalledNameVisibility =>
        InstalledName.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Identity for the upgrade engine.</summary>
    public PackageRef Ref => _candidate.Ref;

    /// <inheritdoc/>
    public string Key => Ref.Id;

    /// <summary>Display name.</summary>
    public string Name => _candidate.Name;

    /// <summary>Version on disk.</summary>
    public string InstalledVersion => _candidate.Package.InstalledVersion ?? "?";

    /// <summary>Version on offer.</summary>
    public string AvailableVersion => _candidate.Package.AvailableVersion ?? "?";

    /// <summary>Both versions for places without room for the arrow glyph.</summary>
    public string VersionText => $"{InstalledVersion} to {AvailableVersion}";

    /// <summary>Whether the row is selected for upgrade.</summary>
    public bool IsChecked
    {
        get;
        set => SetField(ref field, CanSelect && value);
    }

    /// <summary>Whether the package is muted.</summary>
    public bool IsMuted
    {
        get;
        private set
        {
            SetField(ref field, value);
            OnPropertyChanged(nameof(MuteGlyph));
            OnPropertyChanged(nameof(MuteToolTip));
        }
    }

    /// <summary>Whether the offered version is skipped.</summary>
    public bool IsSkipped
    {
        get;
        private set
        {
            SetField(ref field, value);
            OnPropertyChanged(nameof(SkipToolTip));
        }
    }

    /// <summary>How long the offered version has been available.</summary>
    public string Age
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>Section note such as a cooldown, a skip or a failure reason. Empty for none.</summary>
    public string Note
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>Glyph naming the note's kind, for a <c>FontIcon</c>. Empty for none.</summary>
    public string NoteGlyph => Note.Length > 0 ? _noteGlyph : "";

    /// <summary>Whether the note line is shown.</summary>
    public Visibility NoteVisibility => Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Whether the note carries a glyph of its own.</summary>
    public Visibility NoteGlyphVisibility =>
        NoteGlyph.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Age and note in words, for the checkbox tooltip and its accessible help text.</summary>
    public string Caption => Note.Length > 0 ? $"{Age}, {Note}" : Age;

    /// <summary>Release notes, or empty.</summary>
    public string Changelog
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>Link text reflecting whether notes are loading, present, or absent.</summary>
    public string NotesLabel
    {
        get;
        private set => SetField(ref field, value);
    } = "Loading release notes";

    /// <summary>Whether the release notes link can open.</summary>
    public bool IsNotesEnabled
    {
        get;
        private set => SetField(ref field, value);
    }

    /// <summary>Bell glyph for the mute toggle.</summary>
    public string MuteGlyph => IsMuted ? BellOffGlyph : BellGlyph;

    /// <summary>Tooltip and accessible name for the mute toggle.</summary>
    public string MuteToolTip => IsMuted ? "Unmute" : "Mute this package";

    /// <summary>Tooltip and accessible name for the skip toggle.</summary>
    public string SkipToolTip => IsSkipped ? "Stop skipping this version" : "Skip this version";

    /// <summary>Records the fetched release notes, or their absence.</summary>
    /// <param name="changelog">Notes, or <c>null</c> when none were found.</param>
    public void SetChangelog(string? changelog)
    {
        if (string.IsNullOrWhiteSpace(changelog))
        {
            NotesLabel = "No release notes found";
            IsNotesEnabled = false;
            return;
        }

        Changelog = changelog;
        NotesLabel = "Release notes";
        IsNotesEnabled = true;
    }

    /// <summary>Mutes or unmutes the package.</summary>
    public void ToggleMute()
    {
        try
        {
            if (IsMuted)
            {
                AppServices.Current.Preferences.Clear(Ref.Id);
                IsMuted = false;
                IsSkipped = false;
                IsChecked = true;
                SetNote("");
            }
            else
            {
                AppServices.Current.Preferences.Set(Ref.Id, PreferenceState.Muted);
                IsMuted = true;
                IsChecked = false;
                SetNote("hidden from notifications");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _reportError($"Failed to update preference: {exception.Message}");
            return;
        }

        _onPreferenceChanged?.Invoke();
    }

    /// <summary>Skips the offered version, or unskips it when already skipped.</summary>
    public void ToggleSkip()
    {
        string? version = _candidate.Package.AvailableVersion;
        if (version is null)
        {
            return;
        }

        try
        {
            if (IsSkipped)
            {
                AppServices.Current.Preferences.Clear(Ref.Id);
                IsSkipped = false;
                IsChecked = true;
                SetNote("");
            }
            else
            {
                AppServices.Current.Preferences.SkipVersion(Ref.Id, version);
                IsSkipped = true;
                IsMuted = false;
                IsChecked = false;
                SetNote($"skipped {version}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _reportError($"Failed to update preference: {exception.Message}");
            return;
        }

        _onPreferenceChanged?.Invoke();
    }

    /// <summary>Clears the recorded failure so the package returns to the ready list.</summary>
    public void Retry()
    {
        try
        {
            AppServices.Current.Preferences.Clear(Ref.Id);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _reportError($"Failed to update preference: {exception.Message}");
            return;
        }

        _onPreferenceChanged?.Invoke();
    }

    /// <summary>Opens the installer log from the last failure.</summary>
    public void OpenLog() => Launcher.OpenFileDeElevated(LogPath);

    /// <summary>Copies the command that removes the winget pin, since only winget can remove it.</summary>
    public void CopyUnpinCommand()
    {
        DataPackage package = new();
        package.SetText($"winget pin remove {Ref.Id}");
        Clipboard.SetContent(package);
        SetNote($"unpin command copied: winget pin remove {Ref.Id}");
    }

    /// <summary>Describes how long a version has been available.</summary>
    /// <param name="since">Availability date.</param>
    /// <param name="source">Where the date came from.</param>
    /// <param name="now">Current time.</param>
    /// <returns>Text such as "released 3 days ago" or "first seen 2 hours ago".</returns>
    public static string AgeText(DateTimeOffset since, PublishSource source, DateTimeOffset now)
    {
        string verb = source == PublishSource.FirstSeen ? "first seen" : "released";
        return $"{verb} {since.Humanize(now)}";
    }

    /// <summary>Sets the note line and the properties derived from whether it has one.</summary>
    /// <param name="note">Note text, empty for a row with nothing to add.</param>
    private void SetNote(string note)
    {
        Note = note;
        Age = AgeText(_candidate.AvailableSince, _candidate.Source, _clock.GetUtcNow());
        OnPropertyChanged(nameof(NoteGlyph));
        OnPropertyChanged(nameof(NoteVisibility));
        OnPropertyChanged(nameof(NoteGlyphVisibility));
        OnPropertyChanged(nameof(Caption));
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// One manual tool row in the picker. It is selectable like a package, but its upgrade runs as
/// the user in a PowerShell window of its own, never in the elevated upgrade window.
/// </summary>
/// <param name="status">Probe result with the tool's definition.</param>
/// <param name="reportError">Receives the reason a run could not start.</param>
public sealed class ToolItem(Core.Tools.ToolStatus status, Action<string> reportError)
    : ISelectableRow
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    public string Key => $"tool:{status.Id}";

    /// <inheritdoc/>
    public bool CanSelect => true;

    /// <inheritdoc/>
    public bool IsChecked
    {
        get;
        set
        {
            if (field == value)
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    } = true;

    /// <summary>Display name.</summary>
    public string Name => status.Name;

    /// <summary>Installed version.</summary>
    public string CurrentVersion => status.Current ?? "?";

    /// <summary>Latest version.</summary>
    public string LatestVersion => status.Latest ?? "?";

    /// <summary>The upgrade command, shown so it can be read and copied.</summary>
    public string Command => status.Definition.UpgradeCommand;

    /// <summary>Tooltip and accessible name for the run button.</summary>
    public string RunToolTip => $"Upgrade {Name} now";

    /// <summary>Opens a PowerShell window running the upgrade command.</summary>
    /// <returns><c>true</c> when the window started.</returns>
    public bool Run()
    {
        try
        {
            Launcher.StartTerminalCommand(Command);
            return true;
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            reportError($"Could not start the {Name} upgrade: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Runs this one tool now and clears its row, so Update selected does not start the same
    /// command a second time. Shaped for a Click binding.
    /// </summary>
    public void RunNow()
    {
        if (Run())
        {
            IsChecked = false;
        }
    }
}

/// <summary>
/// Placeholder row shown while the inventory loads. It carries one bar per line a real row
/// draws, at the widths real content runs to, so the list does not reflow when the data lands.
/// </summary>
/// <param name="NameWidth">Width of the package name bar.</param>
/// <param name="SubtitleWidth">
/// Width of the installed-name bar. Zero for a row whose installed entry matches the package
/// name, which is how most rows render.
/// </param>
/// <param name="VersionWidth">Width of the version transition bar.</param>
/// <param name="CaptionWidth">Width of the age bar.</param>
public sealed record SkeletonRow(
    double NameWidth,
    double SubtitleWidth,
    double VersionWidth,
    double CaptionWidth
)
{
    /// <summary>Whether this row draws an installed-name line.</summary>
    public Visibility SubtitleVisibility =>
        SubtitleWidth > 0 ? Visibility.Visible : Visibility.Collapsed;
}
