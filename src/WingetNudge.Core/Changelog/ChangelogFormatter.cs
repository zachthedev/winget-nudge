using System.Text;
using System.Text.RegularExpressions;

namespace WingetNudge.Core.Changelog;

/// <summary>Strips release-note noise that reads badly as plain text.</summary>
public static partial class ChangelogFormatter
{
    [GeneratedRegex(@"^\s*#{1,3}\s*(Checksums|Asset Hashes)", RegexOptions.IgnoreCase)]
    private static partial Regex ChecksumHeading();

    [GeneratedRegex("[a-f0-9]{40,}")]
    private static partial Regex HexDump();

    [GeneratedRegex(@"^\s*\[!\[.*shields\.io")]
    private static partial Regex Badge();

    [GeneratedRegex(@"<br\s*/?>")]
    private static partial Regex LineBreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTag();

    /// <summary>
    /// Drops checksum blocks, badge images, binary-file notes and HTML tags, and collapses
    /// runs of blank lines.
    /// </summary>
    /// <param name="raw">Markdown release notes.</param>
    /// <returns>Cleaned text, trimmed.</returns>
    public static string Format(string raw)
    {
        StringBuilder output = new();
        bool inChecksums = false;
        bool previousBlank = false;

        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.TrimEnd();

            if (ChecksumHeading().IsMatch(line))
            {
                inChecksums = true;
                continue;
            }

            if (inChecksums)
            {
                if (HexDump().IsMatch(line) || line.Trim().Length == 0)
                {
                    continue;
                }

                inChecksums = false;
            }

            if (Badge().IsMatch(line) || line.Contains("_Binary files inside", StringComparison.Ordinal))
            {
                continue;
            }

            line = LineBreakTag().Replace(line, "");
            line = HtmlTag().Replace(line, "");

            if (line.Trim().Length == 0)
            {
                if (previousBlank)
                {
                    continue;
                }

                previousBlank = true;
            }
            else
            {
                previousBlank = false;
            }

            output.Append(line).Append('\n');
        }

        return output.ToString().Trim();
    }
}
