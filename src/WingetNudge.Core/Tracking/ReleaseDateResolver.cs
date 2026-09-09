using System.Globalization;
using System.Text.Json;
using WingetNudge.Core.Changelog;
using YamlDotNet.RepresentationModel;

namespace WingetNudge.Core.Tracking;

/// <summary>A resolved publish date and where it came from.</summary>
/// <param name="Date">When the version became available.</param>
/// <param name="Source">Which lookup answered.</param>
public sealed record ResolvedDate(DateTimeOffset Date, PublishSource Source);

/// <summary>Looks up when a package version became available.</summary>
public interface IReleaseDateResolver
{
    /// <summary>Resolves the publish date of one version.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="version">Version string.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The date and its source, or <c>null</c> when every lookup failed.</returns>
    Task<ResolvedDate?> ResolveAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken
    );
}

/// <summary>
/// Resolves publish dates from microsoft/winget-pkgs: the oldest commit that touched the
/// version's manifest folder first, the installer manifest's <c>ReleaseDate</c> second.
/// </summary>
/// <param name="http">Client with a user agent set. GitHub rejects requests without one.</param>
public sealed class GitHubReleaseDateResolver(HttpClient http) : IReleaseDateResolver
{
    private bool _rateLimited;

    /// <inheritdoc/>
    public async Task<ResolvedDate?> ResolveAsync(
        string packageId,
        string version,
        CancellationToken cancellationToken
    )
    {
        string? folder = WingetPkgs.ManifestFolder(packageId, version);
        if (folder is null)
        {
            return null;
        }

        Uri commits = new(
            $"https://api.github.com/repos/microsoft/winget-pkgs/commits?path={Uri.EscapeDataString(folder)}&per_page=100"
        );
        string? json = await TryGetAsync(commits, cancellationToken).ConfigureAwait(false);
        if (json is not null && ParseOldestCommitDate(json) is DateTimeOffset committed)
        {
            return new ResolvedDate(committed, PublishSource.WingetPkgs);
        }

        Uri? manifest = WingetPkgs.InstallerManifestUrl(packageId, version);
        string? yaml = manifest is null
            ? null
            : await TryGetAsync(manifest, cancellationToken).ConfigureAwait(false);
        if (yaml is not null && ParseReleaseDate(yaml) is DateTimeOffset released)
        {
            return new ResolvedDate(released, PublishSource.Manifest);
        }

        return null;
    }

    /// <summary>Finds the earliest committer date in a GitHub commits response.</summary>
    /// <param name="json">Response of the commits API.</param>
    /// <returns>The earliest date, or <c>null</c> when the response holds no dated commit.</returns>
    public static DateTimeOffset? ParseOldestCommitDate(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        DateTimeOffset? oldest = null;
        foreach (JsonElement entry in document.RootElement.EnumerateArray())
        {
            if (
                entry.TryGetProperty("commit", out JsonElement commit)
                && commit.TryGetProperty("committer", out JsonElement committer)
                && committer.TryGetProperty("date", out JsonElement date)
                && date.ValueKind == JsonValueKind.String
                && date.TryGetDateTimeOffset(out DateTimeOffset value)
                && (oldest is null || value < oldest)
            )
            {
                oldest = value;
            }
        }

        return oldest;
    }

    /// <summary>
    /// Reads <c>ReleaseDate</c> from an installer manifest: the top-level field first, then the
    /// first installer entry that carries one.
    /// </summary>
    /// <param name="yaml">Installer manifest text.</param>
    /// <returns>The date at midnight UTC, or <c>null</c> when absent or malformed.</returns>
    public static DateTimeOffset? ParseReleaseDate(string yaml)
    {
        YamlStream stream = new();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return null;
        }

        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return null;
        }

        if (TryScalarDate(root, out DateTimeOffset topLevel))
        {
            return topLevel;
        }

        if (
            root.Children.TryGetValue(new YamlScalarNode("Installers"), out YamlNode? installers)
            && installers is YamlSequenceNode sequence
        )
        {
            foreach (YamlNode installer in sequence.Children)
            {
                if (
                    installer is YamlMappingNode mapping
                    && TryScalarDate(mapping, out DateTimeOffset date)
                )
                {
                    return date;
                }
            }
        }

        return null;
    }

    private static bool TryScalarDate(YamlMappingNode node, out DateTimeOffset date)
    {
        date = default;
        return node.Children.TryGetValue(new YamlScalarNode("ReleaseDate"), out YamlNode? value)
            && value is YamlScalarNode scalar
            && DateTimeOffset.TryParse(
                scalar.Value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out date
            );
    }

    private async Task<string?> TryGetAsync(Uri url, CancellationToken cancellationToken)
    {
        if (_rateLimited && url.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            using HttpResponseMessage response = await http.GetAsync(url, cancellationToken)
                .ConfigureAwait(false);
            if (GitHubRateLimit.IsExhausted(response))
            {
                // Every further call this run would fail the same way; leave the versions
                // unresolved so the next check retries.
                _rateLimited = true;
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response
                .Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Timeout rather than cancellation.
            return null;
        }
    }
}
