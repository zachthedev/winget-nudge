using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

public sealed class DiagnosticsLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Append_AddsATimestampedLineAndDropsLinesPastTheWindow()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllLines(
            _data.Paths.DiagnosticsLog,
            ["no timestamp", $"{Now.AddDays(-31):o} past the window", $"{Now.AddDays(-29):o} inside the window"]
        );

        DiagnosticsLog.Append(_data.Paths, _clock, "first part\nsecond part", retentionDays: 30).Should().BeTrue();

        File.ReadAllLines(_data.Paths.DiagnosticsLog)
            .Should()
            .Equal($"{Now.AddDays(-29):o} inside the window", $"{Now:o} first part second part");
    }

    [Fact]
    public void Append_WhenTheLogIsALink_RefusesWithoutReadingWhatItNames()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        string outside = Path.Combine(_data.Root, "outside.log");
        string[] secret = [$"{Now.AddDays(-1):o} a line from outside the data directory"];
        File.WriteAllLines(outside, secret);
        File.CreateSymbolicLink(_data.Paths.DiagnosticsLog, outside);

        DiagnosticsLog.Append(_data.Paths, _clock, "probe", retentionDays: 30).Should().BeFalse();

        new FileInfo(_data.Paths.DiagnosticsLog)
            .LinkTarget.Should()
            .Be(outside, "a refused append leaves the link as it found it, holding no copy of what it names");
        File.ReadAllLines(outside).Should().Equal(secret);
    }
}
