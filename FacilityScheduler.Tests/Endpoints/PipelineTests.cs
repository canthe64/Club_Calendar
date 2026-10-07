using System.Net;
using FacilityScheduler.Tests.TestSupport;

namespace FacilityScheduler.Tests.Endpoints;

/// <summary>
/// The request pipeline as Program.cs wires it: which routes are anonymous, member-only or
/// staff-only, the clickjacking headers (withheld only from the embeddable public calendar), CORS
/// on the JSON API, and the rate limiters. Unit tests evaluate each policy object on its own; these
/// catch a route bound to the wrong one, or middleware in the wrong order (the D75 lockout class).
/// </summary>
public class PipelineTests : IClassFixture<AppFactory>
{
    private readonly AppFactory app;

    public PipelineTests(AppFactory app)
    {
        this.app = app;
        // The log download answers 404 until a log file exists.
        app.EnsureLogFileAsync().GetAwaiter().GetResult();
    }

    public static TheoryData<string> PublicRoutes =>
    [
        "/public/calendar",
        "/public/calendar.ics",
        "/public/search",
        "/public/practice-ice",
        "/public/make-up-game",
        "/api/public/availability",
    ];

    [Theory]
    [MemberData(nameof(PublicRoutes))]
    public async Task PublicRoutes_AnswerAnonymousCallers(string path)
    {
        var response = await app.ClientFor().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/settings")]
    [InlineData("/practice-ice/approvals")]
    [InlineData("/club-events")]
    [InlineData("/settings/logs/download")]
    [InlineData("/search/export.csv?q=league")]
    public async Task StaffRoutes_ChallengeAnonymous_ForbidMembers_AdmitStaff(string path)
    {
        var anonymous = await app.ClientFor().GetAsync(path);
        var member = await app.ClientFor("member").GetAsync(path);
        var staff = await app.ClientFor("staff").GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.True(anonymous.Headers.Contains(TestAuthHandler.ChallengedHeader));
        Assert.Equal(HttpStatusCode.Forbidden, member.StatusCode);
        Assert.Equal(HttpStatusCode.OK, staff.StatusCode);
    }

    [Theory]
    [InlineData("/practice-ice/request")]
    [InlineData("/make-up-game/request")]
    [InlineData("/my-booking/cancel")]
    public async Task MemberRoutes_ChallengeAnonymous_AdmitAnySignedInMember(string path)
    {
        var anonymous = await app.ClientFor().GetAsync(path);
        var member = await app.ClientFor("member").GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("member")]
    [InlineData("staff")]
    public async Task AMistypedAddress_IsANotFoundPageForEveryone(string? user)
    {
        var response = await app.ClientFor(user).GetAsync("/public/calender");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("does not exist", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task BreelyWebhook_IsNotBehindSignIn_ButRejectsAWrongSecret()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/breely") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Webhook-Secret", "wrong");

        var response = await app.ClientFor().SendAsync(request);

        // The endpoint's own secret check answered, not the sign-in challenge.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.False(response.Headers.Contains(TestAuthHandler.ChallengedHeader));
    }

    [Fact]
    public async Task OnlyThePublicCalendar_CanBeFramedByAnotherSite()
    {
        var calendar = await app.ClientFor().GetAsync("/public/calendar");
        Assert.False(calendar.Headers.Contains("X-Frame-Options"));
        Assert.Equal("nosniff", Assert.Single(calendar.Headers.GetValues("X-Content-Type-Options")));

        foreach (var (path, user) in new[] { ("/public/search", (string?)null), ("/public/calendar.ics", null), ("/", "staff") })
        {
            var response = await app.ClientFor(user).GetAsync(path);
            Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
            Assert.Contains("frame-ancestors 'none'", response.Headers.GetValues("Content-Security-Policy"));
        }
    }

    [Fact]
    public async Task OnlyTheJsonApi_AllowsCrossOriginReads()
    {
        async Task<HttpResponseMessage> FromClubSite(string path)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("Origin", "https://www.curlingseattle.org");
            return await app.ClientFor().SendAsync(request);
        }

        var api = await FromClubSite("/api/public/availability");
        var page = await FromClubSite("/public/search");

        Assert.Equal("*", Assert.Single(api.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(page.Headers.Contains("Access-Control-Allow-Origin"));
    }
}

/// <summary>Rate limits need their own app instance: the windows are process-wide, so sharing one
/// with the tests above would make both order-dependent.</summary>
public class PipelineRateLimitTests
{
    [Fact]
    public async Task PublicPages_AreLimitedTo150AMinute_WithoutStarvingTheCalendarFeed()
    {
        await using var app = new AppFactory();
        var client = app.ClientFor();

        for (var i = 0; i < 150; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/public/availability")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/public/calendar")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/public/calendar.ics")).StatusCode);
    }

    [Fact]
    public async Task StaffExports_AreLimitedTo10AMinute()
    {
        await using var app = new AppFactory();
        await app.EnsureLogFileAsync();
        var client = app.ClientFor("staff");

        for (var i = 0; i < 10; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/settings/logs/download")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/search/export.csv?q=league")).StatusCode);
    }

    [Fact]
    public async Task BreelyWebhook_IsLimitedTo30AMinute_AndSaysSoWith429()
    {
        await using var app = new AppFactory();
        var client = app.ClientFor();
        Task<HttpResponseMessage> Post()
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/webhooks/breely") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Webhook-Secret", "wrong");
            return client.SendAsync(request);
        }

        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await Post()).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post()).StatusCode);
    }
}
