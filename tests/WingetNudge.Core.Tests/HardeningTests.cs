using System.Net;
using AwesomeAssertions;
using WingetNudge.Core.Changelog;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tests.Support;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Tracking;
using WingetNudge.Core.Upgrade;

namespace WingetNudge.Core.Tests;

public sealed class PackageIdBoundaryTests
{
    [Theory]
    [InlineData("Git.Git\n", false)]
    [InlineData("Git.Git\r", false)]
    [InlineData("Git.Git\n--force", false)]
    [InlineData("\nGit.Git", false)]
    [InlineData("Git.Git", true)]
    public void IsValid_RejectsLineBreaksAnywhere(string id, bool valid)
    {
        PackageIdValidator.IsValid(id).Should().Be(valid);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("Git", true)]
    [InlineData("Visual Studio Code (User)", true)]
    [InlineData("Git\n", false)]
    [InlineData("Git\u0000", false)]
    public void IsValidName_RejectsControlCharacters(string name, bool valid)
    {
        PackageIdValidator.IsValidName(name).Should().Be(valid);
    }

    [Fact]
    public void IsValidName_RejectsOverlongNames()
    {
        PackageIdValidator
            .IsValidName(new string('a', PackageIdValidator.MaxNameLength))
            .Should()
            .BeTrue();
        PackageIdValidator
            .IsValidName(new string('a', PackageIdValidator.MaxNameLength + 1))
            .Should()
            .BeFalse();
    }
}

public sealed class InstallLocationTrustTests : IDisposable
{
    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void IsTrustedInstallDirectory_RejectsWindowsAndProtectedProgramFolders()
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        InstallLocationIndex.IsTrustedInstallDirectory(windows).Should().BeFalse();
        InstallLocationIndex
            .IsTrustedInstallDirectory(Path.Combine(windows, "System32"))
            .Should()
            .BeFalse();
        InstallLocationIndex
            .IsTrustedInstallDirectory(Path.Combine(programs, "WindowsApps"))
            .Should()
            .BeFalse();
        InstallLocationIndex
            .IsTrustedInstallDirectory(Path.Combine(programs, "Windows Defender"))
            .Should()
            .BeFalse();
        InstallLocationIndex
            .IsTrustedInstallDirectory(Path.GetPathRoot(windows) ?? @"C:\")
            .Should()
            .BeFalse();
        InstallLocationIndex
            .IsTrustedInstallDirectory(Path.Combine(programs, "Git"))
            .Should()
            .BeTrue();
        InstallLocationIndex.IsTrustedInstallDirectory(_data.Root).Should().BeTrue();
    }

    [Fact]
    public void Resolve_IgnoresProtectedDirectoriesEvenWhenTheRegistryPointsThere()
    {
        string system32 = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32"
        );
        InstallLocationIndex index = new();
        index.Add("Git.Git", "Git", system32);

        index.Resolve(new PackageRef("Git.Git", "Git")).Should().BeNull();
    }

    [Fact]
    public void Resolve_RequiresAWholeWordNameMatchOfThreeOrMoreCharacters()
    {
        string directory = Path.Combine(_data.Root, "app");
        Directory.CreateDirectory(directory);
        InstallLocationIndex index = new();
        index.Add(null, "Advanced Tool 2.0", directory);

        index
            .Resolve(new PackageRef("X.Y", "A"))
            .Should()
            .BeNull("one character matches everything");
        index
            .Resolve(new PackageRef("X.Y", "Adv"))
            .Should()
            .BeNull("a prefix inside a word is not the same product");
        index.Resolve(new PackageRef("X.Y", "Advanced Tool")).Should().Be(directory);
        index.Resolve(new PackageRef("X.Y", "Advanced Tool 2.0")).Should().Be(directory);
    }

    [Fact]
    public void EnumerateExecutables_GivesUpPastTheCap()
    {
        string directory = Path.Combine(_data.Root, "many");
        Directory.CreateDirectory(directory);
        for (int index = 0; index <= InstallLocationIndex.MaxExecutables; index++)
        {
            File.WriteAllText(Path.Combine(directory, $"tool{index}.exe"), "");
        }

        InstallLocationIndex.EnumerateExecutables(directory).Should().BeEmpty();
    }
}

