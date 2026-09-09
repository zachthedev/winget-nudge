using System.Globalization;
using System.Text;

namespace WingetNudge.Core.Storage;

/// <summary>How often the scheduled check runs on its own.</summary>
public enum CheckFrequency
{
    /// <summary>No recurring check; only the events the user picked.</summary>
    Never,

    /// <summary>Every day at the chosen time.</summary>
    Daily,

    /// <summary>Once a week, on the chosen day and time.</summary>
    Weekly,
}

/// <summary>User-tunable settings with defaults for every missing key.</summary>
public sealed record Settings
{
    /// <summary>Default hours a version waits after publication before it is offered.</summary>
    public const int DefaultCooldownHours = 24;

    /// <summary>Largest cooldown the settings accept.</summary>
    public const int MaxCooldownHours = 24 * 30;

    /// <summary>Largest sign-in delay the settings accept, in minutes.</summary>
    public const int MaxLogonDelayMinutes = 120;

    /// <summary>Largest failed-package expiry the settings accept, in days.</summary>
    public const int MaxFailedExpiryDays = 365;

    /// <summary>Largest tool re-check interval the settings accept, in hours.</summary>
    public const int MaxToolCacheHours = 24 * 30;

    /// <summary>Largest background re-check interval the settings accept, in hours.</summary>
    public const int MaxBackgroundCheckHours = 24;

    /// <summary>Largest update-log retention the settings accept, in days.</summary>
    public const int MaxLogRetentionDays = 365;

    /// <summary>Largest number of installer logs kept per package.</summary>
    public const int MaxInstallerLogsPerPackage = 100;

    // ///// When to check /////

    /// <summary>How often the recurring check runs.</summary>
    public CheckFrequency Frequency { get; init; } = CheckFrequency.Weekly;

    /// <summary>Weekday of the recurring check, used only when <see cref="Frequency"/> is weekly.</summary>
    public DayOfWeek CheckDay { get; init; } = DayOfWeek.Monday;

    /// <summary>Hour of the recurring check, 0 to 23.</summary>
    public int CheckHour { get; init; } = 9;

    /// <summary>Minute of the recurring check, 0 to 59.</summary>
    public int CheckMinute { get; init; }

    /// <summary>Whether a check also runs shortly after logon.</summary>
    public bool CheckAtLogon { get; init; } = true;

    /// <summary>Minutes to wait after logon before checking, so it stays out of the way.</summary>
    public int LogonDelayMinutes { get; init; } = 2;

    /// <summary>Whether a check also runs when the user unlocks the machine.</summary>
    public bool CheckAtUnlock { get; init; }

    /// <summary>
    /// Hours between quiet background checks. These refresh publish dates and the cooldown
    /// verdict so a version that matures between announcements is offered as soon as the picker
    /// opens. Zero turns them off.
    /// </summary>
    public int BackgroundCheckHours { get; init; } = 4;

    /// <summary>
    /// Whether a background check may raise a notification of its own. Off, the announcement
    /// stays with the recurring check and the sign-in and unlock triggers.
    /// </summary>
    public bool NotifyOnNewUpdates { get; init; }

    // ///// What to offer /////

    /// <summary>
    /// Hours after publication below which a package version counts as "too new" and is left
    /// out of notifications and Update All. Zero disables the gate.
    /// </summary>
    public int CooldownHours { get; init; } = DefaultCooldownHours;

    /// <summary>Days a failed package stays marked before it is offered again.</summary>
    public int FailedExpiryDays { get; init; } = 7;

    // ///// Manual tools /////

    /// <summary>Hours a manual tool's latest-version probe is cached.</summary>
    public int ToolCacheHours { get; init; } = 24;

    /// <summary>
    /// GitHub token for release-notes and publish-date lookups, encrypted to the current user
    /// so the settings file never carries it in the clear. Empty falls back to the
    /// <c>GITHUB_TOKEN</c> environment variable.
    /// </summary>
    /// <remarks>Set through <see cref="WithGitHubToken"/>; read through <see cref="ResolveGitHubToken"/>.</remarks>
    public string ProtectedGitHubToken { get; init; } = "";

    // ///// History /////

    /// <summary>Days an entry stays in the update log.</summary>
    public int LogRetentionDays { get; init; } = 30;

    /// <summary>Installer dumps kept per package.</summary>
    public int InstallerLogsPerPackage { get; init; } = 10;

