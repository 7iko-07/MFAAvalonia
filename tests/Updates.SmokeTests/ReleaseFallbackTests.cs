using System.Net;
using MFAAvalonia.Helper;

internal static class ReleaseFallbackTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.InternalServerError, HttpStatusCode.OK })
        {
            using var handler = new Handler(status);
            using var client = new HttpClient(handler);
            var session = new GithubReleaseSession(client, "expired-token");
            using var first = await session.GetAsync("https://api.github.com/repos/owner/repo/releases");
            if (status == HttpStatusCode.Unauthorized)
            {
                check(first.IsSuccessStatusCode, "Invalid token blocked public releases");
                using var next = await session.GetAsync("https://api.github.com/repos/owner/repo/releases/tags/v2");
                check(handler.Headers.SequenceEqual(new string?[] { "Bearer expired-token", null, null }), "Pagination/tag lookup retried an invalid token");
            }
            else
            {
                check(first.StatusCode == status, "Unexpected release response replacement");
                check(handler.Headers.Count == 1, "Non-authentication error caused an anonymous retry");
            }
        }
        using var missing = new Handler(HttpStatusCode.Unauthorized, anonymousStatus: HttpStatusCode.NotFound);
        using var missingClient = new HttpClient(missing);
        using var response = await new GithubReleaseSession(missingClient, "expired-token").GetAsync("https://api.github.com/repos/owner/private/releases");
        check(response.StatusCode == HttpStatusCode.NotFound && missing.Headers.Count == 2, "Anonymous failure retried indefinitely or hid the error");
        using var anonymous = new Handler(HttpStatusCode.Unauthorized);
        using var anonymousClient = new HttpClient(anonymous);
        using var publicResponse = await new GithubReleaseSession(anonymousClient, "").GetAsync("https://api.github.com/repos/owner/repo/releases");
        check(publicResponse.IsSuccessStatusCode && anonymous.Headers.SequenceEqual(new string?[] { null }), "Missing token did not use anonymous access directly");
    }

    private sealed class Handler(HttpStatusCode authenticatedStatus, HttpStatusCode anonymousStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<string?> Headers { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var header = request.Headers.Authorization?.ToString();
            Headers.Add(header);
            return Task.FromResult(new HttpResponseMessage(header == null ? anonymousStatus : authenticatedStatus)
                { Content = new StringContent("[]") });
        }
    }
}
