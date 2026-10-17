using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

// An invalid saved token must not prevent access to public releases. Keep the
// fallback local to this operation; never erase the user's configured token.
internal sealed class GithubReleaseSession(HttpClient client, string token)
{
    private bool anonymous = string.IsNullOrWhiteSpace(token);

    public async Task<HttpResponseMessage> GetAsync(string url, CancellationToken cancellation = default)
    {
        var response = await SendAsync(url, cancellation);
        if (!anonymous && response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            anonymous = true;
            response = await SendAsync(url, cancellation);
        }
        return response;
    }

    private async Task<HttpResponseMessage> SendAsync(string url, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (!anonymous) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(request, cancellation);
    }
}
