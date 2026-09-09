using System.Text.RegularExpressions;

namespace WingetNudge.Core.Packages;

/// <summary>Validates winget package identifiers and names arriving from untrusted input.</summary>
public static partial class PackageIdValidator
{
    /// <summary>Longest display name accepted from the command line.</summary>
    public const int MaxNameLength = 256;

    // Allowlist of winget ID characters. '+' is legitimate (Microsoft.VCRedist.2015+.x64,
    // Notepad++.Notepad++). Shell metacharacters stay excluded so an ID from a notification
    // argument cannot reach a command line intact. \A and \z anchor the whole string; $ alone
    // would accept a trailing newline.
    [GeneratedRegex(@"\A[\w][\w.\-+]+\z")]
    private static partial Regex Pattern();

    /// <summary>Whether the value is a well-formed winget package identifier.</summary>
    /// <param name="packageId">Candidate identifier.</param>
    /// <returns><c>true</c> when the identifier is safe to pass on.</returns>
    public static bool IsValid(string packageId) => Pattern().IsMatch(packageId);

    /// <summary>Throws when the identifier is malformed.</summary>
    /// <param name="packageId">Candidate identifier.</param>
    /// <exception cref="ArgumentException">The identifier fails <see cref="IsValid"/>.</exception>
    public static void Ensure(string packageId)
    {
        if (!IsValid(packageId))
        {
            throw new ArgumentException($"'{packageId}' is not a valid winget package id.");
        }
    }

    /// <summary>Whether a display name is safe to carry across the elevation boundary.</summary>
    /// <param name="name">Candidate name; empty is allowed.</param>
    /// <returns><c>true</c> when the name is within length and holds no control characters.</returns>
    public static bool IsValidName(string name) =>
        name.Length <= MaxNameLength && !name.Any(static c => char.IsControl(c));

    /// <summary>Strips control characters and trims a display name to the accepted length.</summary>
    /// <param name="name">Display name from winget.</param>
    /// <returns>A name that passes <see cref="IsValidName"/>.</returns>
    public static string SanitizeName(string name)
    {
        string clean = new(name.Where(static c => !char.IsControl(c)).ToArray());
        return clean.Length > MaxNameLength ? clean[..MaxNameLength] : clean;
    }

    /// <summary>Throws when a display name is unsafe.</summary>
    /// <param name="name">Candidate name.</param>
    /// <exception cref="ArgumentException">The name fails <see cref="IsValidName"/>.</exception>
    public static void EnsureName(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Package name is too long or contains control characters.");
        }
    }
}
