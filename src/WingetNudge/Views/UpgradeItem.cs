using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Upgrade;

namespace WingetNudge.Views;

/// <summary>One package's row in the upgrade window.</summary>
/// <param name="package">The package.</param>
public sealed class UpgradeItem(PackageRef package) : INotifyPropertyChanged
{
    private const string PendingGlyph = "\uE823";
    private const string RunningGlyph = "\uE768";
    private const string DoneGlyph = "\uE73E";
    private const string FailedGlyph = "\uE711";
    private const string SkippedGlyph = "\uE8D8";

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Winget package id.</summary>
    public string Id => package.Id;

    /// <summary>Display name, falling back to the id.</summary>
    public string Name => package.Name.Length > 0 ? package.Name : package.Id;

    /// <summary>Current status line.</summary>
    public string Status
    {
        get;
        set => SetField(ref field, value);
    } = "Waiting";

    /// <summary>Status glyph from Segoe Fluent Icons.</summary>
    public string Glyph
    {
        get;
        private set => SetField(ref field, value);
    } = PendingGlyph;

    /// <summary>Progress from 0 to 100.</summary>
    public double Progress
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Whether the bar shows activity without a known fraction.</summary>
    public bool IsIndeterminate
    {
        get;
        set => SetField(ref field, value);
    }

    /// <summary>Whether the progress bar is shown.</summary>
    public Visibility ProgressVisibility
    {
        get;
        set => SetField(ref field, value);
    } = Visibility.Collapsed;

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

    /// <summary>Installer log written for a failed upgrade, or empty.</summary>
    public string LogPath
    {
        get;
        private set => SetField(ref field, value);
    } = "";

    /// <summary>Whether the installer log link is shown.</summary>
    public Visibility LogVisibility
    {
        get;
        private set => SetField(ref field, value);
    } = Visibility.Collapsed;

    /// <summary>Whether the mute link is shown, which only a failed row offers.</summary>
    public Visibility MuteVisibility
    {
        get;
        private set => SetField(ref field, value);
    } = Visibility.Collapsed;

    /// <summary>Mute link text, which reflects whether the package is already muted.</summary>
    public string MuteLabel
    {
        get;
        private set => SetField(ref field, value);
    } = "Mute this package";

    /// <summary>Whether the mute link can still act.</summary>
    public bool IsMuteEnabled
    {
        get;
        private set => SetField(ref field, value);
    } = true;

    /// <summary>Records that the package is now muted, so the link stops offering it.</summary>
    public void Muted()
    {
        MuteLabel = "Muted";
        IsMuteEnabled = false;
    }

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

    /// <summary>Marks the row as running.</summary>
    public void Start()
    {
        Glyph = RunningGlyph;
        Status = "Starting";
        ProgressVisibility = Visibility.Visible;
        IsIndeterminate = true;
    }

    /// <summary>Applies a winget progress snapshot.</summary>
    /// <param name="snapshot">Progress snapshot.</param>
    public void Apply(UpgradeProgress snapshot)
    {
        Status = snapshot.Phase switch
        {
            UpgradePhase.Queued => "Queued",
            UpgradePhase.Downloading => "Downloading",
            UpgradePhase.Installing => "Installing",
            UpgradePhase.Finishing => "Finishing",
            _ => Status,
        };
        if (snapshot.Fraction is double fraction)
        {
            IsIndeterminate = false;
            Progress = fraction * 100;
        }
        else
        {
            IsIndeterminate = true;
        }
    }

    /// <summary>Marks the row finished.</summary>
    /// <param name="result">Outcome.</param>
    /// <param name="detail">Final status text.</param>
    /// <param name="logPath">Installer log written for a failure, or <c>null</c>.</param>
    public void Finish(PackageResult result, string detail, string? logPath = null)
    {
        LogPath = logPath ?? "";
        LogVisibility = LogPath.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        MuteVisibility = result == PackageResult.Failed ? Visibility.Visible : Visibility.Collapsed;
        Glyph = result switch
        {
            PackageResult.Upgraded => DoneGlyph,
            PackageResult.Failed => FailedGlyph,
            PackageResult.Skipped => SkippedGlyph,
            _ => PendingGlyph,
        };
        Status = detail;
        ProgressVisibility = Visibility.Collapsed;
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
