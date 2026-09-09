using Microsoft.Data.Sqlite;

namespace WingetNudge.Core.Packages;

/// <summary>How winget restricts a package's upgrades. Values match winget's own <c>PinType</c>.</summary>
public enum PinType
{
    /// <summary>No pin.</summary>
    None = 0,

    /// <summary>The manifest's <c>RequiresExplicitUpgrade</c> field, which behaves like a pin.</summary>
    PinnedByManifest = 1,

    /// <summary>Excluded from bulk upgrades; upgrading the package by name still works.</summary>
    Pinning = 2,

    /// <summary>Held to a version range.</summary>
    Gating = 3,

    /// <summary>Blocked from every upgrade until the user removes the pin.</summary>
    Blocking = 4,
}

/// <summary>One winget pin.</summary>
/// <param name="PackageId">Winget package id.</param>
/// <param name="Type">What the pin restricts.</param>
/// <param name="Version">Version range for a gating pin, else empty.</param>
public sealed record WingetPin(string PackageId, PinType Type, string Version)
{
    /// <summary>Whether the pin refuses an upgrade outright.</summary>
    public bool Blocks => Type == PinType.Blocking;

    /// <summary>Plain description for a status line.</summary>
    public string Describe() =>
        Type switch
        {
            PinType.Blocking => "blocked by a winget pin",
            PinType.Gating => $"held by a winget pin to {Version}",
            PinType.Pinning => "pinned in winget, so bulk upgrades skip it",
            PinType.PinnedByManifest => "the package asks to be upgraded explicitly",
            _ => "not pinned",
        };
}

/// <summary>
/// Reads winget's pin database. The COM API exposes no pin type, but it does honor pins, so a
/// held-back package is indistinguishable from one with no applicable installer without this.
/// </summary>
/// <param name="databasePath">Database to read, or <c>null</c> for App Installer's own.</param>
public sealed class WingetPinReader(string? databasePath = null)
{
    /// <summary>Winget's pin database.</summary>
    /// <returns>Full path, whether or not it exists.</returns>
    public static string DefaultPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages",
            "Microsoft.DesktopAppInstaller_8wekyb3d8bbwe",
            "LocalState",
            "pinning.db"
        );

    /// <summary>
    /// Reads every pin. A package pinned in more than one source keeps the strictest pin, which
    /// is what winget enforces.
    /// </summary>
    /// <returns>Pins by package id, empty when the database is absent or unreadable.</returns>
    public IReadOnlyDictionary<string, WingetPin> Load()
    {
        string path = databasePath ?? DefaultPath();
        Dictionary<string, WingetPin> pins = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
        {
            return pins;
        }

        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            // Winget owns this file; a pooled handle would outlive the read and hold it open.
            Pooling = false,
        };

        try
        {
            using SqliteConnection connection = new(builder.ConnectionString);
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT package_id, type, version FROM pin";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(0))
                {
                    continue;
                }

                string id = reader.GetString(0);
                PinType type = reader.IsDBNull(1) ? PinType.None : ToPinType(reader.GetInt64(1));
                WingetPin pin = new(id, type, reader.IsDBNull(2) ? "" : reader.GetString(2));
                if (!pins.TryGetValue(id, out WingetPin? existing) || pin.Type > existing.Type)
                {
                    pins[id] = pin;
                }
            }
        }
        catch (Exception exception)
            when (exception
                    is SqliteException
                        or InvalidOperationException
                        or InvalidCastException
                        or IOException
                        or UnauthorizedAccessException
            )
        {
            // Winget owns the file; an unreadable moment leaves every package unpinned.
            return new Dictionary<string, WingetPin>(StringComparer.OrdinalIgnoreCase);
        }

        return pins;
    }

    private static PinType ToPinType(long stored) =>
        stored switch
        {
            1 => PinType.PinnedByManifest,
            2 => PinType.Pinning,
            3 => PinType.Gating,
            4 => PinType.Blocking,
            _ => PinType.None,
        };
}
