using WingetNudge.Core.Changelog;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Preferences;

namespace WingetNudge.Core.Upgrade;

/// <summary>What the user chose when apps block an upgrade.</summary>
public enum CloseAppsDecision
{
    /// <summary>Close the apps, upgrade, then restart them.</summary>
    Close,

    /// <summary>Leave the package alone.</summary>
    Skip,

    /// <summary>The apps exited on their own; proceed without closing anything.</summary>
    AlreadyClosed,
}

/// <summary>Outcome of one package's upgrade.</summary>
public enum PackageResult
{
    /// <summary>Upgraded.</summary>
    Upgraded,

    /// <summary>Skipped by the user.</summary>
    Skipped,

    /// <summary>Every attempt failed.</summary>
    Failed,
}

/// <summary>Something the engine wants shown.</summary>
public abstract record UpgradeEvent
{
    private UpgradeEvent() { }

    /// <summary>A run-level phase such as "Preparing" began.</summary>
    /// <param name="Text">Phase description.</param>
    public sealed record Phase(string Text) : UpgradeEvent;

    /// <summary>Release notes for the whole run, fetched before the first package starts.</summary>
    /// <param name="Notes">Notes by winget package id; a package absent from the map has none.</param>
    public sealed record Changelogs(IReadOnlyDictionary<string, string> Notes) : UpgradeEvent;

    /// <summary>A package's upgrade started.</summary>
    /// <param name="Package">The package.</param>
    public sealed record Started(PackageRef Package) : UpgradeEvent;

    /// <summary>Winget progress for the current package.</summary>
    /// <param name="PackageId">Winget package id.</param>
    /// <param name="Snapshot">Progress snapshot.</param>
    public sealed record Progress(string PackageId, UpgradeProgress Snapshot) : UpgradeEvent;

    /// <summary>A status line for the current package.</summary>
    /// <param name="PackageId">Winget package id.</param>
    /// <param name="Text">Status text.</param>
    public sealed record Message(string PackageId, string Text) : UpgradeEvent;

    /// <summary>A package's upgrade finished.</summary>
    /// <param name="PackageId">Winget package id.</param>
    /// <param name="Result">Outcome.</param>
    /// <param name="Detail">Final status text.</param>
    /// <param name="LogPath">Installer log written for a failure, or <c>null</c>.</param>
    public sealed record Finished(string PackageId, PackageResult Result, string Detail, string? LogPath = null)
        : UpgradeEvent;
}

/// <summary>Decisions and display the engine delegates to the host.</summary>
public interface IUpgradeInteraction
{
    /// <summary>Asks whether to close the apps holding a package's files.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="processNames">Blocking process names.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The user's decision.</returns>
    Task<CloseAppsDecision> AskCloseAppsAsync(
        string packageId,
        IReadOnlyList<string> processNames,
        CancellationToken cancellationToken
    );

    /// <summary>Displays an engine event.</summary>
    /// <remarks>Arrives on the engine's thread or on winget's callback thread; the host marshals.</remarks>
    /// <param name="upgradeEvent">The event.</param>
    void Report(UpgradeEvent upgradeEvent);
}

/// <summary>Totals for a run.</summary>
/// <param name="Upgraded">Packages upgraded.</param>
/// <param name="Skipped">Packages the user skipped.</param>
/// <param name="Failed">Packages that failed every attempt.</param>
/// <param name="Canceled">Whether the run stopped early.</param>
/// <param name="AbortReason">Why the run stopped without attempting the rest, or <c>null</c>.</param>
public sealed record UpgradeSummary(int Upgraded, int Skipped, int Failed, bool Canceled, string? AbortReason = null);

