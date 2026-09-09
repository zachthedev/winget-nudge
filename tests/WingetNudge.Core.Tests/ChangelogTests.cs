using AwesomeAssertions;
using WingetNudge.Core.Changelog;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Tests.Support;

namespace WingetNudge.Core.Tests;

public sealed class WingetPkgsTests
{
    [Theory]
    [InlineData("Git.Git", "2.48.1", "manifests/g/Git/Git/2.48.1")]
    [InlineData("Python.Python.3.13", "3.13.1", "manifests/p/Python/Python/3/13/3.13.1")]
    [InlineData("7zip.7zip", "26.03", "manifests/7/7zip/7zip/26.03")]
    [InlineData("nodot", "1.0", null)]
    [InlineData("Git.Git", "", null)]
    [InlineData("Git.Git", null, null)]
    public void ManifestFolder_SplitsThePublisherAndNestsTheRest(
        string id,
        string? version,
        string? expected
    )
    {
        WingetPkgs.ManifestFolder(id, version).Should().Be(expected);
    }

    [Fact]
    public void ManifestUrls_PointAtTheRawLocaleAndInstallerFiles()
    {
        WingetPkgs
            .LocaleManifestUrl("Git.Git", "2.48.1")
            ?.ToString()
            .Should()
            .Be(
                "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/manifests/g/Git/Git/2.48.1/Git.Git.locale.en-US.yaml"
            );
        WingetPkgs
            .InstallerManifestUrl("Git.Git", "2.48.1")
            ?.ToString()
            .Should()
            .EndWith("/2.48.1/Git.Git.installer.yaml");
        WingetPkgs.LocaleManifestUrl("nodot", "1").Should().BeNull();
    }
}

public sealed class ChangelogFormatterTests
{
    [Fact]
    public void Format_DropsChecksumBlocksBadgesBinaryNotesAndHtml()
    {
        const string raw = """
            ## Changes
            - Fixed <b>a thing</b><br/>
            [![build](https://img.shields.io/badge/x.svg)](https://x)


            _Binary files inside the archive_
            ### Checksums
            file.exe 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef

            ## Next
            done
            """;

        ChangelogFormatter.Format(raw).Should().Be("## Changes\n- Fixed a thing\n\n## Next\ndone");
    }

    [Fact]
    public void Format_LeavesPlainNotesAloneApartFromTrimming()
    {
        ChangelogFormatter.Format("  hello\nworld  \n\n").Should().Be("hello\nworld");
    }
}

public sealed class ChangelogFetcherTests
{
    [Fact]
    public void ParseManifest_ReadsBlockAndPlainScalarsAndTheUrl()
    {
        const string blockYaml = """
            PackageIdentifier: Git.Git
            ReleaseNotes: |-
              - one
              - two
            ReleaseNotesUrl: https://github.com/git/git/releases
            """;

        ChangelogFetcher
            .ParseManifest(blockYaml)
            .Should()
            .Be(new ManifestNotes("- one\n- two", "https://github.com/git/git/releases"));
        ChangelogFetcher
            .ParseManifest("ReleaseNotes: short note\n")
            .Should()
            .Be(new ManifestNotes("short note", null));
        ChangelogFetcher
            .ParseManifest("PackageIdentifier: X\n")
            .Should()
            .Be(new ManifestNotes(null, null));
        ChangelogFetcher.ParseManifest("key: [unclosed").Should().Be(new ManifestNotes(null, null));
        ChangelogFetcher
            .ParseManifest("- a list, not a map")
            .Should()
            .Be(new ManifestNotes(null, null));
    }

    [Theory]
    [InlineData("v2.48.1", "2.48.1")]
    [InlineData("2.48.1.windows.1", "2.48.1")]
    [InlineData("v1.2.3-beta.1", "1.2.3")]
    [InlineData("release-5", "release")]
    public void NormalizeTag_StripsPrefixSuffixAndPreReleaseParts(string tag, string expected)
    {
        ChangelogFetcher.NormalizeTag(tag).Should().Be(expected);
    }

