using System.Net;
using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// The anonymous "browse practice ice slots" page (docs/practice-ice-hosting-design.md §3.8) - the
/// slot list itself carries no member identity, so it stays anonymous and follows the same hard
/// rule as every other public surface (D15): a plain Minimal API endpoint, hand-built HTML, never a
/// Blazor component sharing the staff app's authenticated circuit. Each start link hands off to
/// /practice-ice/request, an authenticated Blazor page under the app's normal sign-in - member
/// identity only exists once that page's fallback authorization policy has run.
/// </summary>
public static class PracticeIcePublicEndpoint
{
    public static void MapPracticeIcePublicEndpoint(this WebApplication app)
    {
        app.MapGet("/public/practice-ice", async (PublicAvailabilityService service, FacilityConfiguration facility, CancellationToken ct) =>
        {
            var windows = await service.GetPracticeIceWindowsAsync(ct);
            var html = RenderPage(facility, windows);
            return Results.Content(html, "text/html; charset=utf-8");
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-api");
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    // internal, not private - reached directly by PracticeIcePublicEndpointTests (D60's precedent),
    // so this hand-built page's copy is testable without a full ASP.NET Core host.
    internal static string RenderPage(FacilityConfiguration facility, List<PublicAvailabilityWindow> windows)
    {
        var sb = new StringBuilder();

        sb.Append($"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Host Practice Ice</title>
            <link rel="icon" type="image/svg+xml" href="/favicon.svg">
            </head>
            <body style="font-family:-apple-system,'Segoe UI',Roboto,sans-serif;margin:0;color:#1e2a33">
            <header style="background:#1e2a33;color:#fff;padding:10px 24px;font-weight:600;font-size:14px;display:flex;align-items:center;gap:16px">
                <span>GCC Ice &amp; Event Calendar</span>
                <a href="/public/calendar" style="color:#a9c7e4;font-size:12px;font-weight:600;text-decoration:none;margin-left:auto">&#8249; Back to calendar</a>
            </header>
            <div style="padding:16px 24px;max-width:800px">
                <div style="font-size:16px;font-weight:600;color:#1e2a33;margin-bottom:4px">Host Practice Ice</div>
                <div style="font-size:13px;color:#90a0ab;margin-bottom:8px">
                    Any properly trained member can volunteer to host a practice ice session, open to all
                    members, at a time when no other activity is planned on any sheet. Sessions must be
                    requested at least {facility.PracticeIceMinLeadHours} hours in advance and are subject
                    to staff approval.
                </div>
                <div style="font-size:13px;font-weight:700;color:#1e2a33;margin-bottom:16px">
                    Pick a start time below to submit a request. You will be prompted to login with your
                    GCC user or guest account credentials.
                </div>
            """);

        sb.Append(RenderWindows(windows));

        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string RenderWindows(List<PublicAvailabilityWindow> windows)
    {
        if (windows.Count == 0)
        {
            return """
                <div style="font-size:12px;color:#90a0ab;border:1px dashed #d7dfe5;border-radius:8px;padding:20px;text-align:center">
                    No open times right now. Check back later - the ice calendar changes as bookings come and go.
                </div>
                """;
        }

        var sb = new StringBuilder();
        sb.Append("""<div style="display:flex;flex-direction:column;gap:14px">""");

        foreach (var day in windows.GroupBy(w => w.Start.Date).OrderBy(g => g.Key))
        {
            sb.Append($"""<div style="font-size:12px;font-weight:600;color:#1e2a33">{H(day.Key.ToString("dddd, MMMM d"))}</div>""");
            sb.Append("""<div style="display:flex;flex-wrap:wrap;gap:6px">""");

            foreach (var window in day)
            {
                foreach (var startTime in StartOptions(window))
                {
                    var link = $"/practice-ice/request?start={startTime:yyyy-MM-ddTHH:mm}";
                    sb.Append($"""
                        <a href="{link}" style="border:1px solid #e7ecef;border-radius:6px;padding:8px 12px;font-size:12px;color:#1e2a33;text-decoration:none;background:#fff">{H(startTime.ToString("h:mmtt"))}</a>
                        """);
                }
            }

            sb.Append("</div>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }

    // Every 30-minute boundary from which at least the shortest offerable session still fits before
    // the window ends - the same floor PracticeIceRules.DurationOptionsMinutes applies on the
    // request page, so a link is never offered here that would show no valid duration there.
    private static IEnumerable<DateTime> StartOptions(PublicAvailabilityWindow window)
    {
        for (var t = window.Start; t.AddMinutes(PracticeIceRules.MinSessionMinutes) <= window.End; t = t.AddMinutes(PracticeIceRules.SlotIntervalMinutes))
        {
            yield return t;
        }
    }
}
