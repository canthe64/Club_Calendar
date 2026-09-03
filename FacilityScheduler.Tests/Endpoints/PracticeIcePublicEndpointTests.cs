using FacilityScheduler.Domain;
using FacilityScheduler.Endpoints;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The intro copy on /public/practice-ice - operator request, 2026-09-03: the "pick a start time"
/// call-to-action was buried mid-paragraph and easy to miss; it's now its own bold line, with wording
/// that also sets expectations about the sign-in prompt on the next page.
/// </summary>
public class PracticeIcePublicEndpointTests
{
    [Fact]
    public void RenderPage_CallToAction_IsItsOwnBoldLine_WithTheUpdatedWording()
    {
        var facility = TestFacility.Create();

        var html = PracticeIcePublicEndpoint.RenderPage(facility, []);

        Assert.Contains(
            """<div style="font-size:13px;font-weight:700;color:#1e2a33;margin-bottom:16px">""",
            html);
        Assert.Contains(
            "Pick a start time below to submit a request. You will be prompted to login with your",
            html);
        Assert.Contains("GCC user or guest account credentials.", html);
    }

    [Fact]
    public void RenderPage_CallToAction_IsSeparateFromTheExplanatoryParagraphAbove()
    {
        // Two distinct blocks, not one run-on paragraph - the explanatory sentence about who can host
        // and the lead-time requirement stays in its own (non-bold) line above the call to action.
        var facility = TestFacility.Create();

        var html = PracticeIcePublicEndpoint.RenderPage(facility, []);

        Assert.Contains("subject", html);
        Assert.Contains("to staff approval.", html);
        Assert.DoesNotContain("to staff approval. Pick a start time", html);
    }
}
