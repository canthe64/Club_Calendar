using System.Text.RegularExpressions;
using FacilityScheduler.Endpoints;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The intro copy on /public/practice-ice. Operator-supplied wording isn't pinned here - only the parts
/// that read live configuration, and the contact addresses, where a typo would go unnoticed in use.
/// </summary>
public class PracticeIcePublicEndpointTests
{
    // Whitespace-collapsed - the copy wraps across source lines in the hand-built HTML.
    private static string RenderedText() =>
        Regex.Replace(PracticeIcePublicEndpoint.RenderPage(TestFacility.Create(), []), @"\s+", " ");

    [Fact]
    public void RenderPage_StatesTheConfiguredLeadTimeAndMinimumOpenSheets()
    {
        var facility = TestFacility.Create();

        var text = RenderedText();

        Assert.Contains($"Requests must be at least {facility.PracticeIceMinLeadHours} hours in advance.", text);
        Assert.Contains($"enough available sheets are available (minimum {facility.PracticeIceMinOpenSheets})", text);
    }

    [Fact]
    public void RenderPage_LinksBothContactAddresses()
    {
        var text = RenderedText();

        Assert.Contains("""<a href="mailto:practice@curlingseattle.org" """, text);
        Assert.Contains("""<a href="mailto:charlie@curlingseattle.org" """, text);
    }
}
