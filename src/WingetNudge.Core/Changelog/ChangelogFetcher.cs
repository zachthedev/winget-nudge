using System.Text.Json;
using System.Text.RegularExpressions;
using WingetNudge.Core.Packages;
using YamlDotNet.RepresentationModel;

namespace WingetNudge.Core.Changelog;

/// <summary>Release notes parsed from a winget locale manifest.</summary>
/// <param name="Notes">Inline notes, or <c>null</c>.</param>
/// <param name="Url">Release notes URL, or <c>null</c>.</param>
public sealed record ManifestNotes(string? Notes, string? Url);

/// <summary>
/// Finds release notes for packages about to upgrade: the winget-pkgs manifest first, then
/// GitHub releases covering the installed-to-available range, then the bare URL.
/// </summary>
/// <param name="http">Client with a user agent set. GitHub rejects requests without one.</param>
/// <param name="cache">Notes already fetched, or <c>null</c> to fetch every time.</param>
public sealed partial class ChangelogFetcher(HttpClient http, ChangelogCache? cache = null)
{
    /// <summary>Simultaneous requests while fetching notes.</summary>
    public const int MaxConcurrentRequests = 4;

    [GeneratedRegex(@"\.windows\.\d+$")]
    private static partial Regex WindowsSuffix();

    /// <summary>Fetches a changelog per package, omitting packages with nothing found.</summary>
    /// <param name="packages">Packages with installed and available versions.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Formatted notes keyed by package id.</returns>
    public async Task<IReadOnlyDictionary<string, string>> FetchAsync(
        IReadOnlyList<PackageInfo> packages,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        Dictionary<string, (string Owner, string Repo, ManifestNotes Manifest)> gitHub = new(StringComparer.Ordinal);
        using SemaphoreSlim gate = new(MaxConcurrentRequests);

        // ///// Cache /////

        List<PackageInfo> missing = [];
        foreach (PackageInfo package in packages)
        {
            if (cache?.Get(package) is string cached)
            {
                result[package.Id] = cached;
            }
            else
            {
                missing.Add(package);
            }
        }

        if (missing.Count == 0)
        {
            return result;
        }

        // ///// Manifests, concurrently /////

        Dictionary<string, Task<string?>> manifestTasks = new(StringComparer.Ordinal);
        foreach (PackageInfo package in missing)
        {
            Uri? url = WingetPkgs.LocaleManifestUrl(package.Id, package.AvailableVersion);
            if (url is not null)
            {
                manifestTasks[package.Id] = TryGetAsync(url, gate, cancellationToken);
            }
        }

        foreach ((string id, Task<string?> task) in manifestTasks)
        {
            string? yaml = await task.ConfigureAwait(false);
            if (yaml is null)
            {
                continue;
            }

            ManifestNotes parsed = ParseManifest(yaml);
            if (parsed.Url is not null && TryParseGitHubRepo(parsed.Url, out string owner, out string repo))
            {
                gitHub[id] = (owner, repo, parsed);
            }
            else if (parsed.Notes is not null)
            {
                result[id] = ChangelogFormatter.Format(parsed.Notes);
            }
            else if (parsed.Url is not null)
            {
                result[id] = $"changelog: {parsed.Url}";
            }
        }

        // ///// GitHub releases for the version range /////

        // A package whose releases lookup failed gets its fallback text this run, but that text
        // must not be cached in place of the real notes.
        HashSet<string> incomplete = new(StringComparer.Ordinal);

        Dictionary<string, Task<string?>> releaseTasks = new(StringComparer.Ordinal);
        foreach ((string id, (string owner, string repo, _)) in gitHub)
        {
            Uri api = new(
                $"https://api.github.com/repos/{Uri.EscapeDataString(owner)}/{Uri.EscapeDataString(repo)}/releases?per_page=50"
            );
            releaseTasks[id] = TryGetAsync(api, gate, cancellationToken);
        }

        foreach ((string id, Task<string?> task) in releaseTasks)
        {
            ManifestNotes manifest = gitHub[id].Manifest;
            PackageInfo package = packages.First(candidate => candidate.Id == id);
            string? fromReleases = null;
            string? json = await task.ConfigureAwait(false);
            if (json is null)
            {
                incomplete.Add(id);
            }
            else
            {
                try
                {
                    fromReleases = SelectReleaseNotes(json, package.InstalledVersion, package.AvailableVersion);
                }
                catch (JsonException)
                {
                    // Fall through to the manifest notes or URL.
                    incomplete.Add(id);
                }
            }

            result[id] =
                fromReleases is not null ? ChangelogFormatter.Format(fromReleases)
                : manifest.Notes is not null ? ChangelogFormatter.Format(manifest.Notes)
                : $"changelog: {manifest.Url}";
        }

        Remember(result, missing.Where(package => !incomplete.Contains(package.Id)));
        return result;
    }

