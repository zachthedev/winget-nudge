using Microsoft.Win32;
using WingetNudge.Core.Packages;

namespace WingetNudge.Core.Upgrade;

/// <summary>
/// Install directories from the Uninstall registry keys, keyed by winget id and by display
/// name, used to find the executables an upgrade will overwrite.
/// </summary>
/// <remarks>
/// The per-user hive is writable by any process running as the user, and the consumer runs
/// elevated and closes whatever holds the files it is given. A directory is therefore trusted
/// only when it is a plain local path inside a known application root and outside the trees
/// Windows and its security components live in, matches are exact, and the number of
/// executables handed on is capped.
/// </remarks>
public sealed class InstallLocationIndex
{
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKeyWow = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Most executables a single package may register with Restart Manager.</summary>
    public const int MaxExecutables = 400;

    /// <summary>Shortest display name the name lookup accepts.</summary>
    public const int MinNameLength = 3;

    private readonly Dictionary<string, string> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Directories keyed by <c>WinGetPackageIdentifier</c>.</summary>
    public IReadOnlyDictionary<string, string> ById => _byId;

    /// <summary>Directories keyed by <c>DisplayName</c>.</summary>
    public IReadOnlyDictionary<string, string> ByName => _byName;

    /// <summary>Reads every Uninstall entry from both machine views and the user hive.</summary>
    /// <returns>The populated index.</returns>
    public static InstallLocationIndex Build()
    {
        InstallLocationIndex index = new();
        index.Scan(Registry.LocalMachine, UninstallKey);
        index.Scan(Registry.LocalMachine, UninstallKeyWow);
        // A per-user install lives under the profile; an HKCU entry pointing anywhere else is
        // not one, and the hive is writable by anything running as the user.
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        index.Scan(Registry.CurrentUser, UninstallKey, directory => IsUnder(directory, localAppData));
        return index;
    }

    /// <summary>Adds an entry, for tests and for hives read elsewhere.</summary>
    /// <param name="wingetId">Value of <c>WinGetPackageIdentifier</c>, or <c>null</c>.</param>
    /// <param name="displayName">Value of <c>DisplayName</c>, or <c>null</c>.</param>
    /// <param name="directory">Install directory.</param>
    public void Add(string? wingetId, string? displayName, string directory)
    {
        if (!string.IsNullOrEmpty(wingetId))
        {
            _byId[wingetId] = directory;
        }

        if (!string.IsNullOrEmpty(displayName))
        {
            _byName[displayName] = directory;
        }
    }

    /// <summary>
    /// Finds a package's install directory: by exact winget id first, then by a display name
    /// equal to the package name or starting with it as a whole word.
    /// </summary>
    /// <param name="package">Package to look up.</param>
    /// <returns>An existing, trusted directory, or <c>null</c>.</returns>
    public string? Resolve(PackageRef package)
    {
        if (!_byId.TryGetValue(package.Id, out string? directory))
        {
            directory = ResolveByName(package.Name);
        }

        return directory is not null && IsTrustedInstallDirectory(directory) && Directory.Exists(directory)
            ? directory
            : null;
    }

