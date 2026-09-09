using Humanizer;

namespace WingetNudge.Core.Notifications;

/// <summary>Title and body of the update notification.</summary>
/// <param name="Title">Headline with the update count.</param>
/// <param name="Body">Bulleted names, truncated past four entries.</param>
public sealed record UpdateNotificationText(string Title, string Body)
{
    /// <summary>Names shown in full before the body truncates.</summary>
    public const int MaxNamesShown = 4;

    /// <summary>Names kept when the body truncates.</summary>
    public const int NamesKeptWhenTruncated = 3;

    /// <summary>Composes the notification text for a set of updatable names.</summary>
    /// <param name="names">Package names followed by manual tool names.</param>
    /// <returns>The text, or <c>null</c> when there is nothing to announce.</returns>
    public static UpdateNotificationText? Compose(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return null;
        }

        string body;
        if (names.Count <= MaxNamesShown)
        {
            body = string.Join('\n', names.Select(static name => $"* {name}"));
        }
        else
        {
            IEnumerable<string> preview = names
                .Take(NamesKeptWhenTruncated)
                .Select(static name => $"* {name}");
            int rest = names.Count - NamesKeptWhenTruncated;
            body = $"{string.Join('\n', preview)}\n  + {rest} more";
        }

        return new UpdateNotificationText($"{"update".ToQuantity(names.Count)} available", body);
    }
}
