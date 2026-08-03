using System.Security.Cryptography;
using System.Text;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// The real Breely booking-notification webhook - replaces WebhookCaptureEndpoint's diagnostic-only
/// role now that the payload shape is known. A "dumb webhook" by design (architecture doc §5.4.5):
/// the booking already happened in the real world by the time this fires, so the job is to reflect
/// it, never to reject it - always ack quickly and rely on logs/NeedsTriage markers, not the HTTP
/// response, to surface anything that needs a human.
///
/// Auth is a static shared-secret header, not a computed signature - Breely's own webhook
/// configuration only supports a fixed URL, static custom headers, and a body, with no way to
/// compute a per-request signature on its end (confirmed empirically, not from documentation, which
/// was too sparse to rely on). This is a materially weaker guarantee than HMAC (a leaked secret is
/// reusable indefinitely rather than scoped to one request), but it's what the sending platform can
/// actually do; same "AllowAnonymous plus its own check" pattern as every other public endpoint,
/// never a Blazor component (D15).
/// </summary>
public static class BreelyBookingWebhookEndpoint
{
    public static void MapBreelyBookingWebhookEndpoint(this WebApplication app)
    {
        app.MapPost("/api/webhooks/breely", async (HttpContext context, IConfiguration config, BreelyBookingProcessor processor, ILogger<BreelyBookingProcessor> logger, CancellationToken ct) =>
        {
            var expectedSecret = config["Webhook:BreelySharedSecret"];
            var providedSecret = context.Request.Headers["X-Webhook-Secret"].FirstOrDefault();

            if (string.IsNullOrEmpty(expectedSecret) || providedSecret is null || !SecretsMatch(expectedSecret, providedSecret))
            {
                return Results.Unauthorized();
            }

            BreelyWebhookPayload? payload;
            try
            {
                payload = await context.Request.ReadFromJsonAsync<BreelyWebhookPayload>(cancellationToken: ct);
            }
            catch (System.Text.Json.JsonException ex)
            {
                logger.LogWarning(ex, "Breely webhook: malformed JSON body - acknowledged anyway, nothing to retry productively.");
                return Results.Ok();
            }

            var evt = payload?.Event;
            if (evt is null)
            {
                logger.LogWarning("Breely webhook: request had no top-level \"event\" object.");
                return Results.Ok();
            }

            try
            {
                await processor.ProcessAsync(evt, ct);
            }
            catch (Exception ex)
            {
                // Never let a processing failure surface as a non-2xx to the sender - this is a
                // fire-and-forget notification with no retry semantics we control either way, and a
                // non-2xx here wouldn't cause Breely to do anything useful, only obscure the real
                // signal (which lives in this log entry, not the HTTP response).
                logger.LogError(ex, "Breely webhook: failed to process event {Id}", evt.Id);
            }

            return Results.Ok();
        })
        .AllowAnonymous()
        .RequireRateLimiting("booking-webhook");
    }

    private static bool SecretsMatch(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
