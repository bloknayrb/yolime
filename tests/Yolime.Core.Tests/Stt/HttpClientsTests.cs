using Yolime.Core.Net;

namespace Yolime.Core.Tests.Stt;

public class HttpClientsTests
{
    [Fact]
    public void Shared_HasNoDefaultAuth_NoRedirects_NoCertCallback()
    {
        Assert.Null(HttpClients.Shared.DefaultRequestHeaders.Authorization);
        Assert.Equal(Timeout.InfiniteTimeSpan, HttpClients.Shared.Timeout);

        var handler = HttpClients.CreateSocketsHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.ConnectTimeout);
        Assert.NotNull(handler.ConnectCallback);
    }
}
