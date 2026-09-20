using AwesomeAssertions;
using WingetNudge.Core.Tests.Support;
using WingetNudge.Core.Tracking;

namespace WingetNudge.Core.Tests;

public sealed class ReleaseDateResolverTests
{
    private const string Commits = """
        [
          { "sha": "b", "commit": { "committer": { "date": "2026-09-05T10:00:00Z" } } },
          { "sha": "a", "commit": { "committer": { "date": "2026-09-01T08:30:00Z" } } },
          { "sha": "c", "commit": { "committer": { } } }
        ]
        """;

    [Fact]
    public void ParseOldestCommitDate_ReturnsTheEarliestDatedCommit()
    {
        GitHubReleaseDateResolver
            .ParseOldestCommitDate(Commits)
            .Should()
            .Be(new DateTimeOffset(2026, 9, 1, 8, 30, 0, TimeSpan.Zero));
        GitHubReleaseDateResolver.ParseOldestCommitDate("[]").Should().BeNull();
        GitHubReleaseDateResolver.ParseOldestCommitDate("""{ "message": "rate limited" }""").Should().BeNull();
    }

    [Fact]
    public void ParseReleaseDate_ReadsTopLevelThenInstallerLevelDates()
    {
        GitHubReleaseDateResolver
            .ParseReleaseDate("ReleaseDate: 2026-08-30\nInstallers:\n- ReleaseDate: 2026-01-01\n")
            .Should()
            .Be(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        GitHubReleaseDateResolver
            .ParseReleaseDate("Installers:\n- Architecture: x64\n- ReleaseDate: 2026-02-02\n")
            .Should()
            .Be(new DateTimeOffset(2026, 2, 2, 0, 0, 0, TimeSpan.Zero));
        GitHubReleaseDateResolver.ParseReleaseDate("Installers:\n- Architecture: x64\n").Should().BeNull();
        GitHubReleaseDateResolver.ParseReleaseDate("ReleaseDate: yesterday\n").Should().BeNull();
        GitHubReleaseDateResolver.ParseReleaseDate("[unclosed").Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_UsesCommitsFirstThenManifestThenGivesUp()
    {
        FakeHttpHandler http = new FakeHttpHandler()
            .Map("commits?path=manifests%2Fg%2FGit%2FGit%2F2.48.1", Commits)
            .Map("commits?path=manifests%2Fb%2FBun%2FBun%2F1.4", "[]")
            .Map("/Bun/Bun/1.4/Bun.Bun.installer.yaml", "ReleaseDate: 2026-08-20\n")
            .Map("commits?path=manifests%2Fn%2FNo%2FDate%2F1.0", "[]");
        using HttpClient client = http.CreateClient();
        GitHubReleaseDateResolver resolver = new(client);

        (await resolver.ResolveAsync("Git.Git", "2.48.1", CancellationToken.None))
            .Should()
            .Be(new ResolvedDate(new DateTimeOffset(2026, 9, 1, 8, 30, 0, TimeSpan.Zero), PublishSource.WingetPkgs));
        (await resolver.ResolveAsync("Bun.Bun", "1.4", CancellationToken.None))
            .Should()
            .Be(new ResolvedDate(new DateTimeOffset(2026, 8, 20, 0, 0, 0, TimeSpan.Zero), PublishSource.Manifest));
        (await resolver.ResolveAsync("No.Date", "1.0", CancellationToken.None)).Should().BeNull();
        (await resolver.ResolveAsync("nodot", "1.0", CancellationToken.None)).Should().BeNull();
        http.Requests.Should().NotContain(static url => url.Contains("nodot", StringComparison.Ordinal));
    }
}
