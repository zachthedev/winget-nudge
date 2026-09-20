using AwesomeAssertions;
using WingetNudge.Core.Upgrade;

namespace WingetNudge.Core.Tests;

public sealed class WingetDiagnosticsTests : IDisposable
{
    private static readonly DateTimeOffset AttemptStart = new(2026, 9, 10, 10, 47, 0, TimeSpan.Zero);

    // Winget records only that the installer exited non-zero.
    private const string WingetOwnLog = """
        2026-09-10 05:47:13.577 <I> [CLI ] Starting: 'nomachine.exe' with arguments '/VERYSILENT'
        2026-09-10 05:47:15.285 <E> [CLI ] ShellExecute installer failed: 1
        2026-09-10 05:47:15.285 <E> [CLI ] Terminating context: 0x8a150006, aborting the install flow
        """;

    // The installer's own log holds the sentence a user can act on.
    private const string NoMachineLog = """
        2026-09-10 05:47:14.119   NX> 700 Looking for installed NoMachine products.
        2026-09-10 05:47:14.120   NX> 700 NoMachine is already installed. Preparing to update.
        2026-09-10 05:47:15.248   NX> 700 Two different NoMachine packages can't be installed at the same time on the same host.
        2026-09-10 05:47:15.248   Failed to proceed to next wizard page; aborting.
        """;

    private const string BuildToolsLog = """
        [0d20:0022] Download of 'VisualStudio.vsman' succeeded using engine 'WebClient'
        [0d20:0014] Status changed to NoUpdate
        [0d20:0009] Warning: An installed product matching the following parameters cannot be found: productId: Microsoft.VisualStudio.Product.BuildTools
        [0d20:0001] Closing the installer with exit code 1
        """;

