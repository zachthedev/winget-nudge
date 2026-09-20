using WingetNudge.Core.Packages;

namespace WingetNudge.Core.Tools;

/// <summary>A tool installed outside winget that the app probes for updates.</summary>
public sealed record ToolDefinition
{
    /// <summary>Display name shown in the picker.</summary>
    public required string Name { get; init; }

    /// <summary>Executable and arguments that print the installed version.</summary>
    public required IReadOnlyList<string> CurrentCommand { get; init; }

    /// <summary>
    /// Optional .NET regex with one capture group applied to the command's stdout. When absent
    /// the trimmed output is the version.
    /// </summary>
    public string? CurrentRegex { get; init; }

    /// <summary>URL returning JSON that names the latest version.</summary>
    public required string LatestUrl { get; init; }

    /// <summary>Top-level field in the <see cref="LatestUrl"/> response holding the version.</summary>
    public required string LatestJsonField { get; init; }

    /// <summary>
    /// Optional .NET regex with one capture group applied to the field value, for stripping a
    /// prefix such as <c>bun-v</c>.
    /// </summary>
    public string? LatestRegex { get; init; }

    /// <summary>Shell command that upgrades the tool.</summary>
    public required string UpgradeCommand { get; init; }
}

/// <summary>Cached result of one latest-version probe.</summary>
/// <param name="Latest">Version the probe returned.</param>
/// <param name="CheckedAt">When the probe ran.</param>
public sealed record ToolCacheEntry(string Latest, DateTimeOffset CheckedAt);

/// <summary>Probe result for one registered tool.</summary>
/// <param name="Id">Registry key.</param>
/// <param name="Definition">The tool's registered definition.</param>
/// <param name="Current">Installed version, or <c>null</c> when the probe failed.</param>
/// <param name="Latest">Latest version, or <c>null</c> when the probe failed.</param>
public sealed record ToolStatus(string Id, ToolDefinition Definition, string? Current, string? Latest)
{
    /// <summary>Display name.</summary>
    public string Name => Definition.Name;

    /// <summary>
    /// Whether both probes succeeded and the published version is genuinely newer. A tool that
    /// prints its version with a prefix, or one running ahead of its published release, is not
    /// an update.
    /// </summary>
    public bool UpdateAvailable =>
        !string.IsNullOrWhiteSpace(Current)
        && !string.IsNullOrWhiteSpace(Latest)
        && WingetVersion.IsUpgrade(Current, Latest);
}
