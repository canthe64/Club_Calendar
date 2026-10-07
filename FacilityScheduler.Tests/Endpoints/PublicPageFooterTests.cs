using System.Text;
using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The shared "contact Tech Committee" footer on the anonymous, hand-built-HTML pages - they all render
/// the same literal (<see cref="PublicPageFooter.Html"/>), so one page proves the wiring. The staff
/// pages are covered by PageFooterTests.
/// </summary>
public class PublicPageFooterTests
{
    [Fact]
    public void PublicCalendar_PageClose_CarriesTheFooter()
    {
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendPageClose(sb);

        Assert.Contains("For problems or questions with this page, contact", sb.ToString());
        Assert.Contains("""<a href="mailto:techcommittee@curlingseattle.org" """, sb.ToString());
    }
}
