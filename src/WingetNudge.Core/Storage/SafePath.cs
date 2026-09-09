namespace WingetNudge.Core.Storage;

/// <summary>
/// Guards against reparse points in directories the app writes to. The elevated upgrade
/// process writes under a profile directory that any process running as the user can replace
/// with a junction before the first write.
/// </summary>
public static class SafePath
{
    /// <summary>Whether the path exists and is a symbolic link, junction or other reparse point.</summary>
    /// <param name="path">File or directory.</param>
    /// <returns><c>true</c> when the entry redirects elsewhere.</returns>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception)
            when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Throws when the directory, or any ancestor of it below the user's profile, is a reparse
    /// point.
    /// </summary>
    /// <param name="directory">Directory about to be written to.</param>
    /// <exception cref="IOException">The directory or an ancestor redirects elsewhere.</exception>
    public static void EnsureNotReparsePoint(string directory)
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? current = Path.GetFullPath(directory);
        while (current is not null && !current.Equals(profile, StringComparison.OrdinalIgnoreCase))
        {
            if (IsReparsePoint(current))
            {
                throw new IOException(
                    $"'{current}' is a reparse point; refusing to write through it."
                );
            }

            if (
                Path.GetPathRoot(current) is string root
                && root.Equals(current, StringComparison.OrdinalIgnoreCase)
            )
            {
                break;
            }

            current = Path.GetDirectoryName(current);
        }
    }
}
