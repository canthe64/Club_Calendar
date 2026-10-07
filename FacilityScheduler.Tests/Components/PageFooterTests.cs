using Bunit;
using FacilityScheduler.Components.Pages;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Components;

/// <summary>
/// The shared "contact Tech Committee" footer (operator request, 2026-09-04). The staff pages carry it
/// through one PageFooter.razor component, so one page proves the wiring; the anonymous pages use
/// PublicPageFooter.Html (PublicPageFooterTests).
/// </summary>
public class PageFooterTests : BunitContext
{
    [Fact]
    public void StaffCalendar_ShowsTheFooter()
    {
        StaffPageServices.Register(this);

        var cut = Render<Calendar>();

        Assert.Contains("For problems or questions with this page, contact", cut.Markup);
        Assert.Contains("""<a href="mailto:techcommittee@curlingseattle.org" """, cut.Markup);
    }
}
