using System.Text.Json;
using AwesomeAssertions;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

// A scanner or a second process of the app holds a freshly written file for a few milliseconds. Any
// open handle on the target refuses a replace, whatever its share mode, and a handle without delete
// sharing on the temporary refuses it too. The replace retries on the writers' table of waits. A case that
// releases its handle does so at the replace's first wait between attempts, so the release comes at a count
// rather than a time, and no stall on a loaded machine moves it.
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

        // The first replace meets the held target, and the holder lets go at the wait that follows. A write whose
        // replace does not wait meets the holder at its only attempt.
        using AttemptWaits waits = new(1, holder.Dispose);
        Action write = () => JsonFile.Write(path, New);

        try
        {
            write.Should().NotThrow("the write waits out a handle that closes");
        }
        finally
        {
            // The data directory is deleted after the case, and a held file would refuse that.
            holder.Dispose();
        }

        JsonFile.Read<Dictionary<string, string>>(path, deleteIfCorrupt: false).Should().Equal(New);
        Directory.GetFiles(_data.Paths.Directory, $"*{JsonFile.TemporarySuffix}").Should().BeEmpty();
    }

    [Fact(Timeout = 10_000)]
    public async Task Write_WhileTheTargetStaysHeld_ThrowsAndKeepsTheOldContent()
    {
        string path = _data.Paths.Preferences;
        JsonFile.Write(path, Old);

        // The waits are recorded on the pool thread the write runs on, since the watch flows into Task.Run.
        using AttemptWaits waits = new();
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Func<Task> write = () => Task.Run(() => JsonFile.Write(path, New), TestContext.Current.CancellationToken);
            await write.Should().ThrowAsync<UnauthorizedAccessException>();
        }

        waits
            .Delays.Should()
            .Equal([1, 2, 4, 8, 16, 32, 64, 128, 256], "a replace waits the whole writers' table before it gives up");

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
            // The case counts the waits between attempts rather than timing them, so no stall moves it.
            using AttemptWaits waits = new();
            Action write = () => JsonFile.Write(path, New);

            write.Should().Throw<UnauthorizedAccessException>();
            waits.Delays.Should().BeEmpty("a target that refuses every replace fails at once");
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

        // The first attempt meets the held source, and the holder lets go at the wait that follows. A replace that
        // does not wait meets the holder at its only attempt.
        using AttemptWaits waits = new(1, holder.Dispose);
        Action replace = () => JsonFile.Replace(temporary, path);

        try
        {
            replace.Should().NotThrow("the replace waits out a handle that closes");
        }
        finally
        {
            // The data directory is deleted after the case, and a held file would refuse that.
            holder.Dispose();
        }

        JsonFile.Read<Dictionary<string, string>>(path, deleteIfCorrupt: false).Should().Equal(New);
        File.Exists(temporary).Should().BeFalse("the temporary became the target");
    }

    [Fact(Timeout = 10_000)]
    public async Task Replace_WhileTheSourceStaysHeld_ThrowsASharingViolationAndKeepsTheOldContent()
    {
        (string temporary, string path) = PrepareReplace();

        // The waits are recorded on the pool thread the replace runs on, since the watch flows into Task.Run.
        using AttemptWaits waits = new();
        using (new FileStream(temporary, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Func<Task> replace = () =>
                Task.Run(() => JsonFile.Replace(temporary, path), TestContext.Current.CancellationToken);
            (await replace.Should().ThrowAsync<IOException>()).Which.HResult.Should().Be(SharingViolation);
        }

        waits
            .Delays.Should()
            .Equal([1, 2, 4, 8, 16, 32, 64, 128, 256], "a replace waits the whole writers' table before it gives up");

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
