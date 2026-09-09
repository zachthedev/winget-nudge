using WingetNudge.Core.Packages;

namespace WingetNudge.Services;

/// <summary>Which window the XAML application opens first.</summary>
public abstract record StartupMode
{
    private StartupMode() { }

    /// <summary>The package picker.</summary>
    public sealed record Picker : StartupMode;

    /// <summary>The upgrade progress window.</summary>
    /// <param name="Packages">Packages to upgrade.</param>
    public sealed record Upgrade(IReadOnlyList<PackageRef> Packages) : StartupMode;
}
