namespace WingetNudge.Core.Packages;

/// <summary>How an upgrade attempt runs.</summary>
public enum UpgradeMode
{
    /// <summary>Silent install with winget's default options.</summary>
    Silent,

    /// <summary>Installer shows its own UI, so it can name the apps it needs closed.</summary>
    Interactive,

    /// <summary>Silent install with winget's force flag.</summary>
    Force,
}

/// <summary>Phase of a running upgrade.</summary>
public enum UpgradePhase
{
    /// <summary>Waiting for winget to start.</summary>
    Queued,

    /// <summary>Installer download in progress.</summary>
    Downloading,

    /// <summary>Installer running.</summary>
    Installing,

    /// <summary>Post-install steps running.</summary>
    Finishing,
}

/// <summary>Progress snapshot from a running upgrade.</summary>
/// <param name="Phase">Current phase.</param>
/// <param name="Fraction">Completion of the current phase from 0 to 1, or <c>null</c> when unknown.</param>
public sealed record UpgradeProgress(UpgradePhase Phase, double? Fraction);

/// <summary>Result of one upgrade attempt.</summary>
/// <param name="Succeeded">Whether winget reported success.</param>
/// <param name="Status">Winget's status name, for example <c>Ok</c> or <c>InstallError</c>.</param>
/// <param name="InstallerErrorCode">Exit code the installer returned.</param>
/// <param name="ExtendedHResult">HRESULT of winget's extended error, or <c>null</c> when none.</param>
/// <param name="RebootRequired">Whether the installer asked for a reboot.</param>
/// <param name="CorrelationData">Winget correlation data for the attempt.</param>
public sealed record UpgradeOutcome(
    bool Succeeded,
    string Status,
    uint InstallerErrorCode,
    int? ExtendedHResult,
    bool RebootRequired,
    string CorrelationData
)
{
    /// <summary>Winget's status name, trimmed; an exception message arrives with a trailing newline.</summary>
    public string Status { get; init; } = Status.Trim();

    /// <summary>Installer exit code that means files are locked by a running app.</summary>
    public const uint FilesInUseExitCode = 6;

    /// <summary>Winget HRESULT for an install blocked by files in use.</summary>
    public const int FilesInUseHResult = unchecked((int)0x8A150111);

    /// <summary>Winget HRESULT when the installer refuses an elevated context.</summary>
    public const int RefusesElevationHResult = unchecked((int)0x8A150056);

    /// <summary>Whether the failure was caused by files locked by a running app.</summary>
    public bool IsFilesInUse => InstallerErrorCode == FilesInUseExitCode || ExtendedHResult == FilesInUseHResult;

    /// <summary>Whether the installer refused to run because the caller is elevated.</summary>
    public bool RefusesElevation => ExtendedHResult == RefusesElevationHResult;

    /// <summary>Winget's own description of the extended error, or <c>null</c>.</summary>
    public WingetErrorCode? ExtendedError => ExtendedHResult is int hresult ? WingetErrorCodes.Find(hresult) : null;

    /// <summary>Human-readable failure reason, with winget's codes translated.</summary>
    public string Reason
    {
        get
        {
            string reason = Status;
            if (InstallerErrorCode != 0)
            {
                reason += $" (exit code {InstallerErrorCode})";
            }

            if (ExtendedError is WingetErrorCode code)
            {
                reason += $": {code.Description}";
            }
            else if (InstallerErrorCode == FilesInUseExitCode)
            {
                reason += ": files are locked by a running app";
            }

            if (ExtendedHResult is int hresult)
            {
                reason += $" (0x{hresult:X8})";
            }

            return reason;
        }
    }

    /// <summary>Outcome for an attempt that failed before winget ran.</summary>
    /// <param name="status">Failure description.</param>
    /// <returns>A failed outcome.</returns>
    public static UpgradeOutcome Failed(string status) => new(false, status, 0, null, false, "");
}

/// <summary>Runs winget upgrades.</summary>
public interface IPackageUpgrader
{
    /// <summary>Upgrades one package.</summary>
    /// <param name="packageId">Winget package identifier.</param>
    /// <param name="mode">How the attempt runs.</param>
    /// <param name="progress">Receives progress updates, or <c>null</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The attempt's outcome. Never throws for winget-reported failures.</returns>
    Task<UpgradeOutcome> UpgradeAsync(
        string packageId,
        UpgradeMode mode,
        IProgress<UpgradeProgress>? progress,
        CancellationToken cancellationToken
    );
}
