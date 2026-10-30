using System.Net;
using System.Text;

namespace Yolime.Core.Tests.Stt;

/// <summary>Captures each request (with its body already read) and answers with a canned response.</summary>
public sealed class FakeHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, byte[] Body)> Requests { get; } = [];

    public static FakeHttpHandler Json(HttpStatusCode status, string json) =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });

    public static FakeHttpHandler Throws(Exception ex) => new(_ => throw ex);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        Requests.Add((request, body));
        ct.ThrowIfCancellationRequested();
        return respond(request);
    }
}