public sealed class ToolDefinitionValidatorTests : IDisposable
{
    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Theory]
    [InlineData("https://api.github.com/x", true)]
    [InlineData("http://example.test/x", false)]
    [InlineData("file:///C:/Windows/win.ini", false)]
    [InlineData("ftp://example.test/x", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void TryParseLatestUrl_AcceptsOnlyWebSchemes(string url, bool valid)
    {
        ToolDefinitionValidator.TryParseLatestUrl(url, out _).Should().Be(valid);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("^bun-v(.+)$", true)]
    [InlineData("[", false)]
    [InlineData("nogroup", false)]
    public void IsValidRegex_RequiresACompilingPatternWithOneGroup(string? pattern, bool valid)
    {
        ToolDefinitionValidator.IsValidRegex(pattern).Should().Be(valid);
    }

    [Fact]
    public void Register_RejectsABadUrlOrRegexBeforeStoring()
    {
        ToolRegistry registry = new(_data.Paths);
        ToolDefinition bun = ToolRegistryTests.Bun();

        Action badUrl = () => registry.Register("bun", bun with { LatestUrl = "file:///C:/x" });
        Action badRegex = () => registry.Register("bun", bun with { LatestRegex = "[" });

        badUrl.Should().Throw<ArgumentException>();
        badRegex.Should().Throw<ArgumentException>();
        registry.Load().Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatusesAsync_SurvivesAPoisonedEntryFromAnOlderFile()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(
            _data.Paths.ToolRegistry,
            """{ "bad": { "name": "Bad", "currentCommand": ["missing"], "currentRegex": "[", "latestUrl": "not a url", "latestJsonField": "tag_name", "upgradeCommand": "x" } }"""
        );
        ToolRegistry registry = new(_data.Paths);
        using HttpClient http = new FakeHttpHandler().CreateClient();
        ToolProber prober = new(registry, new FakeProcessRunner(), http, TimeProvider.System);

        IReadOnlyList<ToolStatus> statuses = await prober.GetStatusesAsync(CancellationToken.None);

        statuses
            .Should()
            .ContainSingle()
            .Which.Should()
            .BeEquivalentTo(
                new
                {
                    Current = (string?)null,
                    Latest = (string?)null,
                    UpdateAvailable = false,
                }
            );
    }
}

public sealed class GitHubUrlTests
{
    [Theory]
    [InlineData("https://github.com/git/git/releases", "git", "git")]
    [InlineData("https://www.github.com/oven-sh/bun.git", "oven-sh", "bun")]
    [InlineData("https://github.com/owner/repo?tab=x", "owner", "repo")]
    public void TryParseGitHubRepo_ReadsOwnerAndRepoFromRealGitHubUrls(
        string url,
        string owner,
        string repo
    )
    {
        ChangelogFetcher
            .TryParseGitHubRepo(url, out string parsedOwner, out string parsedRepo)
            .Should()
            .BeTrue();
        parsedOwner.Should().Be(owner);
        parsedRepo.Should().Be(repo);
    }

    [Theory]
    [InlineData("https://evil.example/github.com/attacker/repo")]
    [InlineData("https://github.com/../../search?q=x")]
    [InlineData("https://github.com/onlyowner")]
    [InlineData("http://github.com/o/r")]
    [InlineData("https://notgithub.com/o/r")]
    [InlineData("github.com/o/r")]
    public void TryParseGitHubRepo_RejectsLookalikesAndTraversal(string url)
    {
        ChangelogFetcher.TryParseGitHubRepo(url, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ManifestFolder_EscapesReservedCharacters()
    {
        WingetPkgs
            .ManifestFolder("Git.Git?x=", "1#f")
            .Should()
            .Be("manifests/g/Git/Git%3Fx%3D/1%23f");
    }

    [Fact]
    public async Task FetchAsync_TreatsATimeoutAsMissingNotesRatherThanCancellation()
    {
        using HttpClient client = new(new HangingHandler())
        {
            Timeout = TimeSpan.FromMilliseconds(200),
        };
        ChangelogFetcher fetcher = new(client);

        IReadOnlyDictionary<string, string> notes = await fetcher.FetchAsync(
            [Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")],
            CancellationToken.None
        );

        notes.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAsync_StillHonorsTheCallersCancellation()
    {
        using HttpClient client = new(new HangingHandler());
        ChangelogFetcher fetcher = new(client);
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));

        Func<Task> act = () =>
            fetcher.FetchAsync(
                [Fixture.Updatable("Git.Git", "2.47.0", "2.48.1")],
                cancellation.Token
            );

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void IsExhausted_ReadsTheRateLimitHeaderOffA403()
    {
        using HttpResponseMessage limited = new(HttpStatusCode.Forbidden);
        limited.Headers.Add("X-RateLimit-Remaining", "0");
        using HttpResponseMessage forbidden = new(HttpStatusCode.Forbidden);
        forbidden.Headers.Add("X-RateLimit-Remaining", "12");
        using HttpResponseMessage ok = new(HttpStatusCode.OK);
        ok.Headers.Add("X-RateLimit-Remaining", "0");

        GitHubRateLimit.IsExhausted(limited).Should().BeTrue();
        GitHubRateLimit.IsExhausted(forbidden).Should().BeFalse();
        GitHubRateLimit.IsExhausted(ok).Should().BeFalse();
    }

    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}

public sealed class JsonFileTests : IDisposable
{
    private readonly TempData _data = new();

    public void Dispose() => _data.Dispose();

    [Fact]
    public void Write_ReplacesTheFileAtomicallyAndLeavesNoTemporaryBehind()
    {
        string path = Path.Combine(_data.Paths.Directory, "state.json");

        JsonFile.Write(path, new Dictionary<string, int> { ["a"] = 1 });
        JsonFile.Write(path, new Dictionary<string, int> { ["b"] = 2 });

        JsonFile
            .Read<Dictionary<string, int>>(path, deleteIfCorrupt: false)
            .Should()
            .Equal(new Dictionary<string, int> { ["b"] = 2 });
        Directory.GetFiles(_data.Paths.Directory).Should().ContainSingle();
    }

    [Fact]
    public void Read_SetsACorruptUserFileAsideInsteadOfDeletingIt()
    {
        string path = Path.Combine(_data.Paths.Directory, "prefs.json");
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(path, "{{{");

        JsonFile.Read<Dictionary<string, int>>(path, deleteIfCorrupt: false).Should().BeNull();

        File.Exists(path).Should().BeFalse();
        Directory.GetFiles(_data.Paths.Directory, "prefs.json.*.corrupt").Should().ContainSingle();
    }
}

public sealed class GitHubAuthorizationHandlerTests
{
    [Theory]
    [InlineData("https://api.github.com/repos/o/r/releases", true)]
    [InlineData("https://attacker.example/latest.json", false)]
    [InlineData("https://api.github.com.evil.example/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    public async Task SendAsync_AttachesTheTokenOnlyToTheGitHubApi(string url, bool expected)
    {
        RecordingHandler inner = new();
        using HttpClient client = new(
            new GitHubAuthorizationHandler("secret") { InnerHandler = inner }
        );

        await client.GetAsync(new Uri(url), TestContext.Current.CancellationToken);

        (inner.LastAuthorization is not null).Should().Be(expected);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            LastAuthorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") }
            );
        }
    }
}
