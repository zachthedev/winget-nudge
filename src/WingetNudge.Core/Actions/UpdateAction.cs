namespace WingetNudge.Core.Actions;

/// <summary>One button on the notification.</summary>
/// <param name="Label">Button text.</param>
/// <param name="Argument">Notification argument value, or <c>null</c> for dismiss.</param>
/// <param name="IsDismiss">Whether the button only closes the notification.</param>
public sealed record UpdateAction(string Label, string? Argument, bool IsDismiss);

/// <summary>Single source of truth for the notification's actions.</summary>
public static class UpdateActions
{
    /// <summary>Notification argument key that carries the action.</summary>
    public const string ActionKey = "action";

    /// <summary>Argument value for opening the picker.</summary>
    public const string Picker = "picker";

    /// <summary>Argument the notification body carries; a body click opens the picker.</summary>
    public static string LaunchArgument { get; } = $"{ActionKey}={Picker}";

    /// <summary>The buttons, in display order.</summary>
    public static IReadOnlyList<UpdateAction> Buttons { get; } =
    [new("Review updates", Picker, IsDismiss: false), new("Dismiss", null, IsDismiss: true)];
}
