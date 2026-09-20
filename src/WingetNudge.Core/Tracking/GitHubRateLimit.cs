using System.Globalization;
using System.Net;

namespace WingetNudge.Core.Tracking;

/// <summary>Reads GitHub's rate-limit signal off a response.</summary>
public static class GitHubRateLimit
{
    /// <summary>
    /// Whether the response says the unauthenticated budget is spent: a 403 or 429 whose
    /// <c>X-RateLimit-Remaining</c> header is zero.
    /// </summary>
    /// <param name="response">Response from api.github.com.</param>
    /// <returns><c>true</c> when further calls this hour will fail the same way.</returns>
    public static bool IsExhausted(HttpResponseMessage response)
    {
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests))
        {
            return false;
        }

        return response.Headers.TryGetValues("X-RateLimit-Remaining", out IEnumerable<string>? values)
            && values.Any(static value =>
                int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int remaining)
                && remaining == 0
            );
    }
}