    /// <summary>Caches the notes this run fetched in full.</summary>
    /// <param name="result">Every note in hand, cached and freshly fetched alike.</param>
    /// <param name="complete">Packages whose every lookup this run succeeded.</param>
    private void Remember(Dictionary<string, string> result, IEnumerable<PackageInfo> complete)
    {
        if (cache is null)
        {
            return;
        }

        List<(PackageInfo Package, string Notes)> fresh = [];
        foreach (PackageInfo package in complete)
        {
            if (result.TryGetValue(package.Id, out string? notes))
            {
                fresh.Add((package, notes));
            }
        }

        cache.Store(fresh);
    }

    /// <summary>
    /// Extracts owner and repository from a release notes URL, accepting only github.com hosts
    /// so a URL that merely mentions GitHub cannot redirect the lookup.
    /// </summary>
    /// <param name="url">Release notes URL from the manifest.</param>
    /// <param name="owner">Repository owner.</param>
    /// <param name="repo">Repository name without a <c>.git</c> suffix.</param>
    /// <returns><c>true</c> when the URL points into a GitHub repository.</returns>
    public static bool TryParseGitHubRepo(string url, out string owner, out string repo)
    {
        owner = "";
        repo = "";
        if (
            !Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
            || parsed.Scheme != "https"
            || !(
                parsed.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || parsed.Host.Equals("www.github.com", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return false;
        }

        string[] segments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (
            segments.Length < 2
            || segments[0] == "."
            || segments[0] == ".."
            || segments[1] == "."
            || segments[1] == ".."
        )
        {
            return false;
        }

        owner = segments[0];
        repo = segments[1].EndsWith(".git", StringComparison.Ordinal) ? segments[1][..^4] : segments[1];
        return owner.Length > 0 && repo.Length > 0;
    }

    /// <summary>Fetches a document, treating any HTTP failure or timeout as absent.</summary>
    private async Task<string?> TryGetAsync(Uri url, SemaphoreSlim gate, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client timeout, not the caller's cancellation.
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Reads <c>ReleaseNotes</c> and <c>ReleaseNotesUrl</c> from a locale manifest.</summary>
    /// <param name="yaml">Manifest text.</param>
    /// <returns>Whatever the manifest carries; both fields null when it parses as nothing useful.</returns>
    public static ManifestNotes ParseManifest(string yaml)
    {
        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return new ManifestNotes(null, null);
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return new ManifestNotes(null, null);
        }

        string? notes = Scalar(root, "ReleaseNotes");
        string? url = Scalar(root, "ReleaseNotesUrl");
        return new ManifestNotes(
            string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            string.IsNullOrWhiteSpace(url) ? null : url.Trim()
        );
    }

    /// <summary>
    /// Joins the bodies of stable releases newer than the installed version and no newer than
    /// the available one, each under a heading with its tag.
    /// </summary>
    /// <param name="releasesJson">GitHub releases API response.</param>
    /// <param name="installed">Installed version.</param>
    /// <param name="available">Target version.</param>
    /// <returns>Joined bodies, or <c>null</c> when no release qualifies.</returns>
    public static string? SelectReleaseNotes(string releasesJson, string? installed, string? available)
    {
        using JsonDocument document = JsonDocument.Parse(releasesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        Version? low = Version.TryParse(installed, out Version? parsedLow) ? parsedLow : null;
        Version? high = Version.TryParse(available, out Version? parsedHigh) ? parsedHigh : null;
        List<string> bodies = [];
        foreach (JsonElement release in document.RootElement.EnumerateArray())
        {
            string? body = release.TryGetProperty("body", out JsonElement bodyElement) ? bodyElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            if (release.TryGetProperty("prerelease", out JsonElement pre) && pre.ValueKind == JsonValueKind.True)
            {
                continue;
            }

            string tagName = release.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString() ?? ""
                : "";
            string tag = NormalizeTag(tagName);

            if (low is not null && high is not null && Version.TryParse(tag, out Version? tagVersion))
            {
                if (tagVersion > low && tagVersion <= high)
                {
                    bodies.Add($"### {tagName}\n\n{body.Trim()}");
                }
            }
            else if (string.Equals(tag, available, StringComparison.Ordinal))
            {
                bodies.Add(body);
            }
        }

        return bodies.Count == 0 ? null : string.Join("\n\n", bodies);
    }

    /// <summary>Reduces a release tag to a comparable version string.</summary>
    /// <param name="tagName">Raw tag, for example <c>v2.48.1.windows.1</c>.</param>
    /// <returns>The tag without a leading <c>v</c>, a pre-release suffix or a <c>.windows.N</c> suffix.</returns>
    public static string NormalizeTag(string tagName)
    {
        string tag = tagName.StartsWith('v') ? tagName[1..] : tagName;
        tag = tag.Split('-')[0];
        return WindowsSuffix().Replace(tag, "");
    }

    private static string? Scalar(YamlMappingNode root, string key) =>
        root.Children.TryGetValue(new YamlScalarNode(key), out YamlNode? node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;
}
