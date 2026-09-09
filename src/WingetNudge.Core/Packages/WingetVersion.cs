using System.Globalization;

namespace WingetNudge.Core.Packages;

/// <summary>
/// Orders two winget version strings the way winget itself does: dot-separated parts compared by
/// their leading number first and the rest of the text second, missing trailing parts treated as
/// zero, and a decorated version reduced to the number it carries.
/// </summary>
public static class WingetVersion
{
    /// <summary>Compares two versions.</summary>
    /// <param name="left">First version, as winget reports it.</param>
    /// <param name="right">Second version, as winget reports it.</param>
    /// <returns>
    /// Negative when <paramref name="left"/> is older, zero when the two name the same release,
    /// positive when <paramref name="left"/> is newer.
    /// </returns>
    public static int Compare(string? left, string? right)
    {
        (string leftText, int leftBound) = Unwrap(left);
        (string rightText, int rightBound) = Unwrap(right);

        // A bound answers on its own: "< 4.0.0" is below 4.0.0 however the digits line up.
        int digits = CompareParts(leftText, rightText);
        if (digits != 0)
        {
            return digits;
        }

        return leftBound.CompareTo(rightBound);
    }

    /// <summary>Whether one version is a genuine upgrade over another.</summary>
    /// <param name="installed">Version on disk.</param>
    /// <param name="offered">Version winget would install.</param>
    /// <returns><c>true</c> when <paramref name="offered"/> is newer.</returns>
    public static bool IsUpgrade(string? installed, string? offered) =>
        installed is not null
        && offered is not null
        && !IsBounded(installed)
        && !IsBounded(offered)
        // Two strings carrying no number compare as text, which says nothing about release order.
        && (HasDigit(installed) || HasDigit(offered))
        && Compare(installed, offered) < 0;

    private static bool HasDigit(string version) => version.AsSpan().ContainsAnyInRange('0', '9');

    /// <summary>
    /// Whether winget could only bound the version rather than read it. A bound says the version
    /// is unknown, not that it is behind, so nothing may be concluded about an upgrade.
    /// </summary>
    /// <param name="version">Version as winget reports it.</param>
    /// <returns><c>true</c> for a version written as a bound.</returns>
    public static bool IsBounded(string? version)
    {
        string text = (version ?? "").TrimStart();
        return text.StartsWith('<') || text.StartsWith('>');
    }

    /// <summary>
    /// Strips winget's decorations. A leading "&lt;" or "&gt;" marks an installed version winget
    /// could only bound, and a prefix such as "v" or "ad " is the publisher's, not a version.
    /// </summary>
    /// <param name="version">Version as reported.</param>
    /// <returns>The bare version and which side of it the real one sits on.</returns>
    private static (string Text, int Bound) Unwrap(string? version)
    {
        string text = (version ?? "").Trim();
        int bound = 0;
        if (text.StartsWith('<'))
        {
            bound = -1;
            text = text[1..].TrimStart();
        }
        else if (text.StartsWith('>'))
        {
            bound = 1;
            text = text[1..].TrimStart();
        }

        int firstDigit = text.AsSpan().IndexOfAnyInRange('0', '9');
        return (firstDigit <= 0 ? text : text[firstDigit..], bound);
    }

    private static int CompareParts(string left, string right)
    {
        string[] leftParts = Split(left);
        string[] rightParts = Split(right);
        int count = Math.Max(leftParts.Length, rightParts.Length);
        for (int index = 0; index < count; index++)
        {
            // A version that simply stops is padded, so 2.2.0.0 and 2.2.0 are one release.
            string leftPart = index < leftParts.Length ? leftParts[index] : "0";
            string rightPart = index < rightParts.Length ? rightParts[index] : "0";
            int comparison = ComparePart(leftPart, rightPart);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return 0;
    }

    private static string[] Split(string version) =>
        version.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int ComparePart(string left, string right)
    {
        (long leftNumber, string leftSuffix) = SplitLeadingNumber(left);
        (long rightNumber, string rightSuffix) = SplitLeadingNumber(right);
        int byNumber = leftNumber.CompareTo(rightNumber);
        if (byNumber != 0)
        {
            return byNumber;
        }

        // A part with trailing text is a pre-release of the bare number: 1.0-rc precedes 1.0.
        if (leftSuffix.Length == 0 || rightSuffix.Length == 0)
        {
            return rightSuffix.Length.CompareTo(leftSuffix.Length);
        }

        return string.Compare(leftSuffix, rightSuffix, StringComparison.OrdinalIgnoreCase);
    }

    private static (long Number, string Suffix) SplitLeadingNumber(string part)
    {
        int end = 0;
        while (end < part.Length && char.IsAsciiDigit(part[end]))
        {
            end++;
        }

        if (end == 0)
        {
            return (0, part);
        }

        // A part too wide for a long is still a number, and a bigger one than any that fits.
        return long.TryParse(
            part.AsSpan(0, end),
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out long number
        )
            ? (number, part[end..])
            : (long.MaxValue, part[end..]);
    }
}
