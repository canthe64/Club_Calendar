using System.Text;
using FacilityScheduler.Endpoints;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// Live-found via a real iframe embed (2026-09-03): with /public/calendar embedded per
/// docs/public-embed-instructions.md, clicking "Host practice ice" or "Search available ice" silently
/// failed - both links navigate *inside* the embedding site's iframe by default, landing on
/// /public/practice-ice or /public/search, and the security-headers middleware (Program.cs) sends
/// X-Frame-Options: DENY on every route except /public/calendar itself, so the browser refuses to
/// render either destination inside that existing frame. target="_top" is the fix: it breaks the
/// navigation out to the top-level page/tab instead, where framing is no longer a factor.
/// </summary>
public class PublicCalendarHeaderLinksTests
{
    [Fact]
    public void HostPracticeIceLink_CarriesTargetTop_SoItEscapesAnEmbeddingIframe()
    {
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendPageOpen(sb);

        var markup = sb.ToString();
        Assert.Contains("""<a href="/public/practice-ice" target="_top" """, markup);
    }

    [Fact]
    public void SearchAvailableIceLink_CarriesTargetTop_SoItEscapesAnEmbeddingIframe()
    {
        var sb = new StringBuilder();

        PublicCalendarEndpoint.AppendPageOpen(sb);

        var markup = sb.ToString();
        Assert.Contains("""<a href="/public/search" target="_top" """, markup);
    }
}
