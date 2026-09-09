#if DEBUG
using System.Net;
using WingetNudge.Core.Changelog;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Preferences;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Tracking;

namespace WingetNudge.Services;

/// <summary>
/// A fixed inventory of well-known packages and tools, which the README screenshots come from. It
/// stands in for winget, the release-date lookup, the tool probes and the network, in a data
/// directory of its own under the temp folder. Nothing it lists is installed, and nothing saved
/// while it runs reaches the real state. Only a Debug build compiles it, and only
/// <see cref="EnvironmentVariable"/> set to <c>1</c> turns it on.
/// </summary>
internal static class DemoInventory
{
    /// <summary>The environment variable that starts a Debug build on this inventory.</summary>
    public const string EnvironmentVariable = "WINGETNUDGE_DEMO";

    /// <summary>Whether this process was asked to run on the demo inventory.</summary>
    public static bool IsRequested =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    private sealed record DemoPackage(
        string Id,
        string Name,
        string Installed,
        string? Available,
        TimeSpan Age,
        string? Notes = null
    )
    {
        public PackageInfo Info =>
            new(Id, Name, Installed, Available, IsUpdateAvailable: Available is not null);
    }

    // Ages are measured back from the clock at startup. The default cooldown is 24 hours, so
    // Python lands in Too new and the rest are ready.
    private static readonly DemoPackage[] Packages =
    [
        new(
            "Git.Git",
            "Git",
            "2.54.0",
            "2.55.0",
            TimeSpan.FromDays(6),
            Notes(
                "Git",
                "`git switch` guesses a remote branch of the same name by default.",
                "`git status` reads large working trees faster."
            )
        ),
        new(
            "Microsoft.VisualStudioCode",
            "Microsoft Visual Studio Code",
            "1.112.0",
            "1.113.1",
            TimeSpan.FromDays(2),
            Notes(
                "Visual Studio Code",
                "Terminal tabs keep their names across restarts.",
                "Settings search matches extension settings by their display names."
            )
        ),
        new(
            "Microsoft.PowerShell",
            "PowerShell",
            "7.6.5.0",
            "7.6.6.0",
            TimeSpan.FromDays(3),
            Notes(
                "PowerShell",
                "`Get-Error` shows the inner exception chain by default.",
                "Tab completion covers native commands' registered completers."
            )
        ),
        new("Python.Python.3.14", "Python 3.14", "3.14.3", "3.14.4", TimeSpan.FromHours(5)),
        new("Discord.Discord", "Discord", "1.0.9212", "1.0.9215", TimeSpan.FromDays(1)),
        new(
            "Microsoft.WindowsTerminal",
            "Windows Terminal",
            "1.24.2682.0",
            "1.25.2181.0",
            TimeSpan.FromDays(5)
        ),
        new("Microsoft.DotNet.SDK.10", "Microsoft .NET SDK 10.0", "10.0.401", null, TimeSpan.Zero),
    ];

    private static readonly Dictionary<string, string> ToolVersions = new(StringComparer.Ordinal)
    {
        ["bun"] = "1.4.1",
        ["uv"] = "uv 0.9.8",
    };

