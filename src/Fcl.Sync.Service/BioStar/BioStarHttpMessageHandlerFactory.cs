using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.BioStar;

public static class BioStarHttpMessageHandlerFactory
{
    public static HttpMessageHandler Create(IServiceProvider services)
    {
        var options = services.GetRequiredService<IOptions<BioStarOptions>>().Value;
        return new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = options.AllowInvalidServerCertificate
                ? HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                : null
        };
    }
}