    private readonly string _directory = Directory.CreateTempSubdirectory("winget-diag").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string content, TimeSpan offsetFromStart)
    {
        string path = Path.Combine(_directory, name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, (AttemptStart + offsetFromStart).UtcDateTime);
        return path;
    }

    [Fact]
    public void Collect_LiftsTheInstallersOwnRefusalOverWingetsGenericFailure()
    {
        Write("WinGetCOM-2026-09-10-05-47-13.192.log", WingetOwnLog, TimeSpan.FromSeconds(15));
        string installer = Write(
            "NoMachine.NoMachine.10.0.60-26-09-10-05-47-13.log",
            NoMachineLog,
            TimeSpan.FromSeconds(16)
        );

        WingetDiagnostics found = new WingetDiagnosticsReader(_directory).Collect(AttemptStart);

        found
            .Summary.Should()
            .Be(
                "2026-09-10 05:47:15.248   NX> 700 Two different NoMachine packages can't be installed at the same time on the same host."
            );
        found.Files.Should().Contain(installer);
    }

    [Fact]
    public void Collect_ReturnsNothingWhenOnlyWingetsOwnLogMatches()
    {
        Write("WinGetCOM-2026-09-10-05-47-13.192.log", WingetOwnLog, TimeSpan.FromSeconds(15));

        WingetDiagnostics found = new WingetDiagnosticsReader(_directory).Collect(AttemptStart);

        found.Summary.Should().BeNull("winget's own wording is already on the card");
        found.Files.Should().HaveCount(1, "the file still goes into the dump");
    }

    [Fact]
    public void Collect_ReadsTheVisualStudioInstallersRefusal()
    {
        Write("dd_installer_20260910054723.log", BuildToolsLog, TimeSpan.FromSeconds(31));

        WingetDiagnostics found = new WingetDiagnosticsReader(_directory).Collect(AttemptStart);

        found.Summary.Should().Contain("cannot be found");
        found.Summary.Should().Contain("Microsoft.VisualStudio.Product.BuildTools");
    }

    [Fact]
    public void Collect_IgnoresLogsWrittenBeforeTheAttempt()
    {
        Write("NoMachine.NoMachine.9.9.9-old.log", NoMachineLog, TimeSpan.FromMinutes(-5));

        WingetDiagnostics found = new WingetDiagnosticsReader(_directory).Collect(AttemptStart);

        found.Should().Be(WingetDiagnostics.None);
    }

    [Fact]
    public void Collect_KeepsTheFilesWhenNoLineLooksLikeARefusal()
    {
        string quiet = Write("Some.Package-26-09-10.log", "step one done\nstep two done", TimeSpan.FromSeconds(5));

        WingetDiagnostics found = new WingetDiagnosticsReader(_directory).Collect(AttemptStart);

        found.Summary.Should().BeNull("nothing in the log names a refusal");
        found.Files.Should().Equal(quiet);
    }

    [Fact]
    public void Collect_ReturnsNoneWhenTheDirectoryIsMissing()
    {
        string missing = Path.Combine(_directory, "not-there");

        new WingetDiagnosticsReader(missing).Collect(AttemptStart).Should().Be(WingetDiagnostics.None);
    }

    [Fact]
    public void Summary_IsTruncatedSoItFitsAStatusLine()
    {
        Write(
            "Long.Package-26-09-10.log",
            $"the operation was aborted because {new string('x', 500)}",
            TimeSpan.FromSeconds(5)
        );

        WingetDiagnostics found = new WingetDiagnosticsReader(_directory).Collect(AttemptStart);

        found.Summary.Should().NotBeNull();
        found.Summary.Should().HaveLength(WingetDiagnosticsReader.SummaryLength + 3);
        found.Summary.Should().EndWith("...");
    }

    [Fact]
    public void Collect_RefusesToReadThroughARedirectedDirectory()
    {
        string real = Directory.CreateTempSubdirectory("winget-diag-real").FullName;
        string secret = Path.Combine(real, "Planted.Package-26-09-10.log");
        File.WriteAllText(secret, "the operation was aborted for a secret reason");
        File.SetLastWriteTimeUtc(secret, (AttemptStart + TimeSpan.FromSeconds(5)).UtcDateTime);
        string link = Path.Combine(_directory, "redirected");
        Directory.CreateSymbolicLink(link, real);

        try
        {
            WingetDiagnostics found = new WingetDiagnosticsReader(link).Collect(AttemptStart);

            found.Should().Be(WingetDiagnostics.None, "an elevated read must not follow a link");
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(real, recursive: true);
        }
    }

    [Fact]
    public void Collect_StopsAfterTheFileCap()
    {
        for (int index = 0; index < WingetDiagnosticsReader.MaxFiles + 5; index++)
        {
            Write($"Package{index}-26-09-10.log", "step done", TimeSpan.FromSeconds(index + 1));
        }

        WingetDiagnostics found = new WingetDiagnosticsReader(_directory).Collect(AttemptStart);

        found.Files.Should().HaveCount(WingetDiagnosticsReader.MaxFiles);
    }

    [Fact]
    public void Tail_ReadsOnlyTheEndOfAHugeFile()
    {
        string line = new('x', 512);
        string path = Write(
            "Huge.Package-26-09-10.log",
            string.Join('\n', Enumerable.Range(1, 4000).Select(number => $"{line} {number}")),
            TimeSpan.FromSeconds(5)
        );

        IReadOnlyList<string> tail = WingetDiagnosticsReader.Tail(path);

        tail.Should().HaveCount(WingetDiagnosticsReader.TailLines);
        tail[^1].Should().EndWith("4000");
    }

    [Fact]
    public void Tail_KeepsOnlyTheLastLines()
    {
        string path = Write(
            "Big.Package-26-09-10.log",
            string.Join('\n', Enumerable.Range(1, 200).Select(static line => $"line {line}")),
            TimeSpan.FromSeconds(5)
        );

        IReadOnlyList<string> tail = WingetDiagnosticsReader.Tail(path);

        tail.Should().HaveCount(WingetDiagnosticsReader.TailLines);
        tail[^1].Should().Be("line 200");
    }
}
