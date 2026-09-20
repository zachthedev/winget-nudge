using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Changelog;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

public sealed class ChangelogCacheTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly PackageInfo Git = Fixture.Updatable("Git.Git", "2.47.0", "2.48.1");

    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);

    public void Dispose() => _data.Dispose();

    private ChangelogCache Build(int maxEntries = ChangelogCache.DefaultMaxEntries) =>
        new(_data.Paths, _clock, maxEntries);

    private static void Put(ChangelogCache cache, PackageInfo package, string notes) => cache.Store([(package, notes)]);

    // ///// Keys /////

    [Fact]
    public void Get_WithNothingStored_ReturnsNull()
    {
        Build().Get(Git).Should().BeNull();
    }

    [Fact]
    public void Store_ThenGet_ReturnsTheNotesForThatRange()
    {
        ChangelogCache cache = Build();

        Put(cache, Git, "what changed");

        cache.Get(Git).Should().Be("what changed");
    }

    [Theory]
    [InlineData("2.47.0", "2.49.0")]
    [InlineData("2.46.0", "2.48.1")]
    public void Get_ForADifferentRange_Misses(string installed, string available)
    {
        ChangelogCache cache = Build();
        Put(cache, Git, "what changed");

        cache
            .Get(Fixture.Updatable("Git.Git", installed, available))
            .Should()
            .BeNull("notes cover the installed-to-available range, not the package");
    }

    [Fact]
    public void Store_WithNoAvailableVersion_KeepsNothing()
    {
        PackageInfo unversioned = new("Git.Git", "Git", "2.47.0", null, IsUpdateAvailable: true);

        Put(Build(), unversioned, "notes");

        Build().Get(unversioned).Should().BeNull();
        File.Exists(_data.Paths.ChangelogCache).Should().BeFalse("an entry with no key is not worth a file");
    }

    // ///// Persistence /////

    [Fact]
    public void Store_SurvivesANewInstanceOverTheSameDirectory()
    {
        Put(Build(), Git, "notes");

        Build().Get(Git).Should().Be("notes", "a second run must not refetch");
    }

    [Fact]
    public void Store_PastTheCap_DropsTheLeastRecentlyUsed()
    {
        PackageInfo first = Fixture.Updatable("A.A", "0", "1");
        PackageInfo second = Fixture.Updatable("B.B", "0", "1");
        PackageInfo third = Fixture.Updatable("C.C", "0", "1");
        ChangelogCache cache = Build(maxEntries: 2);
        Put(cache, first, "first");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Put(cache, second, "second");

        // Touching the first makes the second the oldest, so it is what a third entry evicts.
        _clock.Advance(TimeSpan.FromMinutes(1));
        cache.Get(first).Should().Be("first");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Put(cache, third, "third");

        cache.Get(third).Should().Be("third");
        cache.Get(first).Should().Be("first");
        cache.Get(second).Should().BeNull();
    }

    [Fact]
    public void Get_OverACorruptFile_StartsClean()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(_data.Paths.ChangelogCache, "{ not json");

        Build().Get(Git).Should().BeNull();
    }

    [Fact]
    public void Get_OverValidJsonInTheWrongShape_IgnoresTheBadEntries()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(
            _data.Paths.ChangelogCache,
            """{ "Git.Git@2.47.0>2.48.1": null, "Bun.Bun@1.3>1.4": { "notes": null } }"""
        );

        Build().Get(Git).Should().BeNull("a null entry is no entry, not a crash");
    }

    // ///// Through the fetcher /////

    [Fact]
    public async Task FetchAsync_ServesACachedPackageWithoutAnyRequest()
    {
        ChangelogCache cache = Build();
        Put(cache, Git, "cached notes");
        FakeHttpHandler handler = new();
        using HttpClient http = handler.CreateClient();

        IReadOnlyDictionary<string, string> notes = await new ChangelogFetcher(http, cache).FetchAsync(
            [Git],
            TestContext.Current.CancellationToken
        );

        notes.Should().ContainKey("Git.Git").WhoseValue.Should().Be("cached notes");
        handler.Requests.Should().BeEmpty("a cached range never goes back to the network");
    }

    [Fact]
    public async Task FetchAsync_CachesALookupThatFullySucceeded()
    {
        FakeHttpHandler handler = new FakeHttpHandler()
            .Map("/Git/Git/2.48.1/Git.Git.locale.en-US.yaml", "ReleaseNotesUrl: https://github.com/git/git/releases\n")
            .Map("/repos/git/git/releases", """[ { "tag_name": "v2.48.1", "body": "real notes" } ]""");
        using HttpClient http = handler.CreateClient();

        await new ChangelogFetcher(http, Build()).FetchAsync([Git], TestContext.Current.CancellationToken);

        Build().Get(Git).Should().Contain("real notes");
    }

    [Fact]
    public async Task FetchAsync_AfterAFailedReleasesLookup_ShowsTheFallbackButCachesNothing()
    {
        // The releases endpoint is unmapped, so it answers 404, standing in for a rate limit.
        FakeHttpHandler failing = new FakeHttpHandler().Map(
            "/Git/Git/2.48.1/Git.Git.locale.en-US.yaml",
            "ReleaseNotesUrl: https://github.com/git/git/releases\n"
        );
        using HttpClient failingHttp = failing.CreateClient();

        IReadOnlyDictionary<string, string> first = await new ChangelogFetcher(failingHttp, Build()).FetchAsync(
            [Git],
            TestContext.Current.CancellationToken
        );

        first["Git.Git"].Should().Be("changelog: https://github.com/git/git/releases");
        Build().Get(Git).Should().BeNull("a partial lookup must not stand in for the real notes");

        FakeHttpHandler healthy = new FakeHttpHandler()
            .Map("/Git/Git/2.48.1/Git.Git.locale.en-US.yaml", "ReleaseNotesUrl: https://github.com/git/git/releases\n")
            .Map("/repos/git/git/releases", """[ { "tag_name": "v2.48.1", "body": "real notes" } ]""");
        using HttpClient healthyHttp = healthy.CreateClient();

        IReadOnlyDictionary<string, string> second = await new ChangelogFetcher(healthyHttp, Build()).FetchAsync(
            [Git],
            TestContext.Current.CancellationToken
        );

        second["Git.Git"].Should().Contain("real notes");
    }
}
