using System.Text.RegularExpressions;

namespace WingetNudge.Core.Tools;

/// <summary>
/// Checks a tool definition before it is stored, so a bad URL or regex fails at registration
/// rather than inside every later check.
/// </summary>
public static class ToolDefinitionValidator
{
    /// <summary>Parses the latest-version URL, accepting only https.</summary>
    /// <param name="value">Candidate URL.</param>
    /// <param name="url">The parsed URL when valid.</param>
    /// <returns><c>true</c> when the URL is absolute and uses https.</returns>
    public static bool TryParseLatestUrl(string? value, out Uri? url)
    {
        url = null;
        return !string.IsNullOrWhiteSpace(value)
            && Uri.TryCreate(value, UriKind.Absolute, out url)
            && url.Scheme == "https";
    }

    /// <summary>Whether a regex compiles and carries at least one capture group.</summary>
    /// <param name="pattern">Regex, or <c>null</c> for none.</param>
    /// <returns><c>true</c> when absent or usable.</returns>
    public static bool IsValidRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return true;
        }

        try
        {
            return new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1))
                    .GetGroupNumbers()
                    .Length >= 2;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Throws when any field of the definition is unusable.</summary>
    /// <param name="tool">Definition to check.</param>
    /// <exception cref="ArgumentException">A field is empty, the URL is not http(s), or a regex is malformed or has no group.</exception>
    public static void Ensure(ToolDefinition tool)
    {
        if (string.IsNullOrWhiteSpace(tool.Name))
        {
            throw new ArgumentException("Tool name is required.");
        }

        if (tool.CurrentCommand.Count == 0 || string.IsNullOrWhiteSpace(tool.CurrentCommand[0]))
        {
            throw new ArgumentException("Current-version command needs an executable.");
        }

        if (!TryParseLatestUrl(tool.LatestUrl, out _))
        {
            throw new ArgumentException($"'{tool.LatestUrl}' is not an https URL.");
        }

        if (string.IsNullOrWhiteSpace(tool.LatestJsonField))
        {
            throw new ArgumentException("Latest-version JSON field is required.");
        }

        if (!IsValidRegex(tool.CurrentRegex))
        {
            throw new ArgumentException(
                "Current-version regex must compile and have one capture group."
            );
        }

        if (!IsValidRegex(tool.LatestRegex))
        {
            throw new ArgumentException(
                "Latest-version regex must compile and have one capture group."
            );
        }

        if (string.IsNullOrWhiteSpace(tool.UpgradeCommand))
        {
            throw new ArgumentException("Upgrade command is required.");
        }
    }
}