    [Fact]
    public void SelectReleaseNotes_KeepsStableReleasesInsideTheRangeNewestFirst()
    {
        const string json = """
            [
              { "tag_name": "v2.49.0", "prerelease": false, "body": "too new" },
              { "tag_name": "v2.48.1", "prerelease": false, "body": "target" },
              { "tag_name": "v2.48.0-rc1", "prerelease": true, "body": "rc" },
              { "tag_name": "v2.48.0", "prerelease": false, "body": "middle" },
              { "tag_name": "v2.47.0", "prerelease": false, "body": "installed" },
              { "tag_name": "v2.46.0", "prerelease": false, "body": "" }
            ]
            """;

        ChangelogFetcher
            .SelectReleaseNotes(json, "2.47.0", "2.48.1")
            .Should()
            .Be("### v2.48.1\n\ntarget\n\n### v2.48.0\n\nmiddle");
    }

    [Fact]
    public void SelectReleaseNotes_FallsBackToTheExactTagWhenVersionsDoNotParse()
    {
        const string json =
            """[ { "tag_name": "nightly", "body": "n" }, { "tag_name": "2024.05", "body": "dated" } ]""";

        ChangelogFetcher
            .SelectReleaseNotes(json, "2024.04", "2024.05")
            .Should()
            .Be("### 2024.05\n\ndated", "two-part versions still parse");
        ChangelogFetcher.SelectReleaseNotes(json, "unknown", "nightly").Should().Be("n");
        ChangelogFetcher.SelectReleaseNotes(json, "1.0", "9.9").Should().BeNull();
        ChangelogFetcher.SelectReleaseNotes("{}", "1.0", "2.0").Should().BeNull();
    }

    [Fact]
    public async Task FetchAsync_PrefersGitHubReleasesThenManifestNotesThenTheUrl()
    {
        FakeHttpHandler http = new FakeHttpHandler()
            .Map(
                "/Git/Git/2.48.1/Git.Git.locale.en-US.yaml",
                "ReleaseNotesUrl: https://github.com/git/git.git/releases\nReleaseNotes: manifest notes\n"
            )
            .Map(
                "/repos/git/git/releases",
                """[ { "tag_name": "v2.48.1", "body": "from github" } ]"""
            )
            .Map(
                "/Inline/Notes/1.1/Inline.Notes.locale.en-US.yaml",
                "ReleaseNotes: |-\n  inline only\n"
            )
            .Map(
                "/Url/Only/1.1/Url.Only.locale.en-US.yaml",
                "ReleaseNotesUrl: https://example.com/notes\n"
            )
            .Map(
                "/Broken/Api/1.1/Broken.Api.locale.en-US.yaml",
                "ReleaseNotesUrl: https://github.com/broken/api\nReleaseNotes: manifest fallback\n"
            );
        using HttpClient client = http.CreateClient();
        ChangelogFetcher fetcher = new(client);
        PackageInfo[] packages =
        [
            Fixture.Updatable("Git.Git", "2.47.0", "2.48.1"),
            Fixture.Updatable("Inline.Notes", "1.0", "1.1"),
            Fixture.Updatable("Url.Only", "1.0", "1.1"),
            Fixture.Updatable("Broken.Api", "1.0", "1.1"),
            Fixture.Updatable("Missing.Manifest", "1.0", "1.1"),
        ];

        IReadOnlyDictionary<string, string> notes = await fetcher.FetchAsync(
            packages,
            CancellationToken.None
        );

        notes
            .Should()
            .Equal(
                new Dictionary<string, string>
                {
                    ["Git.Git"] = "### v2.48.1\n\nfrom github",
                    ["Inline.Notes"] = "inline only",
                    ["Url.Only"] = "changelog: https://example.com/notes",
                    ["Broken.Api"] = "manifest fallback",
                }
            );
        http.Requests.Should()
            .Contain(
                static url =>
                    url.Contains("/repos/git/git/releases?per_page=50", StringComparison.Ordinal),
                "the .git suffix is stripped from the repo name"
            );
    }
}