/// <summary>
/// Upgrades a list of packages: finds and closes blocking apps, retries interactively for
/// locked files, then by force, then without elevation for installers that refuse it, and
/// records every outcome.
/// </summary>
/// <param name="source">Package inventory, for versions.</param>
/// <param name="upgrader">Runs winget upgrades.</param>
/// <param name="preferences">Failed and muted state.</param>
/// <param name="log">Outcome log.</param>
/// <param name="changelogs">Release notes.</param>
/// <param name="detector">Blocking process detection.</param>
/// <param name="ui">Host callbacks.</param>
/// <param name="locations">Install location lookup, or <c>null</c> to read the registry.</param>
/// <param name="diagnostics">Winget log reader, or <c>null</c> to read App Installer's directory.</param>
/// <param name="clock">Time source, or <c>null</c> for the system clock.</param>
/// <param name="deElevated">Non-elevated upgrade route, or <c>null</c> to use a scheduled task.</param>
public sealed class UpgradeEngine(
    IPackageSource source,
    IPackageUpgrader upgrader,
    PreferenceStore preferences,
    UpdateLog log,
    ChangelogFetcher changelogs,
    BlockingProcessDetector detector,
    IUpgradeInteraction ui,
    Func<InstallLocationIndex>? locations = null,
    WingetDiagnosticsReader? diagnostics = null,
    TimeProvider? clock = null,
    IDeElevatedUpgrader? deElevated = null
)
{
    /// <summary>Upgrades the packages in order.</summary>
    /// <param name="packages">Packages to upgrade.</param>
    /// <param name="cancellationToken">Stops before the next package; the current install finishes.</param>
    /// <returns>Run totals, once every event is reported.</returns>
    public async Task<UpgradeSummary> RunAsync(IReadOnlyList<PackageRef> packages, CancellationToken cancellationToken)
    {
        int upgraded = 0;
        int skipped = 0;
        int failed = 0;

        // ///// Preparation /////

        ui.Report(new UpgradeEvent.Phase("Preparing"));
        detector.Prepare(packages, locations ?? InstallLocationIndex.Build);

        ui.Report(new UpgradeEvent.Phase("Fetching changelogs"));
        IReadOnlyDictionary<string, string> notes;
        try
        {
            notes = await FetchChangelogsAsync(packages, cancellationToken).ConfigureAwait(false);
        }
        catch (WingetUnavailableException exception)
        {
            return Abort(packages, 0, exception, upgraded, skipped, failed);
        }

        ui.Report(new UpgradeEvent.Changelogs(notes));

        // ///// Per-package loop /////

        for (int position = 0; position < packages.Count; position++)
        {
            PackageRef package = packages[position];
            if (cancellationToken.IsCancellationRequested)
            {
                return new UpgradeSummary(upgraded, skipped, failed, Canceled: true);
            }

            PackageResult result;
            try
            {
                result = await UpgradeOneAsync(package, cancellationToken).ConfigureAwait(false);
            }
            catch (WingetUnavailableException exception)
            {
                // Nothing downstream can succeed, and the packages keep their prior state.
                return Abort(packages, position, exception, upgraded, skipped, failed);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // One package's collapse leaves the rest of the run standing.
                result = RecordHostFailure(package.Id, exception);
            }

            switch (result)
            {
                case PackageResult.Upgraded:
                    upgraded++;
                    break;
                case PackageResult.Failed:
                    failed++;
                    break;
                case PackageResult.Skipped:
                    skipped++;
                    break;
                default:
                    throw new System.Diagnostics.UnreachableException();
            }
        }

        return new UpgradeSummary(upgraded, skipped, failed, Canceled: false);
    }

    /// <summary>
    /// Ends the run without touching the packages it never tried. They stay unmarked, so the
    /// picker offers them again once winget answers.
    /// </summary>
    private UpgradeSummary Abort(
        IReadOnlyList<PackageRef> packages,
        int startIndex,
        WingetUnavailableException exception,
        int upgraded,
        int skipped,
        int failed
    )
    {
        ui.Report(new UpgradeEvent.Phase("winget is unavailable"));
        for (int index = startIndex; index < packages.Count; index++)
        {
            ui.Report(
                new UpgradeEvent.Finished(
                    packages[index].Id,
                    PackageResult.Skipped,
                    "not attempted: winget is unavailable"
                )
            );
            skipped++;
        }

        return new UpgradeSummary(upgraded, skipped, failed, Canceled: false, exception.Message);
    }

    private async Task<PackageResult> UpgradeOneAsync(PackageRef package, CancellationToken cancellationToken)
    {
        string id = package.Id;
        ui.Report(new UpgradeEvent.Started(package));

        // The holders are found only if winget reports a locked file, so the session is opened
        // inside the ladder and closed here whatever route the ladder took.
        HolderHandle holder = new();
        try
        {
            return await UpgradeWithRetriesAsync(id, holder, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // The apps come back whatever the upgrade did, including when it never ran. Leaving
            // a user's editor closed because winget went away is worse than the failed upgrade.
            RestartHolders(id, holder.Session);
            holder.Dispose();
        }
    }

    /// <summary>The Restart Manager session a package's upgrade opened, if it opened one.</summary>
    private sealed class HolderHandle : IDisposable
    {
        /// <summary>The open session, or <c>null</c> when nothing was ever detected.</summary>
        public IAppCloseSession? Session { get; set; }

        /// <inheritdoc/>
        public void Dispose() => Session?.Dispose();
    }

    /// <summary>
    /// Finds and closes whatever holds a package's files, once winget has said a file is locked.
    /// </summary>
    /// <param name="id">Winget package id.</param>
    /// <param name="holder">Receives the session, so the caller can restart and dispose it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What the ladder should do next.</returns>
    /// <exception cref="OperationCanceledException">The run was canceled.</exception>
    private async Task<HolderOutcome> TryClearHoldersAsync(
        string id,
        HolderHandle holder,
        CancellationToken cancellationToken
    )
    {
        BlockingDetection detection = detector.Detect(id);
        holder.Session = detection.Session;
        if (detection.Processes.Count == 0)
        {
            return HolderOutcome.NothingHeld;
        }

        CloseAppsDecision decision = await ui.AskCloseAppsAsync(id, detection.Processes, cancellationToken)
            .ConfigureAwait(false);
        if (decision == CloseAppsDecision.Skip)
        {
            return HolderOutcome.UserSkipped;
        }

        if (decision == CloseAppsDecision.Close)
        {
            await CloseBlockersAsync(id, detection, cancellationToken).ConfigureAwait(false);
        }

        return HolderOutcome.Cleared;
    }

    /// <summary>What clearing the holders of a locked file settled.</summary>
    private enum HolderOutcome
    {
        /// <summary>Nothing held the files; the ladder carries on unchanged.</summary>
        NothingHeld,

        /// <summary>The holders are gone, so a silent attempt is worth repeating.</summary>
        Cleared,

        /// <summary>The user chose to leave the package alone.</summary>
        UserSkipped,
    }

    private void RestartHolders(string id, IAppCloseSession? session)
    {
        if (session is not { ClosedHolders: true })
        {
            return;
        }

        ui.Report(new UpgradeEvent.Message(id, "Restarting applications"));
        try
        {
            session.Restart();
        }
        catch (InvalidOperationException exception)
        {
            ui.Report(new UpgradeEvent.Message(id, $"restart failed: {exception.Message}"));
        }
    }

    /// <summary>
    /// Closes the apps holding a package's files. Restart Manager refuses when a holder is a
    /// service or a critical process, so the names go to a direct close after that, and an
    /// upgrade the survivors still block falls through to the retry ladder.
    /// </summary>
    private async Task CloseBlockersAsync(string id, BlockingDetection detection, CancellationToken cancellationToken)
    {
        ui.Report(new UpgradeEvent.Message(id, "Closing applications"));
        if (detection.Session is IAppCloseSession session)
        {
            ShutdownResult shutdown = await Task.Run(session.Shutdown, cancellationToken).ConfigureAwait(false);
            if (shutdown.Closed)
            {
                return;
            }

            ui.Report(new UpgradeEvent.Message(id, $"{shutdown.Reason}; closing them directly"));
        }

        IReadOnlyList<string> stubborn = await detector
            .CloseByNameAsync(detection.Processes, cancellationToken)
            .ConfigureAwait(false);
        if (stubborn.Count > 0)
        {
            ui.Report(new UpgradeEvent.Message(id, $"Still running: {string.Join(", ", stubborn)}; upgrading anyway"));
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> FetchChangelogsAsync(
        IReadOnlyList<PackageRef> packages,
        CancellationToken cancellationToken
    )
    {
        try
        {
            HashSet<string> ids = packages.Select(static package => package.Id).ToHashSet(StringComparer.Ordinal);
            IReadOnlyList<PackageInfo> inventory = await source
                .GetInstalledAsync(cancellationToken)
                .ConfigureAwait(false);
            PackageInfo[] targets = inventory
                .Where(package => ids.Contains(package.Id) && package.IsUpdateAvailable)
                .ToArray();
            return await changelogs.FetchAsync(targets, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not WingetUnavailableException
                && (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            )
        {
            ui.Report(new UpgradeEvent.Phase($"Changelogs unavailable: {exception.Message}"));
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private async Task<PackageResult> UpgradeWithRetriesAsync(
        string id,
        HolderHandle holder,
        CancellationToken cancellationToken
    )
    {
        ProgressRelay progress = new(ui, id);
        DateTimeOffset startedAt = (clock ?? TimeProvider.System).GetUtcNow();

        UpgradeOutcome outcome = await AttemptAsync(id, UpgradeMode.Silent, progress, cancellationToken)
            .ConfigureAwait(false);
        if (outcome.Succeeded)
        {
            return RecordSuccess(id, "upgraded", "upgraded");
        }

        ui.Report(new UpgradeEvent.Message(id, $"failed: {outcome.Reason}"));

        // Locked files: winget has now named the problem, so it is worth finding the holders
        // and offering to close them. Asking before this point interrupts every upgrade whose
        // app merely happens to be running.
        if (outcome.IsFilesInUse)
        {
            HolderOutcome holders = await TryClearHoldersAsync(id, holder, cancellationToken).ConfigureAwait(false);
            if (holders == HolderOutcome.UserSkipped)
            {
                ui.Report(new UpgradeEvent.Finished(id, PackageResult.Skipped, "skipped by user"));
                return PackageResult.Skipped;
            }

            if (holders == HolderOutcome.Cleared)
            {
                UpgradeOutcome afterClose = await AttemptAsync(id, UpgradeMode.Silent, progress, cancellationToken)
                    .ConfigureAwait(false);
                if (afterClose.Succeeded)
                {
                    return RecordSuccess(id, "upgraded", "upgraded");
                }

                ui.Report(new UpgradeEvent.Message(id, $"failed: {afterClose.Reason}"));
            }

            ui.Report(
                new UpgradeEvent.Message(id, "Retrying interactively; the installer will show which apps to close")
            );
            UpgradeOutcome interactive = await AttemptAsync(id, UpgradeMode.Interactive, progress, cancellationToken)
                .ConfigureAwait(false);
            if (interactive.Succeeded)
            {
                return RecordSuccess(id, "upgraded-interactive", "upgraded (interactive)");
            }
        }

        ui.Report(new UpgradeEvent.Message(id, "Retrying with force"));
        UpgradeOutcome forced = await AttemptAsync(id, UpgradeMode.Force, progress, cancellationToken)
            .ConfigureAwait(false);
        if (forced.Succeeded)
        {
            return RecordSuccess(id, "upgraded-forced", "upgraded (forced)");
        }

        // Store and MSIX installers refuse an elevated caller.
        if (forced.RefusesElevation)
        {
            ui.Report(new UpgradeEvent.Message(id, "Needs a non-admin context; retrying without elevation"));
            long exit;
            try
            {
                exit = await (deElevated ?? new DeElevatedRunner())
                    .UpgradeAsync(id, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                ui.Report(new UpgradeEvent.Message(id, $"scheduled task error: {exception.Message}"));
                exit = -1;
            }

            if (exit == 0)
            {
                return RecordSuccess(id, "upgraded-deelevated", "upgraded (de-elevated)");
            }

            // The scheduled run reports winget's own code; without it the row says only that
            // something failed, when the answer is usually "close the app first".
            // The code is a 32-bit HRESULT; widening it to long would print eight stray F's.
            int hresult = unchecked((int)exit);
            WingetErrorCode? code = WingetErrorCodes.Find(hresult);
            string reason = code is null
                ? $"needs non-admin; the de-elevated retry failed (0x{hresult:X8})"
                : $"needs non-admin; {code.Description} (0x{hresult:X8})";
            return RecordFailure(id, outcome, reason, exit, startedAt);
        }

        return RecordFailure(
            id,
            outcome,
            outcome.Reason,
            forced.ExtendedHResult ?? (long)forced.InstallerErrorCode,
            startedAt
        );
    }

    /// <summary>
    /// Hands a winget progress snapshot to the host on the thread that reports it. The host
    /// applies events in arrival order, so a package's last snapshot has to land before its
    /// <see cref="UpgradeEvent.Finished"/>, and none may still be in flight once
    /// <see cref="RunAsync"/> returns. A callback posted to a scheduler guarantees neither.
    /// </summary>
    private sealed class ProgressRelay(IUpgradeInteraction ui, string id) : IProgress<UpgradeProgress>
    {
        /// <inheritdoc/>
        public void Report(UpgradeProgress value) => ui.Report(new UpgradeEvent.Progress(id, value));
    }

    private async Task<UpgradeOutcome> AttemptAsync(
        string id,
        UpgradeMode mode,
        IProgress<UpgradeProgress> progress,
        CancellationToken cancellationToken
    )
    {
        try
        {
            return await upgrader.UpgradeAsync(id, mode, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException and not WingetUnavailableException)
        {
            return UpgradeOutcome.Failed(exception.Message);
        }
    }

    private PackageResult RecordSuccess(string id, string resultLabel, string detail)
    {
        preferences.Clear(id);
        log.Append(id, resultLabel, "Ok", 0);
        ui.Report(new UpgradeEvent.Finished(id, PackageResult.Upgraded, detail));
        return PackageResult.Upgraded;
    }

    private PackageResult RecordHostFailure(string id, Exception exception)
    {
        string reason = $"upgrade error: {exception.Message}";
        preferences.Set(id, PreferenceState.Failed, reason);
        log.Append(id, "failed", reason, 0);
        ui.Report(new UpgradeEvent.Finished(id, PackageResult.Failed, reason));
        return PackageResult.Failed;
    }

    private PackageResult RecordFailure(
        string id,
        UpgradeOutcome outcome,
        string reason,
        long errorCode,
        DateTimeOffset startedAt
    )
    {
        WingetDiagnostics found = (diagnostics ?? new WingetDiagnosticsReader()).Collect(startedAt);
        string detail = found.Summary is string summary ? $"{reason}\n{summary}" : reason;
        string file = log.SaveInstallerLog(id, outcome, found);
        preferences.Set(id, PreferenceState.Failed, detail);
        log.Append(id, "failed", detail, errorCode);
        ui.Report(new UpgradeEvent.Finished(id, PackageResult.Failed, detail, file));
        return PackageResult.Failed;
    }
}
