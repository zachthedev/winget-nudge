using WingetNudge.Core.Packages;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Tracking;
using WingetNudge.Core.Upgrade;

namespace WingetNudge.Core.Tests.Support;

public sealed class FakeProcessRunner : IProcessRunner
{
    private readonly Dictionary<string, ProcessOutput> _outputs = new(StringComparer.OrdinalIgnoreCase);

    public FakeProcessRunner Map(string executable, string output, int exitCode = 0)
    {
        _outputs[executable] = new ProcessOutput(exitCode, output);
        return this;
    }

    public Task<ProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    ) =>
        _outputs.TryGetValue(executable, out ProcessOutput? output)
            ? Task.FromResult(output)
            : throw new System.ComponentModel.Win32Exception(2, $"'{executable}' not found");
}

public sealed class FakePackageSource(IReadOnlyList<PackageInfo> packages) : IPackageSource
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(packages);
    }
}

public sealed class FakeUpgrader : IPackageUpgrader
{
    private readonly Dictionary<(string Id, UpgradeMode Mode), UpgradeOutcome> _outcomes = [];
    private readonly Dictionary<(string Id, UpgradeMode Mode), Queue<UpgradeOutcome>> _sequences = [];
    private readonly Dictionary<string, int> _unavailableAfter = new(StringComparer.Ordinal);

    public List<(string Id, UpgradeMode Mode)> Attempts { get; } = [];

    public FakeUpgrader On(string id, UpgradeMode mode, UpgradeOutcome outcome)
    {
        _outcomes[(id, mode)] = outcome;
        return this;
    }

    /// <summary>
    /// Answers one mode differently per attempt, which is what the ladder needs once it retries
    /// the same mode after closing the apps holding a file. The last outcome repeats.
    /// </summary>
    public FakeUpgrader OnEach(string id, UpgradeMode mode, params UpgradeOutcome[] outcomes)
    {
        _sequences[(id, mode)] = new Queue<UpgradeOutcome>(outcomes);
        return this;
    }

    /// <param name="afterAttempts">
    /// Attempts on this package that answer normally before winget disappears. Zero means it is
    /// already gone.
    /// </param>
    public FakeUpgrader WingetGoneFor(string id, int afterAttempts = 0)
    {
        _unavailableAfter[id] = afterAttempts;
        return this;
    }

    public Task<UpgradeOutcome> UpgradeAsync(
        string packageId,
        UpgradeMode mode,
        IProgress<UpgradeProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        Attempts.Add((packageId, mode));
        if (
            _unavailableAfter.TryGetValue(packageId, out int after)
            && Attempts.Count(attempt => attempt.Id == packageId) > after
        )
        {
            throw new WingetUnavailableException("winget COM server unreachable (0x80040154)");
        }

        progress?.Report(new UpgradeProgress(UpgradePhase.Installing, 0.5));
        if (_sequences.TryGetValue((packageId, mode), out Queue<UpgradeOutcome>? queue) && queue.Count > 0)
        {
            return Task.FromResult(queue.Count == 1 ? queue.Peek() : queue.Dequeue());
        }

        return Task.FromResult(
            _outcomes.TryGetValue((packageId, mode), out UpgradeOutcome? outcome)
                ? outcome
                : UpgradeOutcome.Failed("no canned outcome")
        );
    }
}

public sealed class RecordingInteraction(CloseAppsDecision decision = CloseAppsDecision.Close) : IUpgradeInteraction
{
    public List<UpgradeEvent> Events { get; } = [];

    public List<(string Id, IReadOnlyList<string> Processes)> Prompts { get; } = [];

    public Action? OnFinished { get; set; }

    public Task<CloseAppsDecision> AskCloseAppsAsync(
        string packageId,
        IReadOnlyList<string> processNames,
        CancellationToken cancellationToken
    )
    {
        Prompts.Add((packageId, processNames));
        return Task.FromResult(decision);
    }

    public void Report(UpgradeEvent upgradeEvent)
    {
        Events.Add(upgradeEvent);
        if (upgradeEvent is UpgradeEvent.Finished)
        {
            OnFinished?.Invoke();
        }
    }
}

public sealed class FakeReleaseDates : IReleaseDateResolver
{
    private readonly Dictionary<string, ResolvedDate?> _dates = new(StringComparer.Ordinal);

    public List<string> Requests { get; } = [];

    public FakeReleaseDates Map(string packageId, string version, ResolvedDate? date)
    {
        _dates[$"{packageId}@{version}"] = date;
        return this;
    }

    public Task<ResolvedDate?> ResolveAsync(string packageId, string version, CancellationToken cancellationToken)
    {
        string key = $"{packageId}@{version}";
        Requests.Add(key);
        return Task.FromResult(_dates.TryGetValue(key, out ResolvedDate? date) ? date : null);
    }
}

public static class Fixture
{
    public static PackageInfo Updatable(string id, string installed, string available, string? name = null) =>
        new(id, name ?? id, installed, available, IsUpdateAvailable: true);

    public static PackageInfo Current(string id, string installed) =>
        new(id, id, installed, installed, IsUpdateAvailable: false);

    public static UpgradeOutcome Ok() => new(true, "Ok", 0, null, false, "");

    public static UpgradeOutcome Fail(string status = "InstallError", uint exit = 1, int? hresult = null) =>
        new(false, status, exit, hresult, false, "");
}

public sealed class FakeDeElevatedUpgrader(long exitCode) : IDeElevatedUpgrader
{
    private readonly Exception? _failure;

    public FakeDeElevatedUpgrader(Exception failure)
        : this(0) => _failure = failure;

    public List<string> Calls { get; } = [];

    public Task<long> UpgradeAsync(string packageId, CancellationToken cancellationToken)
    {
        Calls.Add(packageId);
        return _failure is null ? Task.FromResult(exitCode) : Task.FromException<long>(_failure);
    }
}

public sealed class FakeCloseSession(ShutdownResult result) : IAppCloseSession
{
    public bool ClosedHolders { get; private set; }

    public int ShutdownCalls { get; private set; }

    public int RestartCalls { get; private set; }

    public bool Disposed { get; private set; }

    public ShutdownResult Shutdown()
    {
        ShutdownCalls++;
        ClosedHolders = result.Closed;
        return result;
    }

    public void Restart() => RestartCalls++;

    public void Dispose() => Disposed = true;
}

public sealed class FakeBlockingDetector : BlockingProcessDetector
{
    private readonly Dictionary<string, Func<BlockingDetection>> _detections = new(StringComparer.Ordinal);

    public List<IReadOnlyList<string>> DirectCloses { get; } = [];

    public IReadOnlyList<string> Survivors { get; set; } = [];

    public FakeBlockingDetector Blocks(string packageId, BlockingDetection detection)
    {
        _detections[packageId] = () => detection;
        return this;
    }

    public FakeBlockingDetector Throws(string packageId, Exception exception)
    {
        _detections[packageId] = () => throw exception;
        return this;
    }

    public override BlockingDetection Detect(string packageId) =>
        _detections.TryGetValue(packageId, out Func<BlockingDetection>? detection)
            ? detection()
            : BlockingDetection.None;

    public override Task<IReadOnlyList<string>> CloseByNameAsync(
        IReadOnlyList<string> processNames,
        CancellationToken cancellationToken
    )
    {
        DirectCloses.Add(processNames);
        return Task.FromResult(Survivors);
    }
}
