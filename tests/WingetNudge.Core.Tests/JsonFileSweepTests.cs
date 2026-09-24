using AwesomeAssertions;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

public sealed class JsonFileSweepTests : IDisposable
{
    private readonly TempData _data = new();

    public JsonFileSweepTests() => Directory.CreateDirectory(_data.Paths.Directory);

    public void Dispose() => _data.Dispose();

    [Fact]
    public void SweepTemporaries_RemovesAnAbandonedWrite()
    {
        string stale = Aged("settings.json.4242-abc.tmp", TimeSpan.FromDays(9));

        JsonFile.SweepTemporaries(_data.Paths.Directory, TimeSpan.FromHours(1)).Should().Be(1);
        File.Exists(stale).Should().BeFalse();
    }

    [Fact]
    public void SweepTemporaries_LeavesAWriteStillInFlight()
    {
        string fresh = Aged("settings.json.4242-abc.tmp", TimeSpan.Zero);

        JsonFile.SweepTemporaries(_data.Paths.Directory, TimeSpan.FromHours(1)).Should().Be(0);
        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void Read_PrunesOnlyCopiesItNamedAndNeverOnesStampedLater()
    {
        // Two names a user chose, two copies from a clock that once ran ahead, and one from an earlier
        // build, which named copies to the second.
        string[] foreign = ["preferences.json.1-old.corrupt", "preferences.json.my-backup.corrupt"];
        string[] ahead =
        [
            $"preferences.json.209901010000000000000-{Guid.NewGuid():N}.corrupt",
            $"preferences.json.209901010000000000001-{Guid.NewGuid():N}.corrupt",
        ];
        string earlier = "preferences.json.20200101000000.corrupt";
        foreach (string name in (string[])[.. foreign, .. ahead, earlier])
        {
            File.WriteAllText(Path.Combine(_data.Paths.Directory, name), name);
        }

        int made = JsonFile.CorruptCopiesKept + 2;
        for (int copy = 0; copy < made; copy++)
        {
            File.WriteAllText(_data.Paths.Preferences, $"{{ corrupt {copy}");
            JsonFile
                .Read<Dictionary<string, string>>(_data.Paths.Preferences, deleteIfCorrupt: false)
                .Should()
                .BeNull();
        }

        foreach (string name in (string[])[.. foreign, .. ahead])
        {
            File.Exists(Path.Combine(_data.Paths.Directory, name))
                .Should()
                .BeTrue($"{name} is not a copy this read may count or delete");
        }

        File.Exists(Path.Combine(_data.Paths.Directory, earlier))
            .Should()
            .BeFalse("an earlier build's copy counts, and it is the oldest");
        Directory
            .GetFiles(_data.Paths.Directory, "preferences.json.*.corrupt")
            .Where(copy => !((string[])[.. foreign, .. ahead]).Contains(Path.GetFileName(copy)))
            .Select(File.ReadAllText)
            .Should()
            .BeEquivalentTo(
                Enumerable
                    .Range(made - JsonFile.CorruptCopiesKept, JsonFile.CorruptCopiesKept)
                    .Select(n => $"{{ corrupt {n}"),
                "the newest real copies keep every slot"
            );
    }

    [Fact]
    public void Read_KeepsOnlyTheNewestCorruptCopies()
    {
        int made = JsonFile.CorruptCopiesKept + 2;
        for (int copy = 0; copy < made; copy++)
        {
            File.WriteAllText(_data.Paths.Preferences, $"{{ corrupt {copy}");
            JsonFile
                .Read<Dictionary<string, string>>(_data.Paths.Preferences, deleteIfCorrupt: false)
                .Should()
                .BeNull();
        }

        Directory
            .GetFiles(_data.Paths.Directory, "preferences.json.*.corrupt")
            .Select(File.ReadAllText)
            .Should()
            .BeEquivalentTo(
                Enumerable
                    .Range(made - JsonFile.CorruptCopiesKept, JsonFile.CorruptCopiesKept)
                    .Select(n => $"{{ corrupt {n}"),
                "the copy just made and the ones before it stay, and older ones go"
            );
    }

    [Fact]
    public void SweepTemporaries_LeavesTheRealStateFiles()
    {
        string settings = Aged("settings.json", TimeSpan.FromDays(9));
        string corrupt = Aged("preferences.json.20260101000000.corrupt", TimeSpan.FromDays(9));

        JsonFile.SweepTemporaries(_data.Paths.Directory, TimeSpan.FromHours(1)).Should().Be(0);
        File.Exists(settings).Should().BeTrue();
        File.Exists(corrupt).Should().BeTrue("a set-aside file holds user data worth repairing");
    }

    [Fact]
    public void SweepTemporaries_OverAMissingDirectory_ReportsNothing()
    {
        JsonFile.SweepTemporaries(Path.Combine(_data.Root, "gone"), TimeSpan.FromHours(1)).Should().Be(0);
    }

    [Fact]
    public void Write_LeavesNoTemporaryBehind()
    {
        JsonFile.Write(_data.Paths.Settings, new Settings());

        Directory.GetFiles(_data.Paths.Directory, $"*{JsonFile.TemporarySuffix}").Should().BeEmpty();
    }

    [Fact]
    public void SweepTemporaries_ThroughALinkedDirectory_DeletesNothing()
    {
        string real = Directory.CreateDirectory(Path.Combine(_data.Root, "elsewhere")).FullName;
        string victim = Path.Combine(real, "someone-else.tmp");
        File.WriteAllText(victim, "{}");
        File.SetLastWriteTimeUtc(victim, DateTime.UtcNow - TimeSpan.FromDays(9));
        string link = Path.Combine(_data.Root, "linked-data");
        Directory.CreateSymbolicLink(link, real);

        try
        {
            JsonFile.SweepTemporaries(link, TimeSpan.FromHours(1)).Should().Be(0);
            File.Exists(victim).Should().BeTrue("a link would aim the deletes at its target");
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Read_CorruptFileBehindALinkedDirectory_LeavesItInPlace(bool deleteIfCorrupt)
    {
        string real = Directory.CreateDirectory(Path.Combine(_data.Root, "elsewhere")).FullName;
        string target = Path.Combine(real, "cache.json");
        File.WriteAllText(target, "{ not json");
        string link = Path.Combine(_data.Root, "linked-data");
        Directory.CreateSymbolicLink(link, real);

        try
        {
            JsonFile
                .Read<Dictionary<string, string>>(Path.Combine(link, "cache.json"), deleteIfCorrupt)
                .Should()
                .BeNull();
            File.Exists(target).Should().BeTrue("neither delete nor rename may follow the link");
            Directory.GetFiles(real).Should().ContainSingle();
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void SweepTemporaries_LeavesLockFiles()
    {
        string stale = Aged("preferences.json.4242-abc.tmp", TimeSpan.FromDays(9));
        string lockFile = Aged($"preferences.json{JsonFile.LockSuffix}", TimeSpan.FromDays(9));

        JsonFile.SweepTemporaries(_data.Paths.Directory, TimeSpan.FromHours(1)).Should().Be(1);
        File.Exists(stale).Should().BeFalse();
        File.Exists(lockFile).Should().BeTrue("a lock file is reused by every write, however old");
    }

    private string Aged(string name, TimeSpan age)
    {
        string path = Path.Combine(_data.Paths.Directory, name);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }
}
