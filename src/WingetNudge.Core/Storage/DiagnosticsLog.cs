namespace WingetNudge.Core.Storage;

/// <summary>
/// Keeps <c>diagnostics.log</c>: one timestamped line for each state write that failed while the
/// app carried on without it.
/// </summary>
/// <remarks>
/// Every append drops the lines older than the update log's retention window, then the oldest past
/// <see cref="BoundedLog.MaxBytes"/>, so the file stays bounded the way the update log does.
/// </remarks>
public static class DiagnosticsLog
{
    /// <summary>Appends a line, dropping every line past the retention window or the size cap.</summary>
    /// <remarks>
    /// A failure to write is swallowed: a log that cannot be written has nowhere left to report to,
    /// and it must not fail the operation it records.
    /// </remarks>
    /// <param name="paths">Data file locations.</param>
    /// <param name="clock">Time source for the timestamp and the window.</param>
    /// <param name="message">The line. Line breaks become spaces.</param>
    /// <param name="retentionDays">Days a line stays, or <c>null</c> to read the user's setting.</param>
    /// <returns><c>true</c> when the line was written.</returns>
    public static bool Append(DataPaths paths, TimeProvider clock, string message, int? retentionDays = null) =>
        BoundedLog.Append(paths, paths.DiagnosticsLog, clock, message.ReplaceLineEndings(" "), retentionDays);

    /// <summary>
    /// Runs a state write that the caller can go on without. When it fails, the failure joins the
    /// list and gets a line in the log rather than reaching the caller.
    /// </summary>
    /// <param name="paths">Data file locations.</param>
    /// <param name="clock">Time source.</param>
    /// <param name="failures">Receives a failed write, or <c>null</c> to let the failure throw.</param>
    /// <param name="file">The file the write targets.</param>
    /// <param name="action">What the write is for, as <see cref="StateWriteFailure.Action"/> reads.</param>
    /// <param name="write">The write.</param>
    /// <returns><c>true</c> when the write succeeded.</returns>
    internal static bool Attempt(
        DataPaths paths,
        TimeProvider clock,
        ICollection<StateWriteFailure>? failures,
        string file,
        string action,
        Action write
    )
    {
        if (failures is null)
        {
            write();
            return true;
        }

        try
        {
            write();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StateWriteFailure failure = new(Path.GetFileName(file), action, exception.Message);
            failures.Add(failure);
            Append(paths, clock, failure.Summary);
            return false;
        }
    }
}
