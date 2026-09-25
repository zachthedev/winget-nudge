using WingetNudge.Core.Storage;

namespace WingetNudge.Core.Tests.Support;

/// <summary>
/// Watches the waits between a state file's attempts while in scope. It records each planned delay and runs an action
/// at a chosen wait, before the product sleeps that delay, so a holder lets go at an exact attempt rather than at a
/// time.
/// </summary>
/// <remarks>
/// The watch reaches only code this test calls, never a test running beside it. Dispose puts back what was there.
/// </remarks>
public sealed class AttemptWaits : IDisposable
{
    private readonly List<int> _delays = [];
    private readonly Action<int>? _previous;
    private readonly int _actAt;
    private readonly Action? _act;

    /// <summary>Starts recording waits, running nothing at any of them.</summary>
    public AttemptWaits()
        : this(0, null) { }

    /// <summary>
    /// Starts recording waits, and runs <paramref name="act"/> at wait number <paramref name="actAt"/>.
    /// </summary>
    /// <param name="actAt">The wait, counting from 1, at which the action runs.</param>
    /// <param name="act">What runs there, such as letting a holder go.</param>
    public AttemptWaits(int actAt, Action? act)
    {
        _actAt = actAt;
        _act = act;
        _previous = JsonFile.BetweenAttempts.Value;
        JsonFile.BetweenAttempts.Value = OnWait;
    }

    /// <summary>Each planned delay in milliseconds, in the order the waits came.</summary>
    public IReadOnlyList<int> Delays => _delays;

    /// <inheritdoc/>
    public void Dispose() => JsonFile.BetweenAttempts.Value = _previous;

    private void OnWait(int delay)
    {
        _delays.Add(delay);
        if (_delays.Count == _actAt)
        {
            _act?.Invoke();
        }
    }
}
