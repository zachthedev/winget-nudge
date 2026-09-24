namespace WingetNudge.Core.Storage;

/// <summary>A state file write that failed while the operation around it carried on.</summary>
/// <param name="File">Name of the file, such as <c>preferences.json</c>.</param>
/// <param name="Action">What the write was for, as a verb phrase such as <c>drop expired failed entries</c>.</param>
/// <param name="Reason">Why the write failed.</param>
public sealed record StateWriteFailure(string File, string Action, string Reason)
{
    /// <summary>One line naming the file, the write and the reason.</summary>
    public string Summary => $"{File}: could not {Action}. {Reason}";
}
