using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The intro copy on /public/practice-ice - operator-supplied rewrite, 2026-09-24: replaced the
/// short two-paragraph intro with a fuller walkthrough (a responsibilities-info email, a numbered
/// step list with a lettered sub-list for the login/guest-account detail, and a closing contact
/// line), superseding the earlier D115 call-to-action wording this test file used to pin.
/// </summary>
public class PracticeIcePublicEndpointTests
{
    [Fact]
    public void RenderPage_MentionsTheLeadTimeRequirement()
    {
        var facility = TestFacility.Create();

        var html = PracticeIcePublicEndpoint.RenderPage(facility, []);

        Assert.Contains($"requested at least {facility.PracticeIceMinLeadHours} hours in advance", html);
    }

    [Fact]
    public void RenderPage_LinksToTheResponsibilitiesEmail()
    {
        var facility = TestFacility.Create();

        var html = PracticeIcePublicEndpoint.RenderPage(facility, []);

        Assert.Contains("""<a href="mailto:practice@curlingseattle.org" """, html);
        Assert.Contains("learn the responsibilities", html);
    }

    [Fact]
    public void RenderPage_ListsTheThreeVolunteerSteps()
    {
        var facility = TestFacility.Create();

        var html = PracticeIcePublicEndpoint.RenderPage(facility, []);

        Assert.Contains("Select an available time slot from the list below", html);
        Assert.Contains("Select the length of time you'll host practice ice (minimum", html);
        Assert.Contains("""Click "Submit Request"</li>""", html);
    }

    [Fact]
    public void RenderPage_GuestAccountSubStep_LinksToCharlieForAGuestAccount()
    {
        var facility = TestFacility.Create();

        var html = PracticeIcePublicEndpoint.RenderPage(facility, []);

        Assert.Contains("if you have a \"guest\" account", html);
        Assert.Contains("""<a href="mailto:charlie@curlingseattle.org" """, html);
    }

    [Fact]
    public void RenderPage_DescribesCalendarTeamReview()
    {
        var facility = TestFacility.Create();

        var html = PracticeIcePublicEndpoint.RenderPage(facility, []);

        Assert.Contains("reviewed by the Calendar team to avoid conflicts.", html);
    }
}
