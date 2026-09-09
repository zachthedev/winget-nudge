using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Preferences;
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
        snapshot
            .For("Failed.App")
            .Should()
            .Be(new PreferenceEntry(PreferenceState.Failed, Now, "exit code 6"));
        snapshot
            .For("Skipped.App")
            .Should()
            .Be(new PreferenceEntry(PreferenceState.Skipped, Now, null, "2.0"));
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
        _store
            .Load()
            .All.ContainsKey("Failed.App")
            .Should()
            .Be(kept, "the pruned file is what the next load reads");
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
    public void IsSuppressed_HoldsBackMutedFailedAndExactSkippedVersions(
        string id,
        string available,
        bool suppressed
    )
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
}
