using System.Net;

namespace Fcl.Sync.Service.Tests;

internal sealed class StubHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> responder;

    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this(request => Task.FromResult(responder(request)))
    {
    }

    public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        this.responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        responder(request);
}
