using Microsoft.UI.Xaml;
using WingetNudge.Core.Tools;

namespace WingetNudge.Views;

/// <summary>One registered tool in the settings list.</summary>
/// <param name="Id">Registry key.</param>
/// <param name="Definition">The registered definition.</param>
/// <param name="EditRequested">Opens the editor for this tool.</param>
/// <param name="RemoveRequested">Unregisters this tool.</param>
public sealed record ToolRow(
    string Id,
    ToolDefinition Definition,
    Action<string, ToolDefinition> EditRequested,
    Action<string> RemoveRequested
)
{
    /// <summary>Display name.</summary>
    public string Name => Definition.Name;

    /// <summary>The version command, for the caption under the name.</summary>
    public string Command => string.Join(' ', Definition.CurrentCommand);

    /// <summary>Opens the editor. Shaped for a XAML Click binding.</summary>
    /// <param name="sender">Source of the click.</param>
    /// <param name="args">Click arguments.</param>
    public void Edit(object sender, RoutedEventArgs args) => EditRequested(Id, Definition);

    /// <summary>Unregisters the tool. Shaped for a XAML Click binding.</summary>
    /// <param name="sender">Source of the click.</param>
    /// <param name="args">Click arguments.</param>
    public void Remove(object sender, RoutedEventArgs args) => RemoveRequested(Id);
}
