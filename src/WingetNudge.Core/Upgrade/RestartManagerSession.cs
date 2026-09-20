using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.RestartManager;

namespace WingetNudge.Core.Upgrade;

/// <summary>A process holding files that an upgrade needs to replace.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="Name">Process name without extension, or the Restart Manager's app name.</param>
public sealed record BlockingProcess(uint ProcessId, string Name);

/// <summary>Outcome of one attempt to close the apps holding a package's files.</summary>
/// <param name="Closed">Whether the holders closed.</param>
/// <param name="Reason">Why the attempt was refused; empty when <paramref name="Closed"/> is <c>true</c>.</param>
public sealed record ShutdownResult(bool Closed, string Reason)
{
    /// <summary>The holders closed.</summary>
    public static ShutdownResult Success { get; } = new(true, "");

    /// <summary>The holders stayed up.</summary>
    /// <param name="reason">Why the attempt was refused.</param>
    /// <returns>A refused result.</returns>
    public static ShutdownResult Refused(string reason) => new(false, reason);
}

/// <summary>Closes the apps holding a package's files and restarts them once it is upgraded.</summary>
public interface IAppCloseSession : IDisposable
{
    /// <summary>Whether <see cref="Shutdown"/> closed the holders, so <see cref="Restart"/> has something to bring back.</summary>
    bool ClosedHolders { get; }

    /// <summary>Closes the holders.</summary>
    /// <returns>Whether they closed, and why not when they did not.</returns>
    ShutdownResult Shutdown();

    /// <summary>Restarts the apps <see cref="Shutdown"/> closed.</summary>
    /// <exception cref="InvalidOperationException">The apps could not be restarted.</exception>
    void Restart();
}

/// <summary>
/// One Restart Manager session: register the files an upgrade touches, list the processes
/// holding them, shut those down, and restart them afterwards.
/// </summary>
public sealed class RestartManagerSession : IAppCloseSession
{
    private const int SessionKeyLength = 32 + 1;

    private uint _handle;
    private bool _disposed;

    private RestartManagerSession(uint handle)
    {
        _handle = handle;
    }

    /// <inheritdoc/>
    public bool ClosedHolders { get; private set; }

    /// <summary>Opens a session.</summary>
    /// <returns>The session.</returns>
    /// <exception cref="InvalidOperationException">Restart Manager refused the session.</exception>
    public static RestartManagerSession Start()
    {
        Span<char> key = stackalloc char[SessionKeyLength];
        WIN32_ERROR error = PInvoke.RmStartSession(out uint handle, key);
        if (error != WIN32_ERROR.NO_ERROR)
        {
            throw new InvalidOperationException($"RmStartSession failed: {error}");
        }

        return new RestartManagerSession(handle);
    }

    /// <summary>Registers files whose holders the session tracks.</summary>
    /// <param name="paths">Full file paths.</param>
    /// <exception cref="InvalidOperationException">Restart Manager rejected the files.</exception>
    public void RegisterFiles(IReadOnlyList<string> paths)
    {
        ThrowIfDisposed();
        if (paths.Count == 0)
        {
            return;
        }

        string[] names = [.. paths];
        WIN32_ERROR error = PInvoke.RmRegisterResources(_handle, names, [], []);
        if (error != WIN32_ERROR.NO_ERROR)
        {
            throw new InvalidOperationException($"RmRegisterResources failed: {error}");
        }
    }

    /// <summary>Lists the processes holding registered files.</summary>
    /// <returns>Distinct blocking processes.</returns>
    /// <exception cref="InvalidOperationException">Restart Manager could not list holders.</exception>
    public IReadOnlyList<BlockingProcess> GetBlockingProcesses()
    {
        ThrowIfDisposed();
        uint count = 0;
        WIN32_ERROR error = PInvoke.RmGetList(_handle, out uint needed, ref count, [], out _);
        if (error == WIN32_ERROR.NO_ERROR || needed == 0)
        {
            return [];
        }

        if (error != WIN32_ERROR.ERROR_MORE_DATA)
        {
            throw new InvalidOperationException($"RmGetList failed: {error}");
        }

        RM_PROCESS_INFO[] buffer = new RM_PROCESS_INFO[needed];
        count = needed;
        error = PInvoke.RmGetList(_handle, out needed, ref count, buffer, out _);
        if (error != WIN32_ERROR.NO_ERROR)
        {
            throw new InvalidOperationException($"RmGetList failed: {error}");
        }

        List<BlockingProcess> processes = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < count; index++)
        {
            RM_PROCESS_INFO info = buffer[index];
            uint pid = info.Process.dwProcessId;
            string name = ResolveName(pid, info.strAppName.ToString());
            if (seen.Add(name))
            {
                processes.Add(new BlockingProcess(pid, name));
            }
        }

        return processes;
    }

    /// <summary>
    /// Closes the holders, gracefully first and by force after the system timeout, which is
    /// what an app that minimizes to the tray on close needs.
    /// </summary>
    /// <returns>
    /// Whether they closed. Restart Manager refuses outright when a holder is a service or a
    /// critical process it cannot bring back, and the caller closes those itself.
    /// </returns>
    public ShutdownResult Shutdown()
    {
        ThrowIfDisposed();
        WIN32_ERROR error = PInvoke.RmShutdown(_handle, (uint)RM_SHUTDOWN_TYPE.RmForceShutdown, null);
        if (error != WIN32_ERROR.NO_ERROR)
        {
            return ShutdownResult.Refused(Explain(error));
        }

        ClosedHolders = true;
        return ShutdownResult.Success;
    }

    /// <summary>
    /// Plain words for a refusal. A service holding the files is the ordinary case and the
    /// caller closes those itself, so it must not read as a fault.
    /// </summary>
    /// <param name="error">What <c>RmShutdown</c> returned.</param>
    /// <returns>Text for a status line.</returns>
    private static string Explain(WIN32_ERROR error) =>
        error switch
        {
            WIN32_ERROR.ERROR_FAIL_NOACTION_REBOOT => "Restart Manager will not close a service",
            _ => $"Restart Manager could not close them ({error})",
        };

    /// <inheritdoc/>
    public void Restart()
    {
        ThrowIfDisposed();
        if (!ClosedHolders)
        {
            return;
        }

        WIN32_ERROR error = PInvoke.RmRestart(_handle, 0, null);
        if (error != WIN32_ERROR.NO_ERROR)
        {
            throw new InvalidOperationException($"RmRestart failed: {error}");
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = PInvoke.RmEndSession(_handle);
        _handle = 0;
    }

    private static string ResolveName(uint pid, string appName)
    {
        try
        {
            using Process process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return appName;
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
