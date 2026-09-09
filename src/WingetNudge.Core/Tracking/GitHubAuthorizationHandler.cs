using System.Net.Http.Headers;

namespace WingetNudge.Core.Tracking;

/// <summary>
/// Attaches a GitHub token to requests bound for <c>api.github.com</c> and nothing else, so a
/// shared client can also fetch user-registered tool URLs without leaking the token to them.
/// </summary>
/// <param name="token">Personal access token, or <c>null</c> to send nothing.</param>
public sealed class GitHubAuthorizationHandler(string? token) : DelegatingHandler
{
    /// <summary>The only host that receives the token.</summary>
    public const string ApiHost = "api.github.com";

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(request);
        if (
            !string.IsNullOrWhiteSpace(token)
            && request.RequestUri is { Scheme: "https" } uri
            && uri.Host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase)
        )
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else
        {
            request.Headers.Authorization = null;
        }

        return base.SendAsync(request, cancellationToken);
    }
}
