using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// A temporary diagnostic listener - not a real integration. The booking platform's webhook
/// documentation is sparse enough that the payload shape needs to be observed empirically before
/// anything real is built against it (architecture doc §5.4.4). This endpoint just records whatever
/// it receives (headers + raw body, any content type) into WebhookCaptureService, viewable on the
/// staff Diagnostics page. No Graph calls, no writes to the calendar, no parsing - zero blast radius
/// regardless of what gets sent here.
///
/// Guarded by an unguessable token in the URL path rather than a real signature, since the sending
/// platform's actual auth capabilities aren't known yet either (that's part of what this is for).
/// Meant to be torn down (or replaced with the real, signature-verified endpoint) once the payload
/// shape is known - see the architecture doc for the follow-up design.
/// </summary>
public static class WebhookCaptureEndpoint
{
    public static void MapWebhookCaptureEndpoint(this WebApplication app)
    {
        app.MapMethods("/api/webhook-capture/{token}", ["GET", "POST"], async (string token, HttpContext context, WebhookCaptureService capture, IConfiguration config, ILogger<CaptureMarker> logger) =>
        {
            var expectedToken = config["Webhook:CaptureToken"];
            if (string.IsNullOrEmpty(expectedToken) || token != expectedToken)
            {
                // Deliberately a plain 404, not 401/403 - gives a random scanner no signal that
                // anything interesting lives at this path.
                return Results.NotFound();
            }

            string body;
            using (var reader = new StreamReader(context.Request.Body))
            {
                body = await reader.ReadToEndAsync();
            }

            var headers = context.Request.Headers
                .Select(h => (h.Key, string.Join(", ", h.Value.ToArray())))
                .ToList();

            var captured = new CapturedWebhookRequest(
                DateTime.UtcNow,
                context.Request.Method,
                context.Connection.RemoteIpAddress?.ToString(),
                context.Request.QueryString.HasValue ? context.Request.QueryString.Value : null,
                context.Request.ContentType,
                headers,
                body);

            capture.Capture(captured);
            logger.LogInformation(
                "Webhook capture: {Method} from {RemoteIp}, content-type {ContentType}, {BodyLength} byte body",
                captured.Method, captured.RemoteIp, captured.ContentType, body.Length);

            return Results.Ok(new { received = true });
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-api");
    }

    // Just a type to hang the ILogger<T> category name off - not otherwise meaningful.
    private sealed class CaptureMarker;
}
