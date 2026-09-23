namespace WingetNudge.CustomActions;

/// <summary>
/// Decides whether a Windows App Runtime framework package registered for the installing user is one
/// the app's bootstrapper accepts: the same package, the same architecture, at or above its minimum
/// version.
/// </summary>
/// <remarks>
/// The test suite compiles this file in by link, so it holds no Windows Installer or packaging API
/// call. The decisions about what those calls return live here instead.
/// </remarks>
public static class RuntimeRequirement
{
    /// <summary>Publisher ID of every Windows App Runtime package, the last field of its full name.</summary>
    public const string PublisherId = "8wekyb3d8bbwe";

    /// <summary><c>ERROR_SUCCESS</c>.</summary>
    public const int ErrorSuccess = 0;

    /// <summary><c>ERROR_INSUFFICIENT_BUFFER</c>, the answer to a sizing call when packages are registered.</summary>
    public const int ErrorInsufficientBuffer = 122;

    /// <summary>What the sizing call of <c>GetPackagesByPackageFamily</c>, made with no buffers, reports.</summary>
    public enum Listing
    {
        /// <summary>No package in the family is registered for the user.</summary>
        Empty,

        /// <summary>Packages are registered, and a second call with buffers of the reported size lists them.</summary>
        Fetch,

        /// <summary>The call failed, or answered in a way the API's contract rules out.</summary>
        Failed,
    }

    /// <summary>
    /// Reads the sizing call's result and count. Any result but success with nothing registered, or
    /// an insufficient buffer with something registered, is a failure, so an API error is never read
    /// as an empty family.
    /// </summary>
    /// <param name="result">The Windows error code the call returned.</param>
    /// <param name="count">The package count the call reported.</param>
    /// <returns>What the caller does next.</returns>
    public static Listing ReadSizingCall(int result, uint count) =>
        (result, count) switch
        {
            (ErrorSuccess, 0) => Listing.Empty,
            (ErrorInsufficientBuffer, > 0) => Listing.Fetch,
            _ => Listing.Failed,
        };

    /// <summary>Family name of a Windows App Runtime package, the name the packaging API lists by.</summary>
    /// <param name="packageName">Package name, such as <c>Microsoft.WindowsAppRuntime.2</c>.</param>
    /// <returns>The family name, such as <c>Microsoft.WindowsAppRuntime.2_8wekyb3d8bbwe</c>.</returns>
    public static string FamilyName(string packageName) => $"{packageName}_{PublisherId}";

    /// <summary>
    /// The highest version among the full names that names the package and the architecture and is at
    /// least the floor.
    /// </summary>
    /// <param name="fullNames">
    /// Package full names, each <c>Name_Version_Architecture_ResourceId_PublisherId</c>. A name in any
    /// other shape is skipped.
    /// </param>
    /// <param name="packageName">Package name the bootstrapper resolves.</param>
    /// <param name="architecture">Architecture field the process needs, such as <c>x64</c>.</param>
    /// <param name="floor">Minimum version the bootstrapper accepts.</param>
    /// <returns>The version, or <c>null</c> when no full name satisfies all three.</returns>
    /// <example>
    /// <code>
    /// Version? found = RuntimeRequirement.HighestSatisfying(
    ///     ["Microsoft.WindowsAppRuntime.2_2.5.1.0_x64__8wekyb3d8bbwe"],
    ///     "Microsoft.WindowsAppRuntime.2",
    ///     "x64",
    ///     new Version(2, 4, 0, 0)
    /// );
    /// </code>
    /// </example>
    public static Version? HighestSatisfying(
        IEnumerable<string> fullNames,
        string packageName,
        string architecture,
        Version floor
    )
    {
        Version? highest = null;
        foreach (string fullName in fullNames)
        {
            // Package identity compares without regard to case.
            string[] fields = fullName.Split('_');
            if (
                fields.Length == 5
                && string.Equals(fields[0], packageName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fields[2], architecture, StringComparison.OrdinalIgnoreCase)
                && string.Equals(fields[4], PublisherId, StringComparison.OrdinalIgnoreCase)
                && Version.TryParse(fields[1], out Version? version)
                && version >= floor
                && (highest is null || version > highest)
            )
            {
                highest = version;
            }
        }

        return highest;
    }
}
