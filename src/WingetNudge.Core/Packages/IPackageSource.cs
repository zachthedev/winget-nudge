namespace WingetNudge.Core.Packages;

/// <summary>Lists packages the winget source manages on the computer the app runs on.</summary>
public interface IPackageSource
{
    /// <summary>
    /// Returns every installed package that the winget source knows, whether or not an upgrade
    /// is available.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Installed packages from the winget source.</returns>
    Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(CancellationToken cancellationToken);
}
