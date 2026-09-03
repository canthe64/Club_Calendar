using System.Text;
using FacilityScheduler.Endpoints;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The shared "contact Tech Committee" footer (operator request, 2026-09-04), covering all three
/// anonymous, hand-built-HTML public pages at once - they all render the exact same literal string
/// (<see cref="PublicPageFooter.Html"/>), so one test class per page would just be the same assertion
/// three times. The staff-facing pages (Calendar, Search, Settings) that carry the identical line are
/// covered separately (bUnit, since PageFooter.razor is a real component) where each page's own tests
/// already live.
/// </summary>
public class PublicPageFooterTests
{
    private const string ExpectedLink = """<a href="mailto:techcommittee@curlingseattle.org" """;
    private const string ExpectedText = "For problems or questions with this page, contact";

    [Fact]
    public void PublicCalendarEndpoint_PageClose_CarriesTheFooter()
    {
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendPageClose(sb);

        var markup = sb.ToString();
        Assert.Contains(ExpectedText, markup);
        Assert.Contains(ExpectedLink, markup);
    }

    [Fact]
    public void PracticeIcePublicEndpoint_RenderPage_CarriesTheFooter()
    {
        var facility = TestFacility.Create();

        var html = PracticeIcePublicEndpoint.RenderPage(facility, []);

        Assert.Contains(ExpectedText, html);
        Assert.Contains(ExpectedLink, html);
    }

    [Fact]
    public void PublicSearchEndpoint_RenderPage_CarriesTheFooter()
    {
        var html = PublicSearchEndpoint.RenderPage(DateTime.Today, DateTime.Today.AddDays(7), minSheets: 1, maxSheets: 3, windows: []);

        Assert.Contains(ExpectedText, html);
        Assert.Contains(ExpectedLink, html);
    }
}
