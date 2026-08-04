using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        app.MapPost("/api/webhooks/breely", async (HttpContext context, IConfiguration config, BreelyBookingProcessor processor, AppLogService appLog, ILogger<BreelyBookingProcessor> logger, CancellationToken ct) =>
        {
            var expectedSecret = config["Webhook:BreelySharedSecret"];
            var providedSecret = context.Request.Headers["X-Webhook-Secret"].FirstOrDefault();

            if (string.IsNullOrEmpty(expectedSecret) || providedSecret is null || !SecretsMatch(expectedSecret, providedSecret))
            {
                var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                logger.LogWarning("Breely webhook: rejected a request with a missing/incorrect X-Webhook-Secret from {RemoteIp}.", remoteIp);
                await appLog.LogSecurityAsync("WebhookAuthFailed", remoteIp, "Missing or incorrect X-Webhook-Secret on /api/webhooks/breely.", ct);
                return Results.Unauthorized();
            }

            var rawBody = await new StreamReader(context.Request.Body).ReadToEndAsync(ct);

            // Debug-tier only, one-shot diagnostic visibility into the *entire* raw body - not just
            // the fixed subset BreelyEvent maps. Added 2026-08-03 after a multi-sheet booking only
            // claimed one sheet: Breely's own UI showed 3 events created but only 1 webhook sent, so
            // whatever tells us "this reservation is 3 sheets" has to be in this raw body somewhere,
            // in a field the mapped DTO would otherwise silently drop. Redacts the three known PII
            // field values (same fields BreelyBookingProcessor redacts) but not any other field, so
            // a lookalike PII field we don't yet know about could still appear here - acceptable for
            // a temporary, Debug-tier-only diagnostic capture, not a standing behavior.
            await appLog.LogDebugAsync("WebhookRawPayloadReceived", "Breely webhook", details: RedactAndTruncate(rawBody), ct: ct);

            BreelyWebhookPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<BreelyWebhookPayload>(rawBody);
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Breely webhook: malformed JSON body - acknowledged anyway, nothing to retry productively.");
                return Results.Ok();
            }

            if (payload is null)
            {
                logger.LogWarning("Breely webhook: request body did not deserialize to a payload object.");
                return Results.Ok();
            }

            // Acknowledge immediately rather than awaiting processing - a multi-sheet batch can mean
            // dozens of sequential Graph calls (well past what "ack fast" implies), and awaiting on
            // the request's own cancellation token meant an HTTP timeout on Breely's side could abort
            // mid-write (e.g. between releasing an old slot and claiming the new one on a reschedule),
            // leaving a booking missing from this calendar while it still exists in Breely. Processing
            // now runs detached, on CancellationToken.None, so once started it always runs to
            // completion; every service `processor` depends on is a singleton (Program.cs), so nothing
            // here is tied to this request's (about-to-end) DI scope. Resolves the top-level "event"
            // plus any siblings in "submission.events" itself - see BreelyBookingProcessor's class doc
            // for why this app can't just look at "event" alone. Per-event failures inside are already
            // individually caught there.
            _ = ProcessInBackgroundAsync(processor, payload, logger);

            return Results.Ok();
        })
        .AllowAnonymous()
        .RequireRateLimiting("booking-webhook");
    }

    private static async Task ProcessInBackgroundAsync(BreelyBookingProcessor processor, BreelyWebhookPayload payload, ILogger logger)
    {
        try
        {
            await processor.ProcessAsync(payload, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Breely webhook: failed to process payload.");
        }
    }

    private static bool SecretsMatch(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }

    private const int MaxRawPayloadLogLength = 8000;

    private static readonly (Regex Pattern, string Replacement)[] PiiRedactions =
    [
        (new Regex(@"""client_full_name""\s*:\s*""[^""]*""", RegexOptions.Compiled), @"""client_full_name"":""[redacted]"""),
        (new Regex(@"""client_email""\s*:\s*""[^""]*""", RegexOptions.Compiled), @"""client_email"":""[redacted]"""),
        (new Regex(@"""client_phone""\s*:\s*""[^""]*""", RegexOptions.Compiled), @"""client_phone"":""[redacted]"""),
    ];

    // Breely's real payload can be large (CRM fields, signed-PDF blobs, raw form-answer dumps) - a
    // single log line with the whole thing could bloat the log file for one diagnostic capture, so
    // this caps it rather than logging megabytes. The AppLogService's own line-based format also
    // collapses embedded double quotes to single quotes when it writes the line - the JSON will look
    // slightly mangled in the viewer but field names/values are still readable.
    private static string RedactAndTruncate(string rawBody)
    {
        var redacted = rawBody;
        foreach (var (pattern, replacement) in PiiRedactions)
        {
            redacted = pattern.Replace(redacted, replacement);
        }

        return redacted.Length > MaxRawPayloadLogLength
            ? redacted[..MaxRawPayloadLogLength] + $"...[truncated, {redacted.Length} total chars]"
            : redacted;
    }
}
