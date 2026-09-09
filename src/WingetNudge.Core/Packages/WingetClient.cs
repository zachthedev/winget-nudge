using Microsoft.Management.Deployment;
using Windows.Foundation;

namespace WingetNudge.Core.Packages;

/// <summary>Winget access through its COM API.</summary>
/// <remarks>
/// Objects come from <see cref="WingetActivation"/>; the server runs at the caller's integrity
/// level, so an elevated process gets an elevated server. The host must initialize reg-free
/// WinRT metadata resolution first, or every cross-process interface query fails.
/// </remarks>
public sealed class WingetClient : IPackageSource, IPackageUpgrader
{
    /// <summary>Name of the community source.</summary>
    public const string SourceName = "winget";

    private PackageManager Manager
    {
        get => field ??= WingetActivation.CreatePackageManager();
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(
        CancellationToken cancellationToken
    ) =>
        Task.Run(
            () =>
            {
                PackageCatalog catalog = ConnectComposite();
                FindPackagesResult found = catalog.FindPackages(
                    WingetActivation.CreateFindPackagesOptions()
                );
                if (found.Status != FindPackagesResultStatus.Ok)
                {
                    throw new InvalidOperationException($"winget search failed: {found.Status}");
                }

                // Indexing goes through IVectorView, which marshals across the server boundary;
                // enumerating asks for IIterable, which the proxy refuses.
                IReadOnlyList<MatchResult> matches = found.Matches;
                List<PackageInfo> packages = [];
                for (int index = 0; index < matches.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    CatalogPackage package = matches[index].CatalogPackage;
                    PackageVersionInfo? remote = package.DefaultInstallVersion;
                    if (remote is null || remote.PackageCatalog.Info.Name != SourceName)
                    {
                        continue;
                    }

                    string? available =
                        package.AvailableVersions.Count > 0
                            ? package.AvailableVersions[0].Version
                            : null;
                    string? installedName = package.InstalledVersion?.DisplayName;
                    packages.Add(
                        new PackageInfo(
                            package.Id,
                            package.Name,
                            package.InstalledVersion?.Version,
                            available,
                            package.IsUpdateAvailable
                        )
                        {
                            InstalledName = string.Equals(
                                installedName,
                                package.Name,
                                StringComparison.OrdinalIgnoreCase
                            )
                                ? null
                                : installedName,
                            OfferedVersion = remote.Version,
                        }
                    );
                }

                return (IReadOnlyList<PackageInfo>)packages;
            },
            cancellationToken
        );

    /// <inheritdoc/>
    public async Task<UpgradeOutcome> UpgradeAsync(
        string packageId,
        UpgradeMode mode,
        IProgress<UpgradeProgress>? progress,
        CancellationToken cancellationToken
    )
    {
        PackageIdValidator.Ensure(packageId);
        CatalogPackage? package = await Task.Run(() => FindById(packageId), cancellationToken)
            .ConfigureAwait(false);
        if (package is null)
        {
            return UpgradeOutcome.Failed("package not found");
        }

        InstallOptions options = WingetActivation.CreateInstallOptions();
        options.AcceptPackageAgreements = true;
        options.PackageInstallScope = PackageInstallScope.Any;
        options.PackageInstallMode =
            mode == UpgradeMode.Interactive
                ? PackageInstallMode.Interactive
                : PackageInstallMode.Silent;
        options.Force = mode == UpgradeMode.Force;

        IAsyncOperationWithProgress<InstallResult, InstallProgress> operation =
            Manager.UpgradePackageAsync(package, options);
        if (progress is not null)
        {
            operation.Progress = (_, snapshot) => progress.Report(Map(snapshot));
        }

        InstallResult result = await operation.AsTask(cancellationToken).ConfigureAwait(false);
        Exception? extended = result.ExtendedErrorCode;
        return new UpgradeOutcome(
            result.Status == InstallResultStatus.Ok,
            result.Status.ToString(),
            result.InstallerErrorCode,
            extended?.HResult,
            result.RebootRequired,
            result.CorrelationData
        );
    }

    private static UpgradeProgress Map(InstallProgress snapshot) =>
        snapshot.State switch
        {
            PackageInstallProgressState.Queued => new UpgradeProgress(UpgradePhase.Queued, null),
            PackageInstallProgressState.Downloading => new UpgradeProgress(
                UpgradePhase.Downloading,
                snapshot.BytesRequired > 0 ? snapshot.DownloadProgress : null
            ),
            PackageInstallProgressState.Installing => new UpgradeProgress(
                UpgradePhase.Installing,
                snapshot.InstallationProgress
            ),
            PackageInstallProgressState.PostInstall => new UpgradeProgress(
                UpgradePhase.Finishing,
                null
            ),
            PackageInstallProgressState.Finished => new UpgradeProgress(UpgradePhase.Finishing, 1),
            _ => new UpgradeProgress(UpgradePhase.Queued, null),
        };

    private CatalogPackage? FindById(string packageId)
    {
        PackageCatalog catalog = ConnectComposite();
        FindPackagesOptions options = WingetActivation.CreateFindPackagesOptions();
        PackageMatchFilter filter = WingetActivation.CreatePackageMatchFilter();
        filter.Field = PackageMatchField.Id;
        filter.Option = PackageFieldMatchOption.EqualsCaseInsensitive;
        filter.Value = packageId;
        options.Filters.Add(filter);
        FindPackagesResult found = catalog.FindPackages(options);
        return found.Status == FindPackagesResultStatus.Ok && found.Matches.Count > 0
            ? found.Matches[0].CatalogPackage
            : null;
    }

    /// <summary>Dumps what the COM API reports for one package, for diagnosing a mismatch.</summary>
    /// <param name="packageId">Winget package id.</param>
    /// <returns>One line per fact.</returns>
    public Task<IReadOnlyList<string>> ExplainAsync(string packageId) =>
        Task.Run<IReadOnlyList<string>>(() =>
        {
            PackageCatalog catalog = ConnectComposite();
            FindPackagesResult found = catalog.FindPackages(
                WingetActivation.CreateFindPackagesOptions()
            );
            IReadOnlyList<MatchResult> matches = found.Matches;
            for (int index = 0; index < matches.Count; index++)
            {
                CatalogPackage package = matches[index].CatalogPackage;
                if (!string.Equals(package.Id, packageId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                List<string> lines =
                [
                    $"Id: {package.Id}",
                    $"Name: {package.Name}",
                    $"InstalledVersion: {package.InstalledVersion?.Version ?? "<null>"}",
                    $"InstalledChannel: {package.InstalledVersion?.Channel ?? "<null>"}",
                    $"IsUpdateAvailable: {package.IsUpdateAvailable}",
                    $"DefaultInstallVersion: {package.DefaultInstallVersion?.Version ?? "<null>"}",
                    $"DefaultInstallCatalog: {package.DefaultInstallVersion?.PackageCatalog.Info.Name ?? "<null>"}",
                    $"AvailableVersions: {package.AvailableVersions.Count}",
                ];
                for (int v = 0; v < Math.Min(package.AvailableVersions.Count, 5); v++)
                {
                    PackageVersionId id = package.AvailableVersions[v];
                    lines.Add($"  [{v}] {id.Version} channel='{id.Channel}'");
                }

                return lines;
            }

            return [$"{packageId}: not found in the composite catalog"];
        });

    private PackageCatalog ConnectComposite()
    {
        CreateCompositePackageCatalogOptions options = WingetActivation.CreateCompositeOptions();
        options.CompositeSearchBehavior = CompositeSearchBehavior.LocalCatalogs;
        options.Catalogs.Add(
            Manager.GetPredefinedPackageCatalog(PredefinedPackageCatalog.OpenWindowsCatalog)
        );
        PackageCatalogReference reference = Manager.CreateCompositePackageCatalog(options);
        ConnectResult connection = reference.Connect();
        if (connection.Status != ConnectResultStatus.Ok)
        {
            throw new InvalidOperationException(
                $"winget catalog connect failed: {connection.Status}"
            );
        }

        return connection.PackageCatalog;
    }
}
