using System.Net.Http.Headers;
using System.Reflection;

namespace WingetNudge.Core.Tracking;

/// <summary>The <c>User-Agent</c> product token the shared HTTP client sends, which GitHub requires.</summary>
public static class AppUserAgent
{
    /// <summary>
    /// Builds <c>WingetNudge/&lt;version&gt;</c> from the assembly's informational version, which is the
    /// <c>Version</c> that Directory.Build.props sets, with any <c>+</c> build metadata cut off.
    /// </summary>
    /// <remarks>
    /// The SDK appends the source commit to the informational version as build metadata. The token names the
    /// release alone, so no host the client reaches receives the commit.
    /// </remarks>
    /// <param name="assembly">The assembly whose version the token carries, normally the app's own.</param>
    /// <returns>The product token.</returns>
    /// <exception cref="InvalidOperationException">The assembly carries no informational version.</exception>
    public static ProductInfoHeaderValue For(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string informational =
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? throw new InvalidOperationException($"{assembly.GetName().Name} carries no informational version.");
        return new ProductInfoHeaderValue("WingetNudge", informational.Split('+', 2)[0]);
    }
}
