using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;
using WingetNudge.Core.Tests.Support;
using WingetNudge.Core.Tools;

namespace WingetNudge.Core.Tests;

public sealed class ToolRegistryTests : IDisposable
{
    private readonly TempData _data = new();
    private readonly ToolRegistry _registry;

    public ToolRegistryTests()
    {
        _registry = new ToolRegistry(_data.Paths);
    }

    public void Dispose() => _data.Dispose();

    [Theory]
    [InlineData("bun", true)]
    [InlineData("uv.tool-1_2", true)]
    [InlineData("", false)]
    [InlineData("-leading", false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    public void IsValidId_AcceptsSimpleIdentifiers(string id, bool valid)
    {
        ToolRegistry.IsValidId(id).Should().Be(valid);
    }

    [Fact]
    public void Register_RejectsAMalformedId()
    {
        Action act = () => _registry.Register("bad id", Bun());

        act.Should().Throw<ArgumentException>();
        _registry.Load().Should().BeEmpty();
    }

    [Fact]
    public void RegisterAndUnregister_RoundTripAndDropTheCache()
    {
        _registry.Register("bun", Bun());
        _registry.SaveCache("bun", new ToolCacheEntry("1.4.2", DateTimeOffset.UnixEpoch));
        _registry.SaveCache("other", new ToolCacheEntry("9", DateTimeOffset.UnixEpoch));

        _registry.Load().Should().ContainKey("bun").WhoseValue.Should().BeEquivalentTo(Bun());
        _registry.Unregister("bun").Should().BeTrue();
        _registry.Unregister("bun").Should().BeFalse();
        _registry.Load().Should().BeEmpty();
        _registry.LoadCache().Keys.Should().Equal("other");
    }

    [Fact]
    public void Load_ReadsTheLegacyCamelCaseShape()
    {
        Directory.CreateDirectory(_data.Paths.Directory);
        File.WriteAllText(
            _data.Paths.ToolRegistry,
            """{ "bun": { "name": "Bun", "currentCommand": ["bun", "--version"], "latestUrl": "https://x", "latestJsonField": "tag_name", "upgradeCommand": "bun upgrade" } }"""
        );

        ToolDefinition tool = _registry.Load()["bun"];

        tool.CurrentCommand.Should().Equal("bun", "--version");
        tool.CurrentRegex.Should().BeNull();
    }

    internal static ToolDefinition Bun() =>
        new()
        {
            Name = "Bun",
            CurrentCommand = ["bun", "--version"],
            CurrentRegex = @"^(\d+\.\d+\.\d+)$",
            LatestUrl = "https://api.github.com/repos/oven-sh/bun/releases/latest",
            LatestJsonField = "tag_name",
            LatestRegex = "^bun-v(.+)$",
            UpgradeCommand = "bun upgrade",
        };
}

public sealed class ToolProberTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly TempData _data = new();
    private readonly FakeTimeProvider _clock = new(Now);
    private readonly FakeProcessRunner _processes = new();
    private readonly FakeHttpHandler _http = new();
    private readonly HttpClient _client;
    private readonly ToolRegistry _registry;
    private readonly ToolProber _prober;

    public ToolProberTests()
    {
        _client = _http.CreateClient();
        _registry = new ToolRegistry(_data.Paths);
        _prober = new ToolProber(_registry, _processes, _client, _clock);
    }

    public void Dispose()
    {
        _client.Dispose();
        _data.Dispose();
    }

    [Theory]
    [InlineData("1.4.2", null, "1.4.2")]
    [InlineData("bun-v1.4.2", "^bun-v(.+)$", "1.4.2")]
    [InlineData("bun-v1.4.2", "^uv-v(.+)$", null)]
    [InlineData("bun-v1.4.2", "no groups", null)]
    [InlineData("", null, null)]
    [InlineData("   ", "^(.*)$", null)]
    public void ExtractVersion_AppliesTheSingleGroupRegex(string value, string? pattern, string? expected)
    {
        ToolProber.ExtractVersion(value, pattern).Should().Be(expected);
    }

    [Fact]
    public async Task GetCurrentAsync_ParsesTheCommandOutputAndSwallowsFailures()
    {
        _processes.Map("bun", "1.3.13\n");

        (await _prober.GetCurrentAsync(ToolRegistryTests.Bun(), CancellationToken.None)).Should().Be("1.3.13");
        (
            await _prober.GetCurrentAsync(
                ToolRegistryTests.Bun() with
                {
                    CurrentCommand = ["missing-tool"],
                },
                CancellationToken.None
            )
        )
            .Should()
            .BeNull();
        (await _prober.GetCurrentAsync(ToolRegistryTests.Bun() with { CurrentCommand = [] }, CancellationToken.None))
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task GetLatestAsync_FetchesParsesCachesAndHonorsTheTtl()
    {
        _http.Map("oven-sh/bun/releases/latest", """{ "tag_name": "bun-v1.4.2" }""");

        (await _prober.GetLatestAsync("bun", ToolRegistryTests.Bun(), false, CancellationToken.None))
            .Should()
            .Be("1.4.2");
        _registry.LoadCache()["bun"].Should().Be(new ToolCacheEntry("1.4.2", Now));

        _clock.Advance(TimeSpan.FromHours(23));
        await _prober.GetLatestAsync("bun", ToolRegistryTests.Bun(), false, CancellationToken.None);
        _http.Requests.Should().HaveCount(1, "a fresh cache entry is served without a request");

        _clock.Advance(TimeSpan.FromHours(2));
        await _prober.GetLatestAsync("bun", ToolRegistryTests.Bun(), false, CancellationToken.None);
        _http.Requests.Should().HaveCount(2, "a stale entry is refetched");

        await _prober.GetLatestAsync("bun", ToolRegistryTests.Bun(), true, CancellationToken.None);
        _http.Requests.Should().HaveCount(3, "force bypasses the cache");
    }

    [Fact]
    public async Task GetLatestAsync_ReturnsNullOnMissingFieldRegexMissOrHttpFailure()
    {
        _http
            .Map("no-field", """{ "name": "x" }""")
            .Map("regex-miss", """{ "tag_name": "v1" }""")
            .Map("server-error", "", HttpStatusCode.InternalServerError);
        ToolDefinition tool = ToolRegistryTests.Bun();

        (
            await _prober.GetLatestAsync(
                "a",
                tool with
                {
                    LatestUrl = "https://x/no-field",
                },
                false,
                CancellationToken.None
            )
        )
            .Should()
            .BeNull();
        (
            await _prober.GetLatestAsync(
                "b",
                tool with
                {
                    LatestUrl = "https://x/regex-miss",
                },
                false,
                CancellationToken.None
            )
        )
            .Should()
            .BeNull();
        (
            await _prober.GetLatestAsync(
                "c",
                tool with
                {
                    LatestUrl = "https://x/server-error",
                },
                false,
                CancellationToken.None
            )
        )
            .Should()
            .BeNull();
        (await _prober.GetLatestAsync("d", tool with { LatestUrl = "" }, false, CancellationToken.None))
            .Should()
            .BeNull();
        _registry.LoadCache().Should().BeEmpty("failures are never cached");
    }

    [Fact]
    public async Task GetStatusesAsync_ProbesEveryToolInIdOrderAndFlagsDifferences()
    {
        _registry.Register(
            "uv",
            ToolRegistryTests.Bun() with
            {
                Name = "uv",
                CurrentCommand = ["uv", "--version"],
                CurrentRegex = null,
                LatestUrl = "https://x/uv",
                LatestRegex = null,
            }
        );
        _registry.Register("bun", ToolRegistryTests.Bun());
        _processes.Map("bun", "1.3.13").Map("uv", "0.5.0");
        _http.Map("oven-sh/bun", """{ "tag_name": "bun-v1.4.2" }""").Map("/uv", """{ "tag_name": "0.5.0" }""");

        IReadOnlyList<ToolStatus> statuses = await _prober.GetStatusesAsync(CancellationToken.None);

        statuses
            .Select(static s => (s.Id, s.Current, s.Latest, s.UpdateAvailable))
            .Should()
            .Equal(("bun", "1.3.13", "1.4.2", true), ("uv", "0.5.0", "0.5.0", false));
    }
}

public sealed class ToolStatusVersionTests
{
    private static ToolStatus Status(string? current, string? latest) =>
        new(
            "tool",
            new ToolDefinition
            {
                Name = "Tool",
                CurrentCommand = ["tool", "--version"],
                LatestUrl = "https://example.invalid",
                LatestJsonField = "tag_name",
                UpgradeCommand = "tool upgrade",
            },
            current,
            latest
        );

    [Theory]
    [InlineData("1.3.13", "1.4.2", true)]
    [InlineData("0.12.1", "0.13.0", true)]
    // The same release written two ways is not an update.
    [InlineData("1.4.2", "v1.4.2", false)]
    [InlineData("1.4.0.0", "1.4", false)]
    // A tool ahead of its published release is not an update either.
    [InlineData("1.5.0", "1.4.2", false)]
    public void UpdateAvailable_OnlyWhenThePublishedVersionIsNewer(string current, string latest, bool expected)
    {
        Status(current, latest).UpdateAvailable.Should().Be(expected);
    }

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("1.0", null)]
    [InlineData("", "1.0")]
    [InlineData("1.0", "  ")]
    public void UpdateAvailable_IsFalseWhenEitherProbeFailed(string? current, string? latest)
    {
        Status(current, latest).UpdateAvailable.Should().BeFalse();
    }
}
