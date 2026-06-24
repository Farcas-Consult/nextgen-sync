using Microsoft.Extensions.Options;

namespace Fcl.Sync.Service.Webhooks;

public static class GymMasterWebhookEndpoints
{
    public static IEndpointRouteBuilder MapGymMasterWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/gymmaster", async (
            HttpRequest request,
            GymMasterWebhookEvent webhookEvent,
            IOptions<GymMasterWebhookOptions> options,
            GymMasterWebhookHandler handler,
            CancellationToken cancellationToken) =>
        {
            if (!request.Headers.TryGetValue("X-Gymmaster-Token", out var token) ||
                token.Count != 1 ||
                !string.Equals(token[0], options.Value.SecretToken, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }

            await handler.HandleAsync(webhookEvent, cancellationToken);
            return Results.Accepted(value: new { webhookEvent.EventId });
        });

        return app;
    }
}
