using System.Diagnostics;
using System.Text.Json;
using AwesomeAssertions;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

// A scanner or a second process of the app holds a freshly written file for a few milliseconds. Any
// open handle on the target refuses a replace, whatever its share mode, and a handle without delete
// sharing on the temporary refuses it too. The replace retries for about 0.6 s, since each of its waits
// rounds up to the ~15 ms timer tick. A case that releases its handle does so from a dedicated thread
// 50 ms after the write or replace starts, so the release waits on no thread pool. A stall of that
// thread past about 0.5 s on a loaded machine still fails it.
public sealed class JsonFileContentionTests : IDisposable
{
    // ERROR_SHARING_VIOLATION, as .NET carries it on an IOException.
    private const int SharingViolation = unchecked((int)0x80070020);

    private static readonly Dictionary<string, string> Old = new(StringComparer.Ordinal) { ["value"] = "old" };
    private static readonly Dictionary<string, string> New = new(StringComparer.Ordinal) { ["value"] = "new" };

    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Theory]
    [InlineData(FileShare.ReadWrite)]
    [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public void Write_WhileAnotherHandleHoldsTheTarget_LandsOnceTheHandleCloses(FileShare share)
    {
        string path = _data.Paths.Preferences;
        JsonFile.Write(path, Old);
        FileStream holder = new(path, FileMode.Open, FileAccess.Read, share);

        // The write runs on this thread as soon as the releaser starts, so its first replace meets the
        // held target.
        Thread releaser = new(() =>
        {
            Thread.Sleep(50);
            holder.Dispose();
        });
        releaser.Start();
        Action write = () => JsonFile.Write(path, New);

        try
        {
            write.Should().NotThrow("the write waits out a handle that closes");
        }
        finally
        {
            // The data directory is deleted after the case, and a held file would refuse that.
            releaser.Join();
        }

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

        try
        {
            replace.Should().NotThrow("the replace waits out a handle that closes");
        }
        finally
        {
            // The data directory is deleted after the case, and a held file would refuse that.
            releaser.Join();
        }

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
}
