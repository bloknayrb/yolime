using System.Net;
using Yolime.Core.Net;

namespace Yolime.Core.Tests.Stt;

public class HostPinningTests
{
    private static Task<HttpResponseMessage> Send(string url, string expect)
    {
        var client = HttpClients.Create(FakeHttpHandler.Json(HttpStatusCode.OK, "{}"));
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        HostPinningHandler.Expect(request, expect);
        return client.SendAsync(request);
    }

    [Fact]
    public async Task Https_ToTheExpectedHost_IsSent() =>
        Assert.Equal(HttpStatusCode.OK, (await Send("https://api.anthropic.com/v1/messages", "api.anthropic.com")).StatusCode);

    [Fact]
    public Task PlainHttp_IsRefused_EvenToTheRightHost() =>
        Assert.ThrowsAsync<HostPinningException>(() => Send("http://api.anthropic.com/v1/messages", "api.anthropic.com"));

    [Fact]
    public Task AnotherHost_IsRefused() =>
        Assert.ThrowsAsync<HostPinningException>(() => Send("https://evil.example/v1/messages", "api.anthropic.com"));
}
