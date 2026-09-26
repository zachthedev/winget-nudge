using System.Globalization;

namespace WingetNudge.CustomActions;

/// <summary>
/// Decides whether the running Windows build reaches the build the package authors as its floor.
/// </summary>
/// <remarks>
/// The test suite compiles this file in by link, so it holds no Windows Installer or system call.
/// The decisions about what those calls return live here instead.
/// </remarks>
public static class WindowsRequirement
{
    /// <summary>
    /// The high nibble of <c>NtBuildNumber</c>, which the kernel keeps for its free and checked flags rather
    /// than the build.
    /// </summary>
    public const uint BuildFlagBits = 0xF0000000;

    /// <summary>
    /// The lowest <c>NtMajorVersion</c> whose page <see cref="SharedDataBuild"/> reads <c>NtBuildNumber</c> from.
    /// The builds the floor admits carry the field at that offset. A page without it holds a reserved field
    /// there, which reads 0, and no floor admits 0.
    /// </summary>
    public const uint FirstSharedBuildMajor = 10;

    /// <summary>
    /// The build number <c>KUSER_SHARED_DATA</c> publishes, from its <c>NtMajorVersion</c> and
    /// <c>NtBuildNumber</c> fields.
    /// </summary>
    /// <param name="majorVersion">The page's <c>NtMajorVersion</c>.</param>
    /// <param name="ntBuildNumber">The page's <c>NtBuildNumber</c>, flag bits included.</param>
    /// <returns>The build, or 0 when the page predates the field, which no floor admits.</returns>
    /// <example>
    /// <code>
    /// uint build = WindowsRequirement.SharedDataBuild(10, 0x6658);
    /// </code>
    /// </example>
    public static uint SharedDataBuild(uint majorVersion, uint ntBuildNumber) =>
        majorVersion >= FirstSharedBuildMajor ? ntBuildNumber & ~BuildFlagBits : 0;

    /// <summary>Reads the floor the package authors, a build number written in decimal digits alone.</summary>
    /// <param name="text">The property value.</param>
    /// <returns>The build, or <c>null</c> when the text is not a build number above zero.</returns>
    /// <example>
    /// <code>
    /// int? floor = WindowsRequirement.ParseFloor("22000");
    /// </code>
    /// </example>
    public static int? ParseFloor(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int floor) && floor > 0 ? floor : null;

    /// <summary>Whether a Windows build reaches the floor.</summary>
    /// <param name="build">The build number the running Windows reports.</param>
    /// <param name="floor">The lowest build setup accepts.</param>
    /// <returns><c>true</c> when the build is the floor or above it.</returns>
    public static bool Satisfies(uint build, int floor) => build >= floor;
}
