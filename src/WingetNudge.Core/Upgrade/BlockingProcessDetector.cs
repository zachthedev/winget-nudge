using System.Diagnostics;
using WingetNudge.Core.Packages;

namespace WingetNudge.Core.Upgrade;

/// <summary>What is holding a package's files, and the session that can close and restart it.</summary>
/// <param name="Processes">Process names, empty when nothing blocks.</param>
/// <param name="Session">
/// Restart Manager session tracking the holders, or <c>null</c> when detection fell back to a
/// process snapshot. The caller owns and disposes it.
/// </param>
public sealed record BlockingDetection(IReadOnlyList<string> Processes, IAppCloseSession? Session)
{
    /// <summary>Nothing is blocking.</summary>
    public static BlockingDetection None { get; } = new([], null);
}

/// <summary>
/// Finds processes that would block an upgrade: Restart Manager over the package's
/// executables first, a path-prefix match over running processes as the fallback.
/// </summary>
public class BlockingProcessDetector
{
    private readonly Dictionary<string, PackageRef> _packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> _directories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _executables = new(
        StringComparer.Ordinal
    );
    private Func<InstallLocationIndex>? _buildIndex;
    private InstallLocationIndex? _index;
    private List<(string Path, string Name)> _snapshot = [];

    /// <summary>Records which packages a run may ask about.</summary>
    /// <remarks>
    /// The registry scan and the walk of an install tree happen on the first
    /// <see cref="Detect"/> for a package, not here. An upgrade that never hits a locked file
    /// never pays for either.
    /// </remarks>
    /// <param name="packages">Packages about to upgrade.</param>
    /// <param name="index">Builds the registry-derived install locations when first needed.</param>
    public void Prepare(IReadOnlyList<PackageRef> packages, Func<InstallLocationIndex> index)
    {
        _buildIndex = index;
        foreach (PackageRef package in packages)
        {
            _packages[package.Id] = package;
        }
    }

    /// <summary>Detects the holders of one package's files.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <returns>Holders and, when Restart Manager answered, the session to close them with.</returns>
    public virtual BlockingDetection Detect(string packageId)
    {
        IReadOnlyList<string> executables = ExecutablesFor(packageId);
        if (executables.Count == 0)
        {
            return BlockingDetection.None;
        }

        RestartManagerSession? session = null;
        try
        {
            session = RestartManagerSession.Start();
            session.RegisterFiles(executables);
            IReadOnlyList<BlockingProcess> blocked = session.GetBlockingProcesses();
            if (blocked.Count > 0)
            {
                return new BlockingDetection(
                    blocked.Select(static process => process.Name).ToArray(),
                    session
                );
            }

            session.Dispose();
            session = null;
        }
        catch (InvalidOperationException)
        {
            session?.Dispose();
            session = null;
        }

        // Fallback: match a process snapshot by install directory.
        if (DirectoryFor(packageId) is not string directory)
        {
            return BlockingDetection.None;
        }

        if (_snapshot.Count == 0)
        {
            _snapshot = SnapshotProcesses();
        }

        string[] names = _snapshot
            .Where(entry => entry.Path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            .Select(static entry => entry.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return names.Length == 0 ? BlockingDetection.None : new BlockingDetection(names, null);
    }

    /// <summary>Closes processes by name: main window first, then kill.</summary>
    /// <param name="processNames">Process names without extension.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Names still running once the kill pass finished.</returns>
    public virtual async Task<IReadOnlyList<string>> CloseByNameAsync(
        IReadOnlyList<string> processNames,
        CancellationToken cancellationToken
    )
    {
        foreach (string name in processNames)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    _ = process.CloseMainWindow();
                }
            }
        }

        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

        foreach (string name in processNames)
        {
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        process.Kill();
                        _ = process.WaitForExit(TimeSpan.FromSeconds(5));
                    }
                    catch (Exception exception)
                        when (exception
                                is InvalidOperationException
                                    or System.ComponentModel.Win32Exception
                        )
                    {
                        // Already gone, or a service this account may not kill.
                    }
                }
            }
        }

        return StillRunning(processNames);
    }

    /// <summary>Whether any of the named processes is still running.</summary>
    /// <param name="processNames">Process names without extension.</param>
    /// <returns>Names still running.</returns>
    public static IReadOnlyList<string> StillRunning(IReadOnlyList<string> processNames)
    {
        HashSet<string> running = new(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                running.Add(process.ProcessName);
            }
        }

        return processNames.Where(running.Contains).ToArray();
    }

    /// <summary>Resolves a package's install directory, reading the registry on first use.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <returns>The directory, or <c>null</c> when the package has no trusted one.</returns>
    private string? DirectoryFor(string packageId)
    {
        if (_directories.TryGetValue(packageId, out string? cached))
        {
            return cached;
        }

        string? directory = null;
        if (_buildIndex is not null && _packages.TryGetValue(packageId, out PackageRef? package))
        {
            _index ??= _buildIndex();
            directory = _index.Resolve(package);
        }

        _directories[packageId] = directory;
        return directory;
    }

    /// <summary>Lists a package's executables, walking its install tree on first use.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <returns>Full paths, empty when the directory is unknown or holds too many.</returns>
    private IReadOnlyList<string> ExecutablesFor(string packageId)
    {
        if (_executables.TryGetValue(packageId, out IReadOnlyList<string>? cached))
        {
            return cached;
        }

        IReadOnlyList<string> executables = DirectoryFor(packageId) is string directory
            ? InstallLocationIndex.EnumerateExecutables(directory)
            : [];
        _executables[packageId] = executables;
        return executables;
    }

    private static List<(string Path, string Name)> SnapshotProcesses()
    {
        List<(string Path, string Name)> snapshot = [];
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (path is not null)
                    {
                        snapshot.Add((path, process.ProcessName));
                    }
                }
                catch (Exception exception)
                    when (exception
                            is InvalidOperationException
                                or System.ComponentModel.Win32Exception
                    )
                {
                    // Protected or exited process; no path to match.
                }
            }
        }

        return snapshot;
    }
}
