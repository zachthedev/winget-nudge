using System.Globalization;
using System.Text;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Upgrade;

/// <summary>One upgrade outcome.</summary>
/// <param name="Timestamp">When the attempt finished.</param>
/// <param name="PackageId">Winget package id.</param>
/// <param name="Result">Outcome label such as <c>upgraded</c>, <c>upgraded-forced</c> or <c>failed</c>.</param>
/// <param name="Status">Winget status or failure reason.</param>
/// <param name="InstallerErrorCode">Exit code the installer returned.</param>
public sealed record UpdateLogEntry(
    DateTimeOffset Timestamp,
    string PackageId,
    string Result,
    string Status,
    long InstallerErrorCode
);

/// <summary>Append-only outcome log kept for 30 days, plus per-failure installer dumps.</summary>
/// <param name="paths">Data file locations.</param>
/// <param name="clock">Time source.</param>
/// <param name="retentionDays">Days an entry stays, or <c>null</c> to read the user's setting.</param>
/// <param name="logsPerPackage">
/// Installer dumps kept per package, or <c>null</c> to read the user's setting.
/// </param>
public sealed class UpdateLog(
    DataPaths paths,
    TimeProvider clock,
    int? retentionDays = null,
    int? logsPerPackage = null
)
{
    private int? _retentionDays = retentionDays;
    private int? _logsPerPackage = logsPerPackage;

    // Reading a setting is a file read, so each happens once rather than per append or dump.
    private int RetentionDays => _retentionDays ??= Settings.Load(paths).LogRetentionDays;

    private int InstallerLogsPerPackage => _logsPerPackage ??= Settings.Load(paths).InstallerLogsPerPackage;

    /// <summary>Reads the log, oldest first.</summary>
    /// <returns>Entries, empty when the file is missing or corrupt.</returns>
    public List<UpdateLogEntry> Load() =>
        JsonFile.Read<List<UpdateLogEntry>>(paths.UpdateLog, deleteIfCorrupt: true) ?? [];

    /// <summary>Appends an outcome and prunes entries past the retention window.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="result">Outcome label.</param>
    /// <param name="status">Winget status or failure reason.</param>
    /// <param name="installerErrorCode">Installer exit code.</param>
    /// <param name="failures">
    /// Receives a failed write, which <c>diagnostics.log</c> also records, or <c>null</c> to let it throw.
    /// </param>
    public void Append(
        string packageId,
        string result,
        string status,
        long installerErrorCode,
        ICollection<StateWriteFailure>? failures = null
    )
    {
        DateTimeOffset now = clock.GetUtcNow();
        UpdateLogEntry appended = new(now, packageId, result, status, installerErrorCode);
        DateTimeOffset cutoff = now.AddDays(-RetentionDays);
        DiagnosticsLog.Attempt(
            paths,
            clock,
            failures,
            paths.UpdateLog,
            $"log the {result} outcome of {packageId}",
            () =>
                JsonFile.Update<List<UpdateLogEntry>>(
                    paths.UpdateLog,
                    deleteIfCorrupt: true,
                    current =>
                    {
                        List<UpdateLogEntry> entries = current ?? [];
                        entries.Add(appended);
                        entries.RemoveAll(entry => entry.Timestamp <= cutoff);
                        return entries;
                    }
                )
        );
    }

    /// <summary>
    /// Writes the full outcome of a failed upgrade to its own file and prunes older dumps past
    /// the per-package limit. The dump just written always stays.
    /// </summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="outcome">The failed attempt.</param>
    /// <param name="diagnostics">Logs winget wrote during the attempt.</param>
    /// <returns>Path of the written file.</returns>
    public string SaveInstallerLog(string packageId, UpgradeOutcome outcome, WingetDiagnostics? diagnostics = null)
    {
        PackageIdValidator.Ensure(packageId);
        return WriteInstallerLog(packageId, outcome, diagnostics);
    }

    /// <summary>
    /// Writes the full outcome of a failed upgrade to its own file, reporting a failed write rather
    /// than throwing it.
    /// </summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="outcome">The failed attempt.</param>
    /// <param name="diagnostics">Logs winget wrote during the attempt.</param>
    /// <param name="failures">Receives a failed write, which <c>diagnostics.log</c> also records.</param>
    /// <returns>Path of the written file, or <c>null</c> when the write failed.</returns>
    internal string? SaveInstallerLog(
        string packageId,
        UpgradeOutcome outcome,
        WingetDiagnostics? diagnostics,
        ICollection<StateWriteFailure> failures
    )
    {
        PackageIdValidator.Ensure(packageId);
        string? file = null;
        DiagnosticsLog.Attempt(
            paths,
            clock,
            failures,
            paths.InstallerLogDirectory,
            $"save the installer log for {packageId}",
            () => file = WriteInstallerLog(packageId, outcome, diagnostics)
        );
        return file;
    }

    private string WriteInstallerLog(string packageId, UpgradeOutcome outcome, WingetDiagnostics? diagnostics)
    {
        DateTimeOffset now = clock.GetUtcNow();
        string stamp = now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string name = $"{packageId}_{stamp}.log";
        string file = Path.Combine(paths.InstallerLogDirectory, name);

        StringBuilder content = new();
        content.AppendLine(CultureInfo.InvariantCulture, $"Package: {packageId}");
        content.AppendLine(CultureInfo.InvariantCulture, $"Timestamp: {now:o}");
        content.AppendLine(CultureInfo.InvariantCulture, $"Status: {outcome.Status}");
        content.AppendLine(CultureInfo.InvariantCulture, $"InstallerErrorCode: {outcome.InstallerErrorCode}");
        if (outcome.ExtendedHResult is int hresult)
        {
            content.AppendLine(CultureInfo.InvariantCulture, $"ExtendedErrorCode: 0x{hresult:X8}");
            if (outcome.ExtendedError is WingetErrorCode code)
            {
                content.AppendLine(CultureInfo.InvariantCulture, $"ExtendedError: {code.Symbol} ({code.Description})");
            }
        }
        else
        {
            content.AppendLine("ExtendedErrorCode: none");
        }

        content.AppendLine(CultureInfo.InvariantCulture, $"RebootRequired: {outcome.RebootRequired}");
        content.AppendLine(CultureInfo.InvariantCulture, $"CorrelationData: {outcome.CorrelationData}");
        AppendDiagnostics(content, diagnostics);

        // The log opens relative to the verified directory and stays open through the prune. Held without delete
        // sharing, it keeps the directory from being emptied and turned into a link, so each delete by path lands
        // inside it.
        using SafeDirectory directory = SafePath.OpenDirectory(paths.InstallerLogDirectory, create: true);
        using FileStream log = directory.OpenFile(name, FileMode.Create, FileAccess.Write, FileShare.Read);
        log.Write(Encoding.UTF8.GetBytes(content.ToString()));

        // The dump just written stays, and takes one of the kept slots, whatever its name sorts as. A clock set back
        // names it older than dumps already there, and the prune would then aim at the handle held above, which
        // refuses the delete.
        IEnumerable<string> stale = Directory
            .EnumerateFiles(paths.InstallerLogDirectory, $"{packageId}_*.log")
            .Where(other => !string.Equals(Path.GetFileName(other), name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static other => other, StringComparer.Ordinal)
            .Skip(InstallerLogsPerPackage - 1);
        foreach (string old in stale)
        {
            File.Delete(old);
        }

        return file;
    }

    /// <summary>Newest installer dump written for a package.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <returns>Full path, or <c>null</c> when the package has no dump.</returns>
    public string? LatestInstallerLog(string packageId)
    {
        if (!PackageIdValidator.IsValid(packageId))
        {
            return null;
        }

        try
        {
            string root = Path.GetFullPath(paths.InstallerLogDirectory);
            return Directory
                .EnumerateFiles(root, $"{packageId}_*.log")
                .Where(name =>
                    Path.GetFullPath(name)
                        .StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                )
                .OrderByDescending(static name => name, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Copies in what winget's own logs said. Winget reports only that the installer exited
    /// non-zero, so without this the sentence that explains the refusal stays buried in App
    /// Installer's diagnostic directory.
    /// </summary>
    /// <param name="content">Dump being built.</param>
    /// <param name="diagnostics">Logs winget wrote, or <c>null</c> when none were collected.</param>
    private static void AppendDiagnostics(StringBuilder content, WingetDiagnostics? diagnostics)
    {
        if (diagnostics is null || diagnostics.Files.Count == 0)
        {
            return;
        }

        if (diagnostics.Summary is string summary)
        {
            content.AppendLine();
            content.AppendLine(CultureInfo.InvariantCulture, $"InstallerSaid: {summary}");
        }

        foreach (string path in diagnostics.Files)
        {
            content.AppendLine();
            content.AppendLine(CultureInfo.InvariantCulture, $"----- {path} -----");
            foreach (string line in WingetDiagnosticsReader.Tail(path))
            {
                content.AppendLine(line);
            }
        }
    }
}
