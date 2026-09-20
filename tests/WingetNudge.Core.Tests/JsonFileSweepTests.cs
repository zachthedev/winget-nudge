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

    private string Aged(string name, TimeSpan age)
    {
        string path = Path.Combine(_data.Paths.Directory, name);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
        return path;
    }
}
