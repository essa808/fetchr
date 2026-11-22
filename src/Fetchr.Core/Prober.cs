using System.Net;
using System.Net.Http.Headers;

namespace Fetchr.Core;

public sealed class ProbeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Asks the server for byte 0 only, to learn the name, size and whether ranges work.</summary>
public static class Prober
{
    public static async Task<ProbeResult> ProbeAsync(HttpClient http, string url, string? referer, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ProbeException("Only http:// and https:// links are supported.");

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        req.Headers.Range = new RangeHeaderValue(0, 0);
        if (referer is not null && Uri.TryCreate(referer, UriKind.Absolute, out var r)) req.Headers.Referrer = r;
        RequestHeaders.Apply(req, RequestHeaders.Filter(headers));

        HttpResponseMessage resp;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ProbeException("Couldn't reach the server: " + e.Message, e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new ProbeException("The server took too long to answer.", e);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
                throw new ProbeException(resp.StatusCode switch
                {
                    HttpStatusCode.NotFound => "The file isn't there any more (404).",
                    HttpStatusCode.Forbidden => "The server refused the download (403). The link may have expired.",
                    HttpStatusCode.Unauthorized => "This link needs a login (401).",
                    _ => $"The server answered {(int)resp.StatusCode} {resp.ReasonPhrase}.",
                });

            var finalUri = resp.RequestMessage?.RequestUri ?? uri;
            var content = resp.Content.Headers;
            var resumable = resp.StatusCode == HttpStatusCode.PartialContent;
            long? size = resumable ? content.ContentRange?.Length : content.ContentLength;
            // A server that sends 206 but hides the total cannot be split safely.
            if (resumable && size is null) resumable = false;

            var name = FileNames.FromResponse(content.ContentDisposition, finalUri, content.ContentType?.MediaType);
            var etag = resp.Headers.ETag is { IsWeak: false } e ? e.ToString() : null;
            var lastModified = content.LastModified?.ToString("r");
            return new ProbeResult(uri.AbsoluteUri, finalUri.AbsoluteUri, name, size, resumable, etag, lastModified, content.ContentType?.MediaType);
        }
    }
}