    /// <summary>
    /// Data paths under the temp folder, emptied so every run starts from the same seed and a
    /// screenshot never carries an earlier run's clicks.
    /// </summary>
    /// <returns>The demo data paths.</returns>
    public static DataPaths CreatePaths()
    {
        string directory = Path.Combine(Path.GetTempPath(), "WingetNudge-demo");
        try
        {
            if (Directory.Exists(directory))
            {
                // A link at this path, or above it, would aim the delete somewhere else. The
                // check runs before the delete rather than as part of it, so it does not close
                // a race against another process running as this user.
                SafePath.EnsureNotReparsePoint(directory);
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A second demo instance holds the first one's files. It starts on what is there.
        }

        return new DataPaths(directory);
    }

    /// <summary>
    /// Writes the preferences, tools and release notes the inventory starts from.
    /// </summary>
    /// <param name="services">Services bound to the demo data paths.</param>
    public static void Seed(AppServices services)
    {
        services.Preferences.Set("Discord.Discord", PreferenceState.Muted);
        services.Preferences.SkipVersion("Microsoft.WindowsTerminal", "1.25.2181.0");

        DateTimeOffset now = services.Clock.GetUtcNow();
        services.Tools.Register(
            "bun",
            new ToolDefinition
            {
                Name = "Bun",
                CurrentCommand = ["bun", "--version"],
                LatestUrl = "https://api.github.com/repos/oven-sh/bun/releases/latest",
                LatestJsonField = "tag_name",
                LatestRegex = "^bun-v(.+)$",
                UpgradeCommand = "bun upgrade",
            }
        );
        services.Tools.SaveCache("bun", new ToolCacheEntry("1.4.2", now));
        services.Tools.Register(
            "uv",
            new ToolDefinition
            {
                Name = "uv",
                CurrentCommand = ["uv", "--version"],
                CurrentRegex = @"^uv (\S+)",
                LatestUrl = "https://api.github.com/repos/astral-sh/uv/releases/latest",
                LatestJsonField = "tag_name",
                UpgradeCommand = "uv self update",
            }
        );
        services.Tools.SaveCache("uv", new ToolCacheEntry("0.9.9", now));

        services.ChangelogCache.Store([
            .. Packages
                .Where(static package => package.Notes is not null)
                .Select(static package => (package.Info, package.Notes ?? "")),
        ]);
    }

    /// <summary>Builds the update check over the demo inventory.</summary>
    /// <param name="services">Services bound to the demo data paths.</param>
    /// <returns>An update check that reaches neither winget nor the network.</returns>
    public static UpdateCheck CreateUpdateCheck(AppServices services) =>
        new(
            new PackageSource(),
            services.Preferences,
            services.Tracker,
            new ReleaseDates(services.Clock.GetUtcNow()),
            services.ToolProber,
            services.Clock,
            // No database at this path, so no package reads as pinned.
            new WingetPinReader(Path.Combine(services.Paths.Directory, "pinning.db"))
        );

    /// <summary>Builds the tool prober over the demo tools.</summary>
    /// <param name="services">Services bound to the demo data paths.</param>
    /// <returns>A prober whose version commands never start a process.</returns>
    public static ToolProber CreateToolProber(AppServices services) =>
        new(
            services.Tools,
            new ProcessRunner(),
            services.Http,
            services.Clock,
            services.Settings.ToolCacheHours
        );

    /// <summary>
    /// Replaces this machine's accent with the default Windows blue in the app's own resources,
    /// so a screenshot carries neither the accent this machine happens to use nor a change to it.
    /// </summary>
    /// <param name="resources">The application's resources, before any window loads.</param>
    public static void UseDefaultAccent(Microsoft.UI.Xaml.ResourceDictionary resources)
    {
        // WinUI's own palette for the default blue, from SystemThemingInterop.cpp in
        // microsoft/microsoft-ui-xaml, which it uses wherever the system accent is overridden.
        // A merged dictionary added last takes precedence over the ones before it.
        Microsoft.UI.Xaml.ResourceDictionary accent = new()
        {
            ["SystemAccentColor"] = Argb(0xFF0078D7),
            ["SystemAccentColorDark1"] = Argb(0xFF005A9E),
            ["SystemAccentColorDark2"] = Argb(0xFF004275),
            ["SystemAccentColorDark3"] = Argb(0xFF002642),
            ["SystemAccentColorLight1"] = Argb(0xFF429CE3),
            ["SystemAccentColorLight2"] = Argb(0xFF76B9ED),
            ["SystemAccentColorLight3"] = Argb(0xFFA6D8FF),
        };
        resources.MergedDictionaries.Add(accent);
    }

    private static Windows.UI.Color Argb(uint value) =>
        Windows.UI.Color.FromArgb(
            (byte)(value >> 24),
            (byte)(value >> 16),
            (byte)(value >> 8),
            (byte)value
        );

    /// <summary>
    /// An HTTP client that answers every request with 404, so a lookup the seed did not cover
    /// fails the way an offline machine does instead of reaching the network.
    /// </summary>
    /// <returns>A client that never sends.</returns>
    public static HttpClient CreateOfflineClient() => new(new OfflineHandler());

    // No line here may start with '#': a Release build skips this file, and the compiler reads a
    // skipped line that starts with one as a directive.
    private static string Notes(string product, string first, string second) =>
        $"""
            **What's new**

            - {first}
            - {second}

            > [!NOTE]
            > Demo text. The real {product} release notes show here.
            """;

    private sealed class PackageSource : IPackageSource
    {
        public Task<IReadOnlyList<PackageInfo>> GetInstalledAsync(
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<PackageInfo>>([.. Packages.Select(static p => p.Info)]);
    }

    private sealed class ReleaseDates(DateTimeOffset now) : IReleaseDateResolver
    {
        public Task<ResolvedDate?> ResolveAsync(
            string packageId,
            string version,
            CancellationToken cancellationToken
        )
        {
            DemoPackage? package = Packages.FirstOrDefault(package =>
                package.Id == packageId && package.Available == version
            );
            return Task.FromResult(
                package is null
                    ? null
                    : new ResolvedDate(now - package.Age, PublishSource.WingetPkgs)
            );
        }
    }

    private sealed class ProcessRunner : IProcessRunner
    {
        public Task<ProcessOutput> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                ToolVersions.TryGetValue(executable, out string? output)
                    ? new ProcessOutput(0, output)
                    : new ProcessOutput(1, "")
            );
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request }
            );
    }
}
#endif
