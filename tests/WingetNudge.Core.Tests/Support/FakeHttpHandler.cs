using System.Net;

namespace WingetNudge.Core.Tests.Support;

/// <summary>Serves canned responses keyed by URL substring; anything else is 404.</summary>
public sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly List<(string Fragment, HttpStatusCode Status, string Body)> _routes = [];

    public List<string> Requests { get; } = [];

    public FakeHttpHandler Map(
        string urlFragment,
        string body,
        HttpStatusCode status = HttpStatusCode.OK
    )
    {
        _routes.Add((urlFragment, status, body));
        return this;
    }

    public HttpClient CreateClient() => new(this) { BaseAddress = null };

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken
    )
    {
        string url = request.RequestUri?.ToString() ?? "";
        Requests.Add(url);
        foreach ((string fragment, HttpStatusCode status, string body) in _routes)
        {
            if (url.Contains(fragment, StringComparison.Ordinal))
            {
                return Task.FromResult(
                    new HttpResponseMessage(status) { Content = new StringContent(body) }
                );
            }
        }

        return Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("") }
        );
    }
}