    /// <summary>
    /// Whether a directory is a plausible application install location: a fully qualified local
    /// drive path (no UNC, no <c>\\?\</c> device prefix) under Program Files, the user's local
    /// application data or ProgramData, and not inside a tree Windows or Defender owns.
    /// </summary>
    /// <param name="directory">Directory to check.</param>
    /// <returns><c>true</c> only for a path inside an application root.</returns>
    public static bool IsTrustedInstallDirectory(string directory)
    {
        if (
            string.IsNullOrWhiteSpace(directory)
            || directory.StartsWith(@"\\", StringComparison.Ordinal)
            || directory.StartsWith("//", StringComparison.Ordinal)
            || directory.Contains('\0', StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(directory)
        )
        {
            return false;
        }

        string full;
        try
        {
            full = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        }
        catch (Exception exception)
            when (exception is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return false;
        }

        if (full.StartsWith(@"\\", StringComparison.Ordinal) || !IsDriveLetterPath(full))
        {
            return false;
        }

        bool insideRoot = AllowedRoots().Any(root => IsUnder(full, root));
        bool insideProtected = ProtectedRoots().Any(root => IsUnder(full, root));
        return insideRoot && !insideProtected;
    }

    /// <summary>Lists executables under a directory, three levels deep, skipping unreadable folders.</summary>
    /// <param name="directory">Install directory.</param>
    /// <returns>Full paths of <c>*.exe</c> files, or empty when there are more than <see cref="MaxExecutables"/>.</returns>
    public static IReadOnlyList<string> EnumerateExecutables(string directory)
    {
        EnumerationOptions options = new()
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 3,
            IgnoreInaccessible = true,
        };
        try
        {
            List<string> executables = [];
            foreach (string file in Directory.EnumerateFiles(directory, "*.exe", options))
            {
                if (executables.Count == MaxExecutables)
                {
                    return [];
                }

                executables.Add(file);
            }

            return executables;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsDriveLetterPath(string path) =>
        path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';

    private static bool IsUnder(string path, string root)
    {
        string trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar);
        return trimmedRoot.Length > 0
            && (
                path.Equals(trimmedRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            );
    }

    private string? ResolveByName(string name)
    {
        if (name.Length < MinNameLength)
        {
            return null;
        }

        if (_byName.TryGetValue(name, out string? exact))
        {
            return exact;
        }

        return _byName
            .Where(pair => pair.Key.StartsWith(name + " ", StringComparison.OrdinalIgnoreCase))
            .Select(static pair => pair.Value)
            .FirstOrDefault();
    }

    private static IEnumerable<string> AllowedRoots()
    {
        foreach (
            Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.LocalApplicationData,
                Environment.SpecialFolder.CommonApplicationData,
            }
        )
        {
            string path = Environment.GetFolderPath(folder);
            if (path.Length > 0)
            {
                yield return path;
            }
        }
    }

    private static IEnumerable<string> ProtectedRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (programData.Length > 0)
        {
            // Defender's live engine and the Store's app files live here.
            yield return Path.Combine(programData, "Microsoft");
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (localAppData.Length > 0)
        {
            yield return Path.Combine(localAppData, "Microsoft", "WindowsApps");
            yield return Path.Combine(localAppData, "Packages");
        }

        foreach (
            Environment.SpecialFolder folder in new[]
            {
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
            }
        )
        {
            string programs = Environment.GetFolderPath(folder);
            if (programs.Length == 0)
            {
                continue;
            }

            yield return Path.Combine(programs, "WindowsApps");
            yield return Path.Combine(programs, "Windows Defender");
            yield return Path.Combine(programs, "Windows Defender Advanced Threat Protection");
            yield return Path.Combine(programs, "Windows NT");
            yield return Path.Combine(programs, "Windows Security");
            yield return Path.Combine(programs, "Windows Mail");
            yield return Path.Combine(programs, "Windows Media Player");
            yield return Path.Combine(programs, "Common Files");
            yield return Path.Combine(programs, "Microsoft", "Edge");
            yield return Path.Combine(programs, "Microsoft", "EdgeUpdate");
        }
    }

    private void Scan(RegistryKey hive, string path, Func<string, bool>? accept = null)
    {
        using RegistryKey? root = hive.OpenSubKey(path);
        if (root is null)
        {
            return;
        }

        foreach (string subName in root.GetSubKeyNames())
        {
            using RegistryKey? sub = root.OpenSubKey(subName);
            if (sub is null)
            {
                continue;
            }

            string? directory = sub.GetValue("InstallLocation") as string;
            if (string.IsNullOrEmpty(directory) && sub.GetValue("DisplayIcon") is string icon)
            {
                directory = Path.GetDirectoryName(icon.Replace("\"", "", StringComparison.Ordinal));
            }

            if (string.IsNullOrEmpty(directory) || (accept is not null && !accept(directory)))
            {
                continue;
            }

            Add(sub.GetValue("WinGetPackageIdentifier") as string, sub.GetValue("DisplayName") as string, directory);
        }
    }
}
