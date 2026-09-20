using Microsoft.UI.Xaml.Controls;
using WingetNudge.Core.Tools;
using WingetNudge.Services;

namespace WingetNudge.Views;

/// <summary>
/// Registers or edits a manual tool. Test runs both probes and shows what they returned, so a
/// wrong regex is caught before it is saved rather than the next time a check runs.
/// </summary>
public sealed partial class ToolEditorDialog : ContentDialog
{
    private readonly string? _existingId;

    /// <summary>Creates the dialog for a new tool, or for one already registered.</summary>
    /// <param name="id">Registry key to edit, or <c>null</c> to add one.</param>
    /// <param name="definition">Definition to edit, or <c>null</c> to add one.</param>
    public ToolEditorDialog(string? id = null, ToolDefinition? definition = null)
    {
        InitializeComponent();
        _existingId = id;
        Title = id is null ? "Add a tool" : $"Edit {definition?.Name ?? id}";
        if (id is not null)
        {
            IdBox.Text = id;
            IdBox.IsEnabled = false;
        }

        if (definition is not null)
        {
            NameBox.Text = definition.Name;
            CurrentCommandBox.Text = string.Join(' ', definition.CurrentCommand);
            CurrentRegexBox.Text = definition.CurrentRegex ?? "";
            LatestUrlBox.Text = definition.LatestUrl;
            LatestFieldBox.Text = definition.LatestJsonField;
            LatestRegexBox.Text = definition.LatestRegex ?? "";
            UpgradeCommandBox.Text = definition.UpgradeCommand;
        }
    }

    /// <summary>The id the dialog saved, or <c>null</c> when nothing was saved.</summary>
    public string? SavedId { get; private set; }

    private ToolDefinition Build() =>
        new()
        {
            Name = NameBox.Text.Trim(),
            CurrentCommand =
            [
                .. CurrentCommandBox.Text.Split(
                    ' ',
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                ),
            ],
            CurrentRegex = Empty(CurrentRegexBox.Text),
            LatestUrl = LatestUrlBox.Text.Trim(),
            LatestJsonField = LatestFieldBox.Text.Trim(),
            LatestRegex = Empty(LatestRegexBox.Text),
            UpgradeCommand = UpgradeCommandBox.Text.Trim(),
        };

    private static string? Empty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private void Report(InfoBarSeverity severity, string message)
    {
        TestBar.Severity = severity;
        TestBar.Message = message;
        TestBar.IsOpen = true;
    }

    private async void OnTestClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Testing must not dismiss the dialog; the point is to fix what it reports.
        args.Cancel = true;
        ToolDefinition definition;
        try
        {
            definition = Build();
            ToolDefinitionValidator.Ensure(definition);
        }
        catch (ArgumentException exception)
        {
            Report(InfoBarSeverity.Error, exception.Message);
            return;
        }

        ToolProber prober = AppServices.Current.ToolProber;
        try
        {
            string? current = await prober.GetCurrentAsync(definition, CancellationToken.None);
            // A GUID key keeps the probe out of the cache a real tool would later read.
            string? latest = await prober.GetLatestAsync(
                _existingId ?? $"probe-{Guid.NewGuid():N}",
                definition,
                force: true,
                CancellationToken.None
            );
            Report(
                current is null || latest is null ? InfoBarSeverity.Warning : InfoBarSeverity.Success,
                $"Installed: {current ?? "not found"}   Latest: {latest ?? "not found"}"
            );
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            Report(InfoBarSeverity.Error, $"Probe failed: {exception.Message}");
        }
    }

    private void OnSaveClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string id = _existingId ?? IdBox.Text.Trim();
        if (!ToolRegistry.IsValidId(id))
        {
            args.Cancel = true;
            Report(InfoBarSeverity.Error, "The id may hold only letters, digits, dot, dash and underscore.");
            return;
        }

        try
        {
            ToolDefinition definition = Build();
            ToolDefinitionValidator.Ensure(definition);
            AppServices.Current.Tools.Register(id, definition);
            SavedId = id;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException)
        {
            args.Cancel = true;
            Report(InfoBarSeverity.Error, exception.Message);
        }
    }
}
