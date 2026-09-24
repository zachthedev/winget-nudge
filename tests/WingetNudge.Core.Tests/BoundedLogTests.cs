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
        string forged = $"{Now.AddDays(-400):o} a message line shaped like an old entry";

        BoundedLog.Append(_data.Paths, path, _clock, $"first\n{forged}\nsecond", retentionDays: 30).Should().BeTrue();

        string[] appended = [$"{Now:o} first", $"  {forged}", "  second"];
        File.ReadAllLines(path)
            .Should()
            .Equal([$"{Now.AddDays(-29):o} inside the window", "   at its stack frame", .. appended]);

        // The cap holds the two newest entries exactly, so the oldest goes although it is inside the window.
        // The forged line stays inside its entry rather than aging out as an entry of its own.
        _clock.Advance(TimeSpan.FromMinutes(1));
        string[] newest = [.. appended, $"{Now.AddMinutes(1):o} third"];
        int cap = Encoding.UTF8.GetByteCount(string.Concat(newest.Select(static line => line + Environment.NewLine)));

        BoundedLog.Append(_data.Paths, path, _clock, "third", retentionDays: 30, maxBytes: cap).Should().BeTrue();

        File.ReadAllLines(path).Should().Equal(newest);
    }

    [Fact]
    public void Append_OverALogFarPastTheCap_ReadsOnlyItsTail()
    {
        // A crash.log written with no cap: 16 MiB of recent entries, each a message and a stack trace.
        Directory.CreateDirectory(_data.Paths.Directory);
        string path = Path.Combine(_data.Paths.Directory, "crash.log");
        using (StreamWriter writer = new(path))
        {
            for (int index = 0; writer.BaseStream.Length < 16 * 1024 * 1024; index++)
            {
                writer.WriteLine($"{Now.AddDays(-1).AddSeconds(index):o} Unhandled");
                writer.WriteLine("System.InvalidOperationException: boom");
                for (int frame = 0; frame < 30; frame++)
                {
                    writer.WriteLine(@"   at Some.Namespace.Type.Method(Int32 value) in C:\src\File.cs:line 123");
                }

                writer.WriteLine();
            }
        }

        long legacy = new FileInfo(path).Length;
        long before = GC.GetAllocatedBytesForCurrentThread();

        BoundedLog.Append(_data.Paths, path, _clock, "new entry", retentionDays: 30).Should().BeTrue();

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        new FileInfo(path).Length.Should().BeLessThanOrEqualTo(BoundedLog.MaxBytes);
        File.ReadLines(path).Last().Should().Be($"{Now:o} new entry");
        allocated.Should().BeLessThan(legacy, "reading the whole log as text allocates at least twice its size");
    }
}
