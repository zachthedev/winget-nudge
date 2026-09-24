using System.Text.Json;
using System.Text.RegularExpressions;
using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Tools;

/// <summary>Probes manually registered tools for their installed and latest versions.</summary>
/// <param name="registry">Tool registry and cache.</param>
/// <param name="processes">Runs version commands.</param>
/// <param name="http">Fetches latest-version JSON. The caller sets the user agent.</param>
/// <param name="clock">Time source.</param>
/// <param name="cacheHours">Hours a cached result stays fresh.</param>
public sealed class ToolProber(
    ToolRegistry registry,
    IProcessRunner processes,
    HttpClient http,
    TimeProvider clock,
    int cacheHours = ToolProber.DefaultCacheHours
)
{
    /// <summary>Default hours a cached latest-version result stays fresh.</summary>
    /// <remarks>Keeps unauthenticated GitHub API use well under its 60 requests per hour.</remarks>
    public const int DefaultCacheHours = 24;

    /// <summary>Largest latest-version response the prober will read.</summary>
    public const int MaxResponseBytes = 4 * 1024 * 1024;

    /// <summary>Runs the tool's version command and extracts the version.</summary>
    /// <param name="tool">Tool definition.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or <c>null</c> when the command fails or the regex misses.</returns>
    public async Task<string?> GetCurrentAsync(ToolDefinition tool, CancellationToken cancellationToken)
    {
        if (tool.CurrentCommand.Count == 0)
        {
            return null;
        }

        string output;
        try
        {
            ProcessOutput result = await processes
                .RunAsync(tool.CurrentCommand[0], tool.CurrentCommand.Skip(1).ToArray(), cancellationToken)
                .ConfigureAwait(false);
            output = result.Output.Trim();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return null;
        }

        return ExtractVersion(output, tool.CurrentRegex);
    }

    /// <summary>
    /// Fetches the tool's latest version, serving from cache while the entry is fresh.
    /// </summary>
    /// <param name="id">Tool id, the cache key.</param>
    /// <param name="tool">Tool definition.</param>
    /// <param name="force">Bypass the cache and refetch.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The version, or <c>null</c> on any failure.</returns>
    public Task<string?> GetLatestAsync(
        string id,
        ToolDefinition tool,
        bool force,
        CancellationToken cancellationToken
    ) => GetLatestAsync(id, tool, force, [], cancellationToken);

    // The cache only saves a request, so a failed cache write never fails the lookup. It joins the
    // failures and diagnostics.log instead.
    private async Task<string?> GetLatestAsync(
        string id,
        ToolDefinition tool,
        bool force,
        ICollection<StateWriteFailure> failures,
        CancellationToken cancellationToken
    )
    {
        if (
            string.IsNullOrWhiteSpace(tool.LatestJsonField)
            || !ToolDefinitionValidator.TryParseLatestUrl(tool.LatestUrl, out Uri? latestUrl)
        )
        {
            return null;
        }

        Dictionary<string, ToolCacheEntry> cache = registry.LoadCache();
        DateTimeOffset now = clock.GetUtcNow();
        if (
            !force
            && cache.TryGetValue(id, out ToolCacheEntry? cached)
            && (now - cached.CheckedAt).TotalHours < cacheHours
        )
        {
            return cached.Latest;
        }

        string? latest;
        try
        {
            using HttpResponseMessage response = await http.GetAsync(latestUrl, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            // A release endpoint answers in kilobytes; anything larger is not one.
            using Stream bounded = new BoundedStream(body, MaxResponseBytes);
            using JsonDocument document = await JsonDocument
                .ParseAsync(bounded, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (
                !document.RootElement.TryGetProperty(tool.LatestJsonField, out JsonElement field)
                || field.ValueKind != JsonValueKind.String
            )
            {
                return null;
            }

            latest = ExtractVersion(field.GetString() ?? "", tool.LatestRegex);
        }
        catch (Exception exception)
            when (exception
                    is HttpRequestException
                        or JsonException
                        or TaskCanceledException
                        or NotSupportedException
                        or ArgumentException
            )
        {
            return null;
        }

        if (latest is null)
        {
            return null;
        }

        registry.SaveCache(id, new ToolCacheEntry(latest, now), clock, failures);
        return latest;
    }

    /// <summary>Probes every registered tool, sorted by id.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One status per tool. A failing probe yields a null version, never an exception.</returns>
    public Task<IReadOnlyList<ToolStatus>> GetStatusesAsync(CancellationToken cancellationToken) =>
        GetStatusesAsync([], cancellationToken);

    /// <summary>Probes every registered tool, sorted by id.</summary>
    /// <param name="failures">Receives each cache write that failed, which <c>diagnostics.log</c> also records.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>One status per tool. A failing probe yields a null version, never an exception.</returns>
    public async Task<IReadOnlyList<ToolStatus>> GetStatusesAsync(
        ICollection<StateWriteFailure> failures,
        CancellationToken cancellationToken
    )
    {
        Dictionary<string, ToolDefinition> tools = registry.Load();
        List<ToolStatus> statuses = [];
        foreach ((string id, ToolDefinition tool) in tools.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
        {
            string? current = await GetCurrentAsync(tool, cancellationToken).ConfigureAwait(false);
            string? latest = await GetLatestAsync(id, tool, force: false, failures, cancellationToken)
                .ConfigureAwait(false);
            statuses.Add(new ToolStatus(id, tool, current, latest));
        }

        return statuses;
    }

    /// <summary>Applies an optional single-group regex to a probe result.</summary>
    /// <param name="value">Raw probe output.</param>
    /// <param name="pattern">Regex with one capture group, or <c>null</c> to use the value as-is.</param>
    /// <returns>The extracted version, or <c>null</c> when empty or the regex misses.</returns>
    public static string? ExtractVersion(string value, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(pattern))
        {
            return value;
        }

        try
        {
            Match match = Regex.Match(value, pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return match.Success && match.Groups.Count >= 2 ? match.Groups[1].Value : null;
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        {
            return null;
        }
    }
}
