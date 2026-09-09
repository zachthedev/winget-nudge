using System.Diagnostics;

namespace WingetNudge.Core.Tools;

/// <summary>Captured output of a finished process.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="Output">Combined stdout and stderr.</param>
public sealed record ProcessOutput(int ExitCode, string Output);

/// <summary>Runs a command and captures its output.</summary>
public interface IProcessRunner
{
    /// <summary>Runs a command to completion.</summary>
    /// <param name="executable">Program to run, resolved through PATH.</param>
    /// <param name="arguments">Arguments passed verbatim, one per element.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Exit code and combined output.</returns>
    /// <exception cref="System.ComponentModel.Win32Exception">The executable was not found.</exception>
    Task<ProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    );
}

/// <summary>Runs commands hidden with no console window, killed after <see cref="Timeout"/>.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Longest a version command may run.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Most output characters kept.</summary>
    public const int MaxOutputLength = 64 * 1024;

    /// <inheritdoc/>
    public async Task<ProcessOutput> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeout.CancelAfter(Timeout);
        CancellationToken token = timeout.Token;

        ProcessStartInfo startInfo = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process =
            Process.Start(startInfo)
            ?? throw new InvalidOperationException($"'{executable}' did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(token);
        Task<string> stderr = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception)
                when (exception is InvalidOperationException or System.ComponentModel.Win32Exception
                )
            {
                // Already gone.
            }

            throw;
        }

        string output = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
        if (output.Length > MaxOutputLength)
        {
            output = output[..MaxOutputLength];
        }

        return new ProcessOutput(process.ExitCode, output);
    }
}
