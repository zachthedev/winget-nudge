using System.Net.Http.Headers;
using WingetNudge.Core.Changelog;
using WingetNudge.Core.Notifications;
using WingetNudge.Core.Packages;
using WingetNudge.Core.Preferences;
using WingetNudge.Core.Registration;
using WingetNudge.Core.Storage;
using WingetNudge.Core.Tools;
using WingetNudge.Core.Tracking;
using WingetNudge.Core.Upgrade;

namespace WingetNudge.Services;

/// <summary>Composition root: one instance of every service the app needs, built lazily.</summary>
public sealed class AppServices : IDisposable
{
    /// <summary>Name shown in the Start Menu, notifications and window titles.</summary>
    public const string DisplayName = "Winget Nudge";

    /// <summary>The process-wide instance.</summary>
    public static AppServices Current { get; } = new();

    /// <summary>
    /// How stale a half-written state file must be before startup deletes it. A write that
    /// finishes takes milliseconds, so anything past this was abandoned by a killed process.
    /// </summary>
    private static readonly TimeSpan AbandonedWriteAge = TimeSpan.FromHours(1);

    private AppServices()
    {
#if DEBUG
        IsDemo = DemoInventory.IsRequested;
        if (IsDemo)
        {
            Paths = DemoInventory.CreatePaths();
            try
            {
                DemoInventory.Seed(this);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // This constructor runs inside the type initializer, before the app can write a
                // crash log, so a throw here would end the process with nothing to read.
            }

            return;
        }
#endif
        Paths = DataPaths.Default;
        try
        {
            LegacyDataMigrator.MigrateIfNeeded(Paths);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The app starts empty rather than not at all; the legacy folder stays for a retry.
        }

        // Housekeeping runs as the user only. The elevated upgrade window has no reason to
        // delete anything, and a profile directory is the user's to redirect.
        if (!ProcessIdentity.IsElevated)
        {
            JsonFile.SweepTemporaries(Paths.Directory, AbandonedWriteAge);
        }
    }

    /// <summary>Data file locations.</summary>
    public DataPaths Paths { get; }

    /// <summary>
    /// Whether this process runs on the fixed demo inventory the README screenshots come from.
    /// Only a Debug build ever sets it, so a release build never starts in it.
    /// </summary>
    public bool IsDemo { get; }

    /// <summary>Time source.</summary>
    public TimeProvider Clock { get; } = TimeProvider.System;

    /// <summary>Full path of the running executable.</summary>
    public string ExecutablePath { get; } =
        Environment.ProcessPath ?? throw new InvalidOperationException("Process path unavailable.");

    /// <summary>Directory the executable runs from.</summary>
    public string AppDirectory => Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory;

    /// <summary>Notification logo.</summary>
    public string IconPngPath => Path.Combine(AppDirectory, "Assets", "icon.png");

    /// <summary>Window and shortcut icon.</summary>
    public string IconIcoPath => Path.Combine(AppDirectory, "Assets", "icon.ico");

    /// <summary>HTTP client with the app's user agent, which GitHub requires.</summary>
    public HttpClient Http
    {
        get
        {
            lock (_gate)
            {
#if DEBUG
                if (_http is null && IsDemo)
                {
                    _http = DemoInventory.CreateOfflineClient();
                }
#endif
                if (_http is null)
                {
                    ProductInfoHeaderValue userAgent = AppUserAgent.For(typeof(AppServices).Assembly);

                    // The token rides only on api.github.com requests; tool URLs share the client.
                    HttpClient http = new(
                        new GitHubAuthorizationHandler(Settings.ResolveGitHubToken())
                        {
                            InnerHandler = new SocketsHttpHandler(),
                        }
                    )
                    {
                        Timeout = TimeSpan.FromSeconds(10),
                    };
                    http.DefaultRequestHeaders.UserAgent.Add(userAgent);

                    // Cached only once the user agent is on it, so a throw above leaves nothing cached and the
                    // next access fails the same way.
                    _http = http;
                }

                return _http;
            }
        }
    }

    /// <summary>Winget COM client.</summary>
    public WingetClient Winget
    {
        get => Once(ref field, static _ => new WingetClient());
    }

    private Settings? _settings;
    private PreferenceStore? _preferences;
    private UpdateLog? _updateLog;
    private ToolProber? _toolProber;
    private UpdateCheck? _updateCheck;
    private HttpClient? _http;
    private GitHubReleaseDateResolver? _releaseDates;
    private ChangelogFetcher? _changelogs;
    private ChangelogCache? _changelogCache;

    // The picker builds the services on a pool thread while the UI thread can reach them, so every lazy member
    // builds, and every reset clears, under this lock. It is reentrant, since one member builds from another.
    private readonly Lock _gate = new();

