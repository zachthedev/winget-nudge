using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Tests.Support;

/// <summary>
/// Turns a directory into a junction at the moment <see cref="SafePath.OpenDirectory"/> has verified it and before
/// anything opens relative to it, as another process running as the user could. A check by path passes at that moment,
/// and an open by path after it would follow the junction.
/// </summary>
/// <remarks>
/// The watch reaches only code this test calls, never a test running beside it. Dispose puts back what was there and
/// removes the junction, which leaves its target's contents untouched.
/// </remarks>
public sealed class JunctionAfterCheck : IDisposable
{
    private readonly string _directory;
    private readonly string _target;
    private readonly Action<string>? _previous;

    /// <summary>Starts watching for the walk to verify <paramref name="directory"/>.</summary>
    /// <param name="directory">The directory to convert. It has to be empty when the walk verifies it.</param>
    /// <param name="target">The directory the junction redirects to.</param>
    public JunctionAfterCheck(string directory, string target)
    {
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _target = target;
        _previous = SafePath.BetweenCheckAndOpen.Value;
        SafePath.BetweenCheckAndOpen.Value = OnVerified;
    }

    /// <summary>Whether the directory became a junction.</summary>
    public bool Converted { get; private set; }

    /// <inheritdoc/>
    public void Dispose()
    {
        SafePath.BetweenCheckAndOpen.Value = _previous;

        // The directory is deleted after the case, and a recursive delete fails on a junction without elevation.
        if (SafePath.IsReparsePoint(_directory))
        {
            Directory.Delete(_directory);
        }
    }

    private void OnVerified(string directory)
    {
        if (!Converted && string.Equals(directory, _directory, StringComparison.OrdinalIgnoreCase))
        {
            Junction.Create(directory, _target);
            Converted = true;
        }
    }
}
