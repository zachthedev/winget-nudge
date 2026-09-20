using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WingetNudge.Core.Storage;

/// <summary>Reads and writes the app's JSON state files with one shared serializer configuration.</summary>
public static class JsonFile
{
    private const int ReadRetries = 3;

    /// <summary>Serializer options shared by every state file.</summary>
    public static JsonSerializerOptions Options { get; } =
        new(JsonSerializerDefaults.General)
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = null,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        };

    /// <summary>
    /// Reads a JSON file, returning <c>null</c> when it is absent or unreadable.
    /// </summary>
    /// <typeparam name="T">Deserialized shape.</typeparam>
    /// <param name="path">File to read.</param>
    /// <param name="deleteIfCorrupt">
    /// When true, a file that fails to parse is deleted so the next write starts clean. Use for
    /// state that is recreatable. When false the corrupt file is renamed aside with a
    /// <c>.corrupt</c> suffix so user data survives for repair.
    /// </param>
    /// <returns>The parsed value, or <c>null</c> when the file is missing or corrupt.</returns>
    public static T? Read<T>(string path, bool deleteIfCorrupt)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return JsonSerializer.Deserialize<T>(stream, Options);
            }
            catch (JsonException)
            {
                SetAside(path, deleteIfCorrupt);
                return null;
            }
            catch (IOException) when (attempt < ReadRetries)
            {
                // A writer is mid-replace; the next attempt sees the finished file.
                Thread.Sleep(50 * attempt);
            }
        }
    }

    /// <summary>
    /// Serializes a value to a file, creating the parent directory when missing. The content
    /// goes to a temporary file first and replaces the target in one move, so a reader never
    /// sees a half-written file.
    /// </summary>
    /// <typeparam name="T">Serialized shape.</typeparam>
    /// <param name="path">File to write.</param>
    /// <param name="value">Value to serialize.</param>
    /// <exception cref="IOException">The parent directory is a reparse point.</exception>
    public static void Write<T>(string path, T value)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            SafePath.EnsureNotReparsePoint(directory);
        }

        string temporary = $"{path}.{Environment.ProcessId}-{Guid.NewGuid():N}.tmp";
        try
        {
            using (FileStream stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, value, Options);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            // A failed move would otherwise leave the serialized value sitting in the temporary,
            // where nothing ever collects it.
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Nothing further to try; the caller already has the original failure.
                }
            }
        }
    }

    /// <summary>Extension every half-written file carries while <see cref="Write"/> runs.</summary>
    public const string TemporarySuffix = ".tmp";

    /// <summary>
    /// Deletes temporary files left behind by a write that never finished, which a process kill
    /// or a power loss mid-replace produces.
    /// </summary>
    /// <remarks>
    /// Age is the test rather than the process id in the name, because a process id is reused
    /// and a live write finishes in milliseconds. Anything older than the window is abandoned.
    /// </remarks>
    /// <param name="directory">Data directory to sweep.</param>
    /// <param name="olderThan">How stale a file must be before it counts as abandoned.</param>
    /// <returns>Number of files removed.</returns>
    public static int SweepTemporaries(string directory, TimeSpan olderThan)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            // A junction here would aim these deletes at whatever directory it points to.
            SafePath.EnsureNotReparsePoint(directory);
        }
        catch (IOException)
        {
            return 0;
        }

        int removed = 0;
        DateTime cutoff = DateTime.UtcNow - olderThan;
        try
        {
            foreach (string path in Directory.EnumerateFiles(directory, $"*{TemporarySuffix}"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                    {
                        continue;
                    }

                    File.Delete(path);
                    removed++;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Another process holds it, so it is not abandoned after all.
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The directory went away or is unreadable; nothing to clean.
        }

        return removed;
    }

    private static void SetAside(string path, bool delete)
    {
        try
        {
            // A junction in the path would aim this delete or rename at another directory.
            if (Path.GetDirectoryName(path) is string directory)
            {
                SafePath.EnsureNotReparsePoint(directory);
            }

            if (delete)
            {
                File.Delete(path);
                return;
            }

            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(path, $"{path}.{stamp}.corrupt", overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The file stays; the next read reports it again.
        }
    }
}
