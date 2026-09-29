using System.Net;
using System.Text;
using FacilityScheduler.Domain;
using FacilityScheduler.Services;

namespace FacilityScheduler.Endpoints;

/// <summary>
/// The anonymous "find a make-up game time" page (staff request 2026-09-28). Same shape and same
/// hard rule as /public/practice-ice (D15): a plain Minimal API endpoint with hand-built HTML, never
/// a Blazor component. Each time links to /make-up-game/request, the authenticated page where the
/// member signs in, reviews the slot, and acknowledges the policy.
/// </summary>
public static class MakeUpGamePublicEndpoint
{
    public static void MapMakeUpGamePublicEndpoint(this WebApplication app)
    {
        app.MapGet("/public/make-up-game", async (PublicAvailabilityService service, FacilityConfiguration facility, CancellationToken ct) =>
        {
            var options = await service.GetMakeUpGameOptionsAsync(ct);
            return Results.Content(RenderPage(facility, options), "text/html; charset=utf-8");
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-api");
    }

    private static string H(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);

    // internal, not private - reached directly by MakeUpGamePublicEndpointTests (D60's precedent).
    internal static string RenderPage(FacilityConfiguration facility, List<MakeUpGameOption> options)
    {
        var sb = new StringBuilder();

        sb.Append($"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1.0">
            <title>Schedule a Make-Up Game</title>
            <link rel="icon" type="image/svg+xml" href="/favicon.svg">
            </head>
            <body style="font-family:-apple-system,'Segoe UI',Roboto,sans-serif;margin:0;color:#1e2a33">
            <header style="background:#1e2a33;color:#fff;padding:10px 24px;font-weight:600;font-size:14px;display:flex;align-items:center;gap:16px">
                <span>GCC Ice &amp; Event Calendar</span>
                <a href="/public/calendar" style="color:#a9c7e4;font-size:12px;font-weight:600;text-decoration:none;margin-left:auto">&#8249; Back to calendar</a>
            </header>
            <div style="padding:16px 24px;max-width:800px">
                <div style="font-size:16px;font-weight:600;color:#1e2a33;margin-bottom:4px">Schedule a Make-Up Game</div>
                <div style="font-size:13px;color:#90a0ab;margin-bottom:8px">
                    From this page you can schedule a sheet for a league make-up game. Make-up game sessions
                    are {MakeUpGameRules.DurationMinutes / 60} hours long and are played on an open sheet while other ice is
                    already in use. A request is for a single sheet/game.
                </div>
                <div style="font-size:13px;color:#90a0ab;margin-bottom:8px">
                    Please <strong>do not</strong> use this tool for any other purpose than scheduling a single
                    make-up game. If you wish to reserve practice ice, please visit the
                    <a href="/public/practice-ice" style="color:#2d5f8a">Host Practice Ice</a> page.
                </div>
                <div style="font-size:13px;color:#90a0ab;margin-bottom:8px">To schedule a make-up game:</div>
                <ol style="font-size:13px;color:#90a0ab;margin:0 0 8px;padding-left:20px">
                    <li>Select an available time slot from the list below
                        <ol type="a" style="margin:4px 0 0;padding-left:20px">
                            <li>You will be prompted to login to continue &ndash; you can use either your GCC
                                email or your personal email if you have a "guest" account.</li>
                            <li>Contact Charlie at
                                <a href="mailto:charlie@curlingseattle.org" style="color:#2d5f8a">charlie@curlingseattle.org</a>
                                if you need a guest account</li>
                        </ol>
                    </li>
                    <li style="margin-top:6px">Click "Submit Request"</li>
                </ol>
                <div style="font-size:13px;color:#90a0ab;margin-bottom:8px">
                    Requests must be at least {facility.PracticeIceMinLeadHours} hours in advance and no more than
                    {facility.PracticeIceMaxHorizonDays} days out. Pick a start time below - you'll sign in, review the
                    details, and confirm. Your game is added to the calendar right away.
                </div>
                <div style="font-size:13px;color:#90a0ab;margin-bottom:16px">
                    If there is not a suitable time slot listed, check if someone on your teams are qualified to
                    host practice ice! They can offer to host practice ice and you can then use one of those
                    sheets for your game, plus they'll earn volunteer hours and contribute to the club!
                </div>
            """);

        sb.Append(RenderOptions(options));
        sb.Append(PublicPageFooter.Html);
        sb.Append("</div></body></html>");
        return sb.ToString();
    }

    private static string RenderOptions(List<MakeUpGameOption> options)
    {
        if (options.Count == 0)
        {
            return """
                <div style="font-size:12px;color:#90a0ab;border:1px dashed #d7dfe5;border-radius:8px;padding:20px;text-align:center">
                    No make-up game times right now. Check back later - the ice calendar changes as bookings come and go.
                </div>
                """;
        }

        var sb = new StringBuilder();
        sb.Append("""<div style="display:flex;flex-direction:column;gap:14px">""");

        foreach (var day in options.GroupBy(o => o.Start.Date).OrderBy(g => g.Key))
        {
            sb.Append($"""<div style="font-size:12px;font-weight:600;color:#1e2a33">{H(day.Key.ToString("dddd, MMMM d"))}</div>""");
            sb.Append("""<div style="display:flex;flex-wrap:wrap;gap:6px">""");

            foreach (var option in day.OrderBy(o => o.Start))
            {
                var link = $"/make-up-game/request?start={option.Start:yyyy-MM-ddTHH:mm}";
                sb.Append($"""
                    <a href="{link}" style="border:1px solid #e7ecef;border-radius:6px;padding:8px 12px;font-size:12px;color:#1e2a33;text-decoration:none;background:#fff">{H(option.Start.ToString("h:mmtt"))}<span style="color:#90a0ab"> · {H(CalendarStyles.SheetLabel(option.SheetMailbox))}</span></a>
                    """);
            }

            sb.Append("</div>");
        }

        sb.Append("</div>");
        return sb.ToString();
    }
}
