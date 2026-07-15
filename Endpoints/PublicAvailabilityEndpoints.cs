using FacilityScheduler.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// The app's only internet-anonymous surface (architecture doc §6.4) - kept in its own file so
/// Program.cs doesn't grow unwieldy, mirroring how the Graph services already live separately.
/// </summary>
public static class PublicAvailabilityEndpoints
{
    public static void MapPublicAvailabilityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/public/availability", async (int? days, PublicAvailabilityService service, CancellationToken ct) =>
        {
            var result = await service.GetAvailabilityAsync(days, ct);
            return Results.Ok(result);
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-api")
        .RequireCors("public-api");
    }
}
