using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WingetNudge.Views;

/// <summary>
/// One package's release notes, shown in place of the list. The browser starts when the view is
/// laid out, so nothing here costs anything until a user asks for notes.
/// </summary>
public sealed partial class ReleaseNotesView : UserControl
{
    /// <summary>Creates the view.</summary>
    /// <param name="markdown">Release notes.</param>
    public ReleaseNotesView(string markdown)
    {
        InitializeComponent();
        Notes.Markdown = markdown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        await Notes.RenderAsync();
    }
}