    /// <summary>User-tunable settings.</summary>
    public Settings Settings =>
        Once(ref _settings, static services => WingetNudge.Core.Storage.Settings.Load(services.Paths));

    /// <summary>The settings a use already loaded, or <c>null</c> when none has. Never reads settings.json.</summary>
    public Settings? LoadedSettings
    {
        get
        {
            lock (_gate)
            {
                return _settings;
            }
        }
    }

    /// <summary>
    /// Drops every service that reads a setting, so the next use rebuilds against what was just
    /// saved.
    /// </summary>
    public void ReloadSettings()
    {
        lock (_gate)
        {
            _settings = null;
            _preferences = null;
            _updateLog = null;
            _toolProber = null;
            _updateCheck = null;

            // The GitHub token is captured in the handler at construction, so a saved token only
            // reaches a request once the client and everything built on it are rebuilt.
            _http?.Dispose();
            _http = null;
            _releaseDates = null;
            _changelogs = null;
        }
    }

    /// <summary>Muted and failed package state.</summary>
    public PreferenceStore Preferences =>
        Once(
            ref _preferences,
            static services => new PreferenceStore(services.Paths, services.Clock, services.Settings.FailedExpiryDays)
        );

    /// <summary>First-seen tracking and cooldown.</summary>
    public VersionTracker Tracker
    {
        get => Once(ref field, static services => new VersionTracker(services.Paths, services.Clock));
    }

    /// <summary>Manual tool registry.</summary>
    public ToolRegistry Tools
    {
        get => Once(ref field, static services => new ToolRegistry(services.Paths));
    }

    /// <summary>Manual tool probes.</summary>
    public ToolProber ToolProber => Once(ref _toolProber, static services => services.CreateToolProber());

    /// <summary>Publish date lookup against winget-pkgs.</summary>
    public GitHubReleaseDateResolver ReleaseDates =>
        Once(ref _releaseDates, static services => new GitHubReleaseDateResolver(services.Http));

    /// <summary>The shared inventory query.</summary>
    public UpdateCheck UpdateCheck => Once(ref _updateCheck, static services => services.CreateUpdateCheck());

    /// <summary>Scheduled task and Start Menu shortcut.</summary>
    public StartupRegistrar Registrar
    {
        get => Once(ref field, static services => new StartupRegistrar(services.ExecutablePath, DisplayName));
    }

    /// <summary>Outcome log.</summary>
    public UpdateLog UpdateLog =>
        Once(
            ref _updateLog,
            static services => new UpdateLog(
                services.Paths,
                services.Clock,
                services.Settings.LogRetentionDays,
                services.Settings.InstallerLogsPerPackage
            )
        );

    /// <summary>Release notes already fetched, keyed by package and version.</summary>
    public ChangelogCache ChangelogCache =>
        Once(ref _changelogCache, static services => new ChangelogCache(services.Paths, services.Clock));

    /// <summary>Release notes.</summary>
    public ChangelogFetcher Changelogs =>
        Once(ref _changelogs, static services => new ChangelogFetcher(services.Http, services.ChangelogCache));

    /// <summary>What the last notification announced.</summary>
    public NotificationState Notifications
    {
        get => Once(ref field, static services => new NotificationState(services.Paths, services.Clock));
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            _http?.Dispose();
            _http = null;
        }
    }

    // A build that throws caches nothing, so the next use builds again.
    private T Once<T>(ref T? slot, Func<AppServices, T> build)
        where T : class
    {
        lock (_gate)
        {
            return slot ??= build(this);
        }
    }

    private ToolProber CreateToolProber()
    {
#if DEBUG
        if (IsDemo)
        {
            return DemoInventory.CreateToolProber(this);
        }
#endif
        return new ToolProber(Tools, new ProcessRunner(), Http, Clock, Settings.ToolCacheHours);
    }

    private UpdateCheck CreateUpdateCheck()
    {
#if DEBUG
        if (IsDemo)
        {
            return DemoInventory.CreateUpdateCheck(this);
        }
#endif
        return new UpdateCheck(Winget, Preferences, Tracker, ReleaseDates, ToolProber, Clock);
    }

    /// <summary>Builds an upgrade engine bound to a host.</summary>
    /// <param name="ui">Host callbacks.</param>
    /// <returns>A fresh engine.</returns>
    public UpgradeEngine CreateUpgradeEngine(IUpgradeInteraction ui)
    {
        // The engine drives winget against whatever it is handed, and the demo inventory names
        // real packages. Program refuses the upgrade verb before this, and this refuses the rest.
        if (IsDemo)
        {
            throw new InvalidOperationException("The demo inventory upgrades nothing.");
        }

        return new UpgradeEngine(Winget, Winget, Preferences, UpdateLog, Changelogs, new BlockingProcessDetector(), ui);
    }
}
