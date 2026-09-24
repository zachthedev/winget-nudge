using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Preferences;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

public sealed class PreferenceStoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly PreferenceStore _store;

    public PreferenceStoreTests()
    {
        _store = new PreferenceStore(_data.Paths, _clock);
    }

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Load_WithoutAFile_IsEmpty()
    {
        _store.Load().All.Should().BeEmpty();
    }

    [Fact]
    public void Set_ThenLoad_RoundTripsEveryState()
    {
        _store.Set("Muted.App", PreferenceState.Muted);
        _store.Set("Failed.App", PreferenceState.Failed, "exit code 6");
        _store.SkipVersion("Skipped.App", "2.0");

        PreferenceSnapshot snapshot = _store.Load();

        snapshot.For("Muted.App").Should().Be(new PreferenceEntry(PreferenceState.Muted, Now));
        snapshot.For("Failed.App").Should().Be(new PreferenceEntry(PreferenceState.Failed, Now, "exit code 6"));
        snapshot.For("Skipped.App").Should().Be(new PreferenceEntry(PreferenceState.Skipped, Now, null, "2.0"));
    }

    [Theory]
    [InlineData(6.9, true)]
    [InlineData(7, false)]
    [InlineData(30, false)]
    public void Load_PrunesFailedEntriesOlderThanSevenDays(double ageDays, bool kept)
    {
        _store.Set("Failed.App", PreferenceState.Failed, "boom");
        _store.Set("Muted.App", PreferenceState.Muted);
        _clock.Advance(TimeSpan.FromDays(ageDays));

        PreferenceSnapshot snapshot = _store.Load();

        snapshot.All.ContainsKey("Failed.App").Should().Be(kept);
        snapshot.All.Should().ContainKey("Muted.App", "muted entries never expire");
        _store.Load().All.ContainsKey("Failed.App").Should().Be(kept, "the pruned file is what the next load reads");
    }

    [Fact]
    public void Clear_RemovesOnlyThatPackage()
    {
        _store.Set("A", PreferenceState.Muted);
        _store.Set("B", PreferenceState.Muted);

        _store.Clear("A");
        _store.Clear("Missing");

        _store.Load().All.Keys.Should().Equal("B");
    }

    [Theory]
    [InlineData("Muted.App", "1.0", true)]
    [InlineData("Failed.App", "1.0", true)]
    [InlineData("Skipped.App", "2.0", true)]
    [InlineData("Skipped.App", "2.1", false)]
    [InlineData("Unknown.App", "1.0", false)]
    public void IsSuppressed_HoldsBackMutedFailedAndExactSkippedVersions(string id, string available, bool suppressed)
    {
        _store.Set("Muted.App", PreferenceState.Muted);
        _store.Set("Failed.App", PreferenceState.Failed, "boom");
        _store.SkipVersion("Skipped.App", "2.0");

        _store.Load().IsSuppressed(id, available).Should().Be(suppressed);
    }

    [Fact]
    public void Load_SetsACorruptFileAsideAndStartsEmpty()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(_data.Paths.Preferences, "{{{");

        _store.Load().All.Should().BeEmpty();
        File.Exists(_data.Paths.Preferences).Should().BeFalse();
        Directory
            .GetFiles(_data.Paths.Directory, "preferences.json.*.corrupt")
            .Should()
            .ContainSingle("user preferences are kept for repair");
    }

    [Fact(Timeout = 30_000)]
    public async Task Set_OverACorruptFileHeldPastItsSetAside_FailsAndKeepsTheFile()
    {
        // A scanner holds the corrupt file until the update gives up moving it aside, then lets go. Any
        // write from that moment would replace the only copy, so the handle goes the instant a temporary
        // file appears. The update must fail before it writes anything.
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(_data.Paths.Preferences, "{ corrupt original");
        using FileStream holder = new(_data.Paths.Preferences, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Task set = Task.Run(
            () => new PreferenceStore(_data.Paths, _clock).Set("App.Id", PreferenceState.Muted),
            TestContext.Current.CancellationToken
        );
        while (
            !set.IsCompleted && Directory.GetFiles(_data.Paths.Directory, $"*{JsonFile.TemporarySuffix}").Length == 0
        )
        {
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }

        holder.Dispose();

        Func<Task> finishing = () => set;
        IOException refusal = (await finishing.Should().ThrowAsync<IOException>()).Which;
        refusal
            .Message.Should()
            .StartWith(
                "preferences.json is corrupt and could not be moved aside for repair, so nothing was saved: ",
                "the message states the refusal without guessing its cause"
            );
        refusal.InnerException.Should().NotBeNull("the move's own error is the reason");
        refusal.Message.Should().EndWith(refusal.InnerException?.Message ?? "", "the message names that reason");
        File.ReadAllText(_data.Paths.Preferences).Should().Be("{ corrupt original", "the original stays for repair");
        Directory
            .GetFiles(_data.Paths.Directory, $"*{JsonFile.TemporarySuffix}")
            .Should()
            .BeEmpty("the update refused before it began a write");
    }

    [Fact]
    public async Task Load_RacingASetOverACorruptFile_KeepsTheSetAndTheRepairCopy()
    {
        // A scan's load and a picker toggle meet over a corrupt file. The two interleave differently
        // from round to round, and every order must keep the change and the original bytes.
        Directory.CreateDirectory(_data.Paths.Directory);
        for (int round = 0; round < 100; round++)
        {
            File.WriteAllText(_data.Paths.Preferences, "{ corrupt");
            using Barrier start = new(2);
            string id = $"App{round}";
            Task reader = Task.Run(
                () =>
                {
                    start.SignalAndWait(TestContext.Current.CancellationToken);
                    new PreferenceStore(_data.Paths, _clock).Load();
                },
                TestContext.Current.CancellationToken
            );
            Task writer = Task.Run(
                () =>
                {
                    start.SignalAndWait(TestContext.Current.CancellationToken);
                    new PreferenceStore(_data.Paths, _clock).Set(id, PreferenceState.Muted);
                },
                TestContext.Current.CancellationToken
            );

            Func<Task> race = () => Task.WhenAll(reader, writer);
            await race.Should().NotThrowAsync($"round {round}: neither side may trip over the other's set-aside");
            _store.Load().For(id).Should().NotBeNull($"round {round}: a Set that returned is on disk");
            Directory
                .GetFiles(_data.Paths.Directory, "preferences.json.*.corrupt")
                .Should()
                .HaveCount(
                    Math.Min(round + 1, JsonFile.CorruptCopiesKept),
                    $"round {round}: each round sets one copy aside, none replaces another, and the oldest go"
                );
        }

        Directory
            .GetFiles(_data.Paths.Directory, "preferences.json.*.corrupt")
            .Select(File.ReadAllText)
            .Should()
            .AllBe("{ corrupt", "every copy holds the original bytes, never a file a writer replaced");
    }

    [Fact]
    public void Load_ReadsTheLegacyLowercaseStateNames()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(
            _data.Paths.Preferences,
            """{ "Git.Git": { "state": "muted", "since": "2026-09-01T00:00:00+00:00" } }"""
        );

        _store.Load().For("Git.Git")?.State.Should().Be(PreferenceState.Muted);
    }

    [Fact(Timeout = 60_000)]
    public async Task Set_FromTwoStoresAtOnce_KeepsEveryChange()
    {
        // Two stores on one data directory stand in for two processes of the app.
        PreferenceStore other = new(_data.Paths, _clock);

        await Task.WhenAll(
            Task.Run(() => SetMany(_store, "A"), TestContext.Current.CancellationToken),
            Task.Run(() => SetMany(other, "B"), TestContext.Current.CancellationToken)
        );

        _store.Load().All.Should().HaveCount(100, "neither store may overwrite the other's entries");
    }

    private static void SetMany(PreferenceStore store, string prefix)
    {
        for (int index = 0; index < 50; index++)
        {
            store.Set($"{prefix}{index}", PreferenceState.Muted);
        }
    }
}
