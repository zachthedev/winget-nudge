using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

// A scanner or a second process of the app holds a freshly written file for a few milliseconds. Any
// open handle on the target refuses a replace, whatever its share mode, and a handle without delete
// sharing on the temporary refuses it too. The replace retries for about 0.6 s, since each of its waits
// rounds up to the ~15 ms timer tick. A case that releases its handle does so 50 ms after the replace
// is under way, so a stall of more than about 0.5 s on a loaded machine fails it.
public sealed class JsonFileContentionTests : IDisposable
{
    // ERROR_SHARING_VIOLATION, as .NET carries it on an IOException.
    private const int SharingViolation = unchecked((int)0x80070020);

    private static readonly Dictionary<string, string> Old = new(StringComparer.Ordinal) { ["value"] = "old" };
    private static readonly Dictionary<string, string> New = new(StringComparer.Ordinal) { ["value"] = "new" };

    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Theory(Timeout = 10_000)]
    [InlineData(FileShare.ReadWrite)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public async Task Write_WhileAnotherHandleHoldsTheTarget_LandsOnceTheHandleCloses(FileShare share)
    {
        string path = _data.Paths.Preferences;
        JsonFile.Write(path, Old);

        Task write;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, share))
        {
            write = Task.Run(() => JsonFile.Write(path, New), TestContext.Current.CancellationToken);

            // The temporary exists from its creation until the replace lands or fails, so seeing it
            // shows the write reached the replace while this handle holds the target.
            await WaitForTemporaryAsync(write);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            write.IsCompleted.Should().BeFalse("the write waits out a handle that closes");
        }

        await write.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        JsonFile.Read<Dictionary<string, string>>(path, deleteIfCorrupt: false).Should().Equal(New);
        Directory.GetFiles(_data.Paths.Directory, $"*{JsonFile.TemporarySuffix}").Should().BeEmpty();
    }

    [Fact(Timeout = 10_000)]
    public async Task Write_WhileTheTargetStaysHeld_ThrowsAndKeepsTheOldContent()
    {
        string path = _data.Paths.Preferences;
        JsonFile.Write(path, Old);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Func<Task> write = () => Task.Run(() => JsonFile.Write(path, New), TestContext.Current.CancellationToken);
            await write.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        JsonFile.Read<Dictionary<string, string>>(path, deleteIfCorrupt: false).Should().Equal(Old);
        Directory.GetFiles(_data.Paths.Directory, $"*{JsonFile.TemporarySuffix}").Should().BeEmpty();
    }

    [Theory]
    [InlineData("read-only file")]
    [InlineData("directory")]
    public void Write_OverATargetThatRefusesEveryReplace_FailsWithoutWaiting(string target)
    {
        string path = _data.Paths.Preferences;
        if (target == "directory")
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            JsonFile.Write(path, Old);
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }

        try
        {
            // Waiting out the retries takes about 0.6 s and failing at once about 5 ms, so 300 ms
            // separates the two with room for a cold first call.
            Stopwatch elapsed = Stopwatch.StartNew();
            Action write = () => JsonFile.Write(path, New);

            write.Should().Throw<UnauthorizedAccessException>();
            elapsed
                .Elapsed.Should()
                .BeLessThan(TimeSpan.FromMilliseconds(300), "a target that refuses every replace fails at once");
            if (target == "directory")
            {
                Directory.Exists(path).Should().BeTrue();
            }
            else
            {
                JsonFile.Read<Dictionary<string, string>>(path, deleteIfCorrupt: false).Should().Equal(Old);
            }

            Directory.GetFiles(_data.Paths.Directory, $"*{JsonFile.TemporarySuffix}").Should().BeEmpty();
        }
        finally
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
            }
        }
    }

    [Fact]
    public void Replace_WhileAHandleWithoutDeleteSharingHoldsTheSource_LandsOnceTheHandleCloses()
    {
        (string temporary, string path) = PrepareReplace();
        FileStream holder = new(temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // The replace runs on this thread as soon as the releaser starts, so its first attempt meets the
        // held source.
        Thread releaser = new(() =>
        {
            Thread.Sleep(50);
            holder.Dispose();
        });
        releaser.Start();
        Action replace = () => JsonFile.Replace(temporary, path);

        replace.Should().NotThrow("the replace waits out a handle that closes");
        releaser.Join();
        JsonFile.Read<Dictionary<string, string>>(path, deleteIfCorrupt: false).Should().Equal(New);
        File.Exists(temporary).Should().BeFalse("the temporary became the target");
    }

    [Fact(Timeout = 10_000)]
    public async Task Replace_WhileTheSourceStaysHeld_ThrowsASharingViolationAndKeepsTheOldContent()
    {
        (string temporary, string path) = PrepareReplace();

        using (new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Func<Task> replace = () =>
                Task.Run(() => JsonFile.Replace(temporary, path), TestContext.Current.CancellationToken);
            (await replace.Should().ThrowAsync<IOException>()).Which.HResult.Should().Be(SharingViolation);
        }

        JsonFile.Read<Dictionary<string, string>>(path, deleteIfCorrupt: false).Should().Equal(Old);
    }

    private (string Temporary, string Path) PrepareReplace()
    {
        string path = _data.Paths.Preferences;
        JsonFile.Write(path, Old);
        string temporary = $"{path}.test{JsonFile.TemporarySuffix}";
        File.WriteAllText(temporary, JsonSerializer.Serialize(New, JsonFile.Options));
        return (temporary, path);
    }

    // Returns once the write's temporary exists, or once the write has already finished.
    private async Task WaitForTemporaryAsync(Task write)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (
            !write.IsCompleted
            && Directory.GetFiles(_data.Paths.Directory, $"preferences.json.*{JsonFile.TemporarySuffix}").Length == 0
        )
        {
            waited.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "the write creates its temporary first");
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
    }
}
