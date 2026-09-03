namespace FacilityScheduler.Endpoints;

/// <summary>
/// Shared "contact us" footer (operator request, 2026-09-04) - one literal string used identically by
/// every anonymous, hand-built-HTML public page (<see cref="PublicCalendarEndpoint"/>,
/// <see cref="PracticeIcePublicEndpoint"/>, <see cref="PublicSearchEndpoint"/>), so a future wording
/// or address change can't land on some of the three pages and not others. The staff-facing
/// equivalent (Calendar, Search, Settings) is <c>Components/Layout/PageFooter.razor</c> - a real
/// Blazor component there, since those pages render through the component tree rather than a
/// StringBuilder.
/// </summary>
internal static class PublicPageFooter
{
    internal const string Html = """
        <div style="margin-top:24px;padding-top:12px;border-top:1px solid #e7ecef;font-size:11px;color:#90a0ab">
            For problems or questions with this page, contact <a href="mailto:techcommittee@curlingseattle.org" style="color:#2d5f8a">Tech Committee.</a>
        </div>
        """;
}