    /// <summary>
    /// Loads settings from disk. A corrupt file falls back to defaults and is left in place,
    /// since it holds user choices worth repairing by hand.
    /// </summary>
    /// <param name="paths">Data file locations.</param>
    /// <returns>Settings with defaults filled in and out-of-range values clamped.</returns>
    public static Settings Load(DataPaths paths) =>
        (
            JsonFile.Read<Settings>(paths.Settings, deleteIfCorrupt: false) ?? new Settings()
        ).Clamped();

    /// <summary>Writes settings to disk.</summary>
    /// <param name="paths">Data file locations.</param>
    public void Save(DataPaths paths) => JsonFile.Write(paths.Settings, Clamped());

    /// <summary>Stores a token, encrypted to the current user.</summary>
    /// <param name="token">Plain token, or empty to clear it.</param>
    /// <returns>A copy carrying the encrypted token.</returns>
    public Settings WithGitHubToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return this with { ProtectedGitHubToken = "" };
        }

        return this with
        {
            ProtectedGitHubToken = UserSecret.Protect(token.Trim()),
        };
    }

    /// <summary>Whether a token is stored, without decrypting it.</summary>
    public bool HasGitHubToken => ProtectedGitHubToken.Length > 0;

    /// <summary>The token to send to GitHub, preferring the setting over the environment.</summary>
    /// <returns>A token, or <c>null</c> when neither source has one.</returns>
    public string? ResolveGitHubToken()
    {
        if (ProtectedGitHubToken.Length > 0)
        {
            // A blob from another user, or a corrupt one, is not a token; fall through.
            if (
                UserSecret.Unprotect(ProtectedGitHubToken) is string token
                && !string.IsNullOrWhiteSpace(token)
            )
            {
                return token;
            }
        }

        string? environment = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        return string.IsNullOrWhiteSpace(environment) ? null : environment;
    }

    /// <summary>
    /// Keeps the encrypted token out of the record's generated text, which otherwise prints
    /// every member.
    /// </summary>
    /// <param name="builder">Receives the printed members.</param>
    /// <returns>Whether anything was printed.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(
            CultureInfo.InvariantCulture,
            $"Frequency = {Frequency}, CheckDay = {CheckDay}, CheckHour = {CheckHour}, "
        );
        builder.Append(
            CultureInfo.InvariantCulture,
            $"CheckMinute = {CheckMinute}, CheckAtLogon = {CheckAtLogon}, "
        );
        builder.Append(
            CultureInfo.InvariantCulture,
            $"CheckAtUnlock = {CheckAtUnlock}, LogonDelayMinutes = {LogonDelayMinutes}, "
        );
        builder.Append(
            CultureInfo.InvariantCulture,
            $"BackgroundCheckHours = {BackgroundCheckHours}, NotifyOnNewUpdates = {NotifyOnNewUpdates}, "
        );
        builder.Append(
            CultureInfo.InvariantCulture,
            $"CooldownHours = {CooldownHours}, FailedExpiryDays = {FailedExpiryDays}, "
        );
        builder.Append(
            CultureInfo.InvariantCulture,
            $"ToolCacheHours = {ToolCacheHours}, GitHubToken = {(HasGitHubToken ? "<set>" : "<unset>")}, "
        );
        builder.Append(
            CultureInfo.InvariantCulture,
            $"LogRetentionDays = {LogRetentionDays}, InstallerLogsPerPackage = {InstallerLogsPerPackage}"
        );
        return true;
    }

    /// <summary>Returns a copy with every value inside its valid range.</summary>
    /// <returns>Clamped settings.</returns>
    public Settings Clamped() =>
        this with
        {
            Frequency = Enum.IsDefined(Frequency) ? Frequency : CheckFrequency.Weekly,
            CooldownHours = Math.Clamp(CooldownHours, 0, MaxCooldownHours),
            CheckHour = Math.Clamp(CheckHour, 0, 23),
            CheckMinute = Math.Clamp(CheckMinute, 0, 59),
            CheckDay = Enum.IsDefined(CheckDay) ? CheckDay : DayOfWeek.Monday,
            LogonDelayMinutes = Math.Clamp(LogonDelayMinutes, 0, MaxLogonDelayMinutes),
            BackgroundCheckHours = Math.Clamp(BackgroundCheckHours, 0, MaxBackgroundCheckHours),
            FailedExpiryDays = Math.Clamp(FailedExpiryDays, 1, MaxFailedExpiryDays),
            ToolCacheHours = Math.Clamp(ToolCacheHours, 1, MaxToolCacheHours),
            LogRetentionDays = Math.Clamp(LogRetentionDays, 1, MaxLogRetentionDays),
            InstallerLogsPerPackage = Math.Clamp(
                InstallerLogsPerPackage,
                1,
                MaxInstallerLogsPerPackage
            ),
        };
}
