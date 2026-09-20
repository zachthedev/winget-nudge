namespace WingetNudge.Core.Changelog;

/// <summary>Paths into the microsoft/winget-pkgs repository.</summary>
public static class WingetPkgs
{
    private const string RawRoot = "https://raw.githubusercontent.com/microsoft/winget-pkgs/master/";

    /// <summary>Repository-relative folder holding one version's manifests.</summary>
    /// <param name="packageId">Winget package id, for example <c>Git.Git</c>.</param>
    /// <param name="version">Version string.</param>
    /// <returns><c>manifests/g/Git/Git/2.48.1</c>, or <c>null</c> when the id has no publisher segment or the version is empty.</returns>
    public static string? ManifestFolder(string packageId, string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        int dot = packageId.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0)
        {
            return null;
        }

        string letter = Uri.EscapeDataString(char.ToLowerInvariant(packageId[0]).ToString());
        string publisher = Uri.EscapeDataString(packageId[..dot]);
        string rest = string.Join('/', packageId[(dot + 1)..].Split('.').Select(Uri.EscapeDataString));
        return $"manifests/{letter}/{publisher}/{rest}/{Uri.EscapeDataString(version)}";
    }

    /// <summary>Raw URL of the en-US locale manifest.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="version">Version string.</param>
    /// <returns>The URL, or <c>null</c> when no folder can be derived.</returns>
    public static Uri? LocaleManifestUrl(string packageId, string? version) =>
        ManifestFolder(packageId, version) is string folder
            ? new Uri($"{RawRoot}{folder}/{Uri.EscapeDataString(packageId)}.locale.en-US.yaml")
            : null;

    /// <summary>Raw URL of the installer manifest.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <param name="version">Version string.</param>
    /// <returns>The URL, or <c>null</c> when no folder can be derived.</returns>
    public static Uri? InstallerManifestUrl(string packageId, string? version) =>
        ManifestFolder(packageId, version) is string folder
            ? new Uri($"{RawRoot}{folder}/{Uri.EscapeDataString(packageId)}.installer.yaml")
            : null;
}
