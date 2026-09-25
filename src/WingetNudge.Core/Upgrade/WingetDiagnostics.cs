using System.Text.RegularExpressions;
using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Upgrade;

/// <summary>What winget's own logs recorded for one failed attempt.</summary>
/// <param name="Summary">The installer's own words for the refusal, or <c>null</c> when none stood out.</param>
/// <param name="Files">Diagnostic files winget wrote during the attempt, newest last.</param>
public sealed record WingetDiagnostics(string? Summary, IReadOnlyList<string> Files)
{
    /// <summary>Winget wrote nothing for the attempt.</summary>
    public static WingetDiagnostics None { get; } = new(null, []);
}

/// <summary>
/// Reads the logs winget leaves behind for a failed install. Winget reports only that the
/// installer exited non-zero; the installer's own log holds the sentence that explains it, and
/// winget drops that log in the same directory.
/// </summary>
/// <remarks>
/// The elevated upgrade window calls this, and the directory sits under a profile any process
/// running as the user can redirect. The directory opens by handle, refusing a reparse point at any
/// step, each file opens relative to that handle and never through a link, and the reads are bounded,
/// so a planted file cannot grow into a privileged read of somewhere else.
/// </remarks>
/// <param name="directory">
/// Diagnostic directory to read, or <c>null</c> for the one App Installer writes to.
/// </param>
public sealed partial class WingetDiagnosticsReader(string? directory = null)
{
    /// <summary>Lines kept from each installer log.</summary>
    public const int TailLines = 60;

    /// <summary>Bytes read from the end of each log to find those lines.</summary>
    public const int TailBytes = 64 * 1024;

    /// <summary>Logs collected from one attempt.</summary>
    public const int MaxFiles = 12;

    /// <summary>Longest summary lifted onto a status line.</summary>
    public const int SummaryLength = 300;

    /// <summary>Lines scanned in one log while looking for a refusal.</summary>
    public const int MaxScannedLines = 20_000;

    // Winget's own logs repeat what the caller already knows; the installer's log is the find.
    [GeneratedRegex(@"^WinGetCOM?-", RegexOptions.IgnoreCase)]
    private static partial Regex WingetOwnLog();

    // Phrases an installer uses when it refuses, ordered by nothing: the first hit wins.
    [GeneratedRegex(
        @"\b(abort(ing|ed)?|cannot be (found|installed)|can't be installed|is not supported|access is denied|requires? a (reboot|restart))\b",
        RegexOptions.IgnoreCase
    )]
    private static partial Regex RefusalPhrase();

    /// <summary>The directory winget writes its diagnostics to.</summary>
    /// <returns>Full path, whether or not it exists.</returns>
    public static string DefaultDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
            "LocalState",
            "DiagOutputDir"
        );

    /// <summary>Collects the logs winget wrote since an attempt began.</summary>
    /// <param name="since">When the attempt started.</param>
    /// <returns>The installer's explanation and the files it came from.</returns>
    public WingetDiagnostics Collect(DateTimeOffset since)
    {
        string root = directory ?? DefaultDirectory();
        if (!Directory.Exists(root))
        {
            return WingetDiagnostics.None;
        }

        List<FileInfo> files;
        string? summary;
        try
        {
            // A redirected directory would have this elevated process read somewhere else.
            using SafeDirectory held = SafePath.OpenDirectory(root, create: false);
            files =
            [
                .. held.GetFiles("*.log")
                    .Where(file => file.LastWriteTimeUtc >= since.UtcDateTime)
                    .Where(static file => !SafePath.IsReparsePoint(file.FullName))
                    .OrderBy(static file => file.LastWriteTimeUtc)
                    .Take(MaxFiles),
            ];
            summary = files
                .Where(static file => !WingetOwnLog().IsMatch(file.Name))
                .Select(file => FindRefusal(held, file.Name))
                .FirstOrDefault(static line => line is not null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return WingetDiagnostics.None;
        }

        return files.Count == 0
            ? WingetDiagnostics.None
            : new WingetDiagnostics(summary, [.. files.Select(static file => file.FullName)]);
    }

    /// <summary>Reads the tail of a diagnostic file.</summary>
    /// <param name="path">Full path of the file.</param>
    /// <returns>The last <see cref="TailLines"/> lines, or an empty list when unreadable.</returns>
    public static IReadOnlyList<string> Tail(string path)
    {
        string full = Path.GetFullPath(path);
        string name = Path.GetFileName(full);
        if (Path.GetDirectoryName(full) is not string parent || name.Length == 0)
        {
            // A volume root, or a path ending in a separator, names no file.
            return [];
        }

        try
        {
            // The directory may have been redirected since Collect listed it, so the read goes through the handle a
            // fresh walk verified.
            using SafeDirectory directory = SafePath.OpenDirectory(parent, create: false);
            using FileStream stream = directory.OpenFile(name, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Only the end of the file can hold the last lines, and a winget log runs to megabytes.
            long start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using StreamReader reader = new(stream);

            // A seek into the middle of the file lands mid-line; that fragment is not a line.
            if (start > 0)
            {
                _ = reader.ReadLine();
            }

            List<string> lines = [];
            while (reader.ReadLine() is string line)
            {
                lines.Add(line);
                if (lines.Count > TailLines)
                {
                    lines.RemoveAt(0);
                }
            }

            return lines;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A name the open refuses, such as a dot entry a device path keeps, is unreadable like any other.
            return [];
        }
    }

    private static string? FindRefusal(SafeDirectory directory, string name)
    {
        try
        {
            using FileStream stream = directory.OpenFile(name, FileMode.Open, FileAccess.Read, FileShare.Read);
            using StreamReader reader = new(stream);
            int scanned = 0;
            while (reader.ReadLine() is string line)
            {
                if (++scanned > MaxScannedLines)
                {
                    break;
                }

                string trimmed = line.Trim();
                if (trimmed.Length == 0 || !RefusalPhrase().IsMatch(trimmed))
                {
                    continue;
                }

                return trimmed.Length <= SummaryLength ? trimmed : trimmed[..SummaryLength].TrimEnd() + "...";
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An installer still holding its log leaves the winget-level reason standing.
        }

        return null;
    }
}
