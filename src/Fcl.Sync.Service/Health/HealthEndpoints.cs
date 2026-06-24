namespace Fcl.Sync.Service.Health;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            checkedAt = DateTimeOffset.UtcNow
        }));

        return app;
    }
}
