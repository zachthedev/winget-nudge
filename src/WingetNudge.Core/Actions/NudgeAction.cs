namespace WingetNudge.Core.Actions;

/// <summary>What a notification activation asks the app to do.</summary>
public abstract record NudgeAction
{
    private NudgeAction() { }

    /// <summary>Open the picker window.</summary>
    public sealed record OpenPicker : NudgeAction;

    /// <summary>The arguments named nothing the app recognizes.</summary>
    public sealed record Unknown : NudgeAction;

    /// <summary>Parses notification arguments into an action.</summary>
    /// <param name="arguments">Key/value pairs from the activation.</param>
    /// <returns>The action, or <see cref="Unknown"/> for anything unrecognized.</returns>
    public static NudgeAction Parse(IReadOnlyDictionary<string, string> arguments) =>
        arguments.TryGetValue(UpdateActions.ActionKey, out string? action) && action == UpdateActions.Picker
            ? new OpenPicker()
            : new Unknown();

    /// <summary>Parses the serialized <c>key=value;key=value</c> form of the arguments.</summary>
    /// <param name="serialized">Argument string as the notification platform delivers it.</param>
    /// <returns>The action, or <see cref="Unknown"/> for anything unrecognized or malformed.</returns>
    public static NudgeAction Parse(string serialized)
    {
        Dictionary<string, string> arguments = new(StringComparer.Ordinal);
        foreach (string pair in serialized.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = pair.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            arguments[pair[..separator]] = Uri.UnescapeDataString(pair[(separator + 1)..]);
        }

        return Parse(arguments);
    }
}
