namespace WingetNudge.Core.Packages;

/// <summary>One winget-managed package as seen in the installed catalog.</summary>
/// <param name="Id">Winget package identifier, for example <c>Git.Git</c>.</param>
/// <param name="Name">Display name.</param>
/// <param name="InstalledVersion">Version on disk, or <c>null</c> when winget cannot read it.</param>
/// <param name="AvailableVersion">Newest version in the source, or <c>null</c> when none is known.</param>
/// <param name="IsUpdateAvailable">Whether winget reports an applicable upgrade.</param>
public sealed record PackageInfo(
    string Id,
    string Name,
    string? InstalledVersion,
    string? AvailableVersion,
    bool IsUpdateAvailable
)
{
    /// <summary>
    /// Display name of the installed entry. It differs from <see cref="Name"/> when winget
    /// correlated the package to the wrong entry in Apps and Features.
    /// </summary>
    public string? InstalledName { get; init; }

    /// <summary>
    /// Version winget would install. It can exceed <see cref="InstalledVersion"/> while
    /// <see cref="IsUpdateAvailable"/> stays <c>false</c>, because winget also weighs whether an
    /// installer matches the scope, type and architecture of what is already installed.
    /// </summary>
    public string? OfferedVersion { get; init; }

    /// <summary>Whether winget names a newer version but will not upgrade to it.</summary>
    public bool IsHeldBack =>
        !IsUpdateAvailable && WingetVersion.IsUpgrade(InstalledVersion, OfferedVersion);
}

/// <summary>Package identity and display name, enough to run and report an upgrade.</summary>
/// <param name="Id">Winget package identifier.</param>
/// <param name="Name">Display name, or empty when unknown.</param>
public sealed record PackageRef(string Id, string Name);
