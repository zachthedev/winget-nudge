using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

public sealed class BoundedLogTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Append_AddsAnEntryThenDropsEntriesPastTheWindowAndTheOldestPastTheCap()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        string path = Path.Combine(_data.Paths.Directory, "entries.log");
        File.WriteAllLines(
            path,
            [
                "no timestamp",
                $"{Now.AddDays(-31):o} past the window",
                "10:00 is a time in its message, not a timestamp",
                $"{Now.AddDays(-29):o} inside the window",
                "   at its stack frame",
            ]
        );

        BoundedLog.Append(_data.Paths, path, _clock, "first\nsecond", retentionDays: 30).Should().BeTrue();

        File.ReadAllLines(path)
            .Should()
            .Equal($"{Now.AddDays(-29):o} inside the window", "   at its stack frame", $"{Now:o} first", "second");

        // The cap holds the two newest entries exactly, so the oldest goes although it is inside the window.
        _clock.Advance(TimeSpan.FromMinutes(1));
        string[] newest = [$"{Now:o} first", "second", $"{Now.AddMinutes(1):o} third"];
        int cap = Encoding.UTF8.GetByteCount(string.Concat(newest.Select(static line => line + Environment.NewLine)));

        BoundedLog.Append(_data.Paths, path, _clock, "third", retentionDays: 30, maxBytes: cap).Should().BeTrue();

        File.ReadAllLines(path).Should().Equal(newest);
    }
}
