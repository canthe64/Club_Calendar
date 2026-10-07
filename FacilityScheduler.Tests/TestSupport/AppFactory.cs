using System.Security.Claims;
using System.Text.Encodings.Web;
using FacilityScheduler.Services;
using FacilityScheduler.Services.Graph;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FacilityScheduler.Tests.TestSupport;

/// <summary>The real Program.cs pipeline (middleware order, endpoint auth bindings, rate limiters,
/// security headers) hosted in memory. Graph is replaced by the in-memory fakes, and Entra sign-in
/// by <see cref="TestAuthHandler"/>, so no tenant or secret is needed. Runs as environment "Test",
/// which skips appsettings.Development.json and user secrets.</summary>
public sealed class AppFactory : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "test-webhook-secret";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Test");

        var settings = new Dictionary<string, string?>
        {
            ["AllowedHosts"] = "localhost",
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000002",
            ["AzureAd:Domain"] = TestFacility.TenantDomain,
            ["Graph:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["Graph:ClientId"] = "00000000-0000-0000-0000-000000000002",
            ["Facility:TenantDomain"] = TestFacility.TenantDomain,
            ["Facility:ClubEventsMailboxLocalPart"] = TestFacility.ClubEventsLocalPart,
            ["Facility:TimeZone"] = TestFacility.TimeZoneId,
            ["Facility:PublicBaseUrl"] = TestFacility.PublicBaseUrl,
            ["StaffAccess:StaffGroupId"] = TestFacility.StaffGroupId,
            ["AppLog:LogDirectory"] = TestTempDirectory.Create(),
            ["Webhook:BreelySharedSecret"] = WebhookSecret,
        };
        for (var i = 0; i < TestFacility.SheetLocalParts.Length; i++)
        {
            settings[$"Facility:SheetMailboxLocalParts:{i}"] = TestFacility.SheetLocalParts[i];
        }
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IGraphEventGateway>();
            services.RemoveAll<IGraphMailGateway>();
            services.RemoveAll<IGraphGroupGateway>();
            services.AddSingleton<IGraphEventGateway>(sp =>
                new FakeGraphEventGateway(sp.GetRequiredService<FacilityConfiguration>().ZoneInfo));
            services.AddSingleton<IGraphMailGateway, FakeGraphMailGateway>();
            services.AddSingleton<IGraphGroupGateway, FakeGraphGroupGateway>();

            services.AddAuthentication(options =>
                {
                    options.DefaultScheme = TestAuthHandler.SchemeName;
                    options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                    options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    options.DefaultForbidScheme = TestAuthHandler.SchemeName;
                    options.DefaultSignInScheme = TestAuthHandler.SchemeName;
                })
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        });
    }

    /// <summary>Writes one app-log line, so the Settings log download has a file to serve.</summary>
    public Task EnsureLogFileAsync() =>
        Services.GetRequiredService<AppLogService>().LogActionAsync("PipelineTest", "tester", null, null, "seed");

    /// <summary>A client for the given caller: null (anonymous), "member" (signed in, not staff) or
    /// "staff". Redirects aren't followed, so a challenge shows up as itself.</summary>
    public HttpClient ClientFor(string? user = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        if (user is not null)
        {
            client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, user);
        }
        return client;
    }
}

/// <summary>Signs the caller in from a request header instead of Entra. A challenge answers 401 and
/// marks the response, so a test can tell "the auth layer turned this away" from an endpoint's own
/// 401 (the Breely webhook's secret check).</summary>
public sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string UserHeader = "X-Test-User";
    public const string ChallengedHeader = "X-Test-Challenged";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var user = Request.Headers[UserHeader].ToString();
        if (user is not ("member" or "staff"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        List<Claim> claims =
        [
            new("name", $"Test {user}"),
            new("preferred_username", $"{user}@test.example")
        ];
        if (user == "staff")
        {
            claims.Add(new Claim(StaffAccessService.StaffClaimType, StaffAccessService.StaffClaimValue));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", "roles"));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers[ChallengedHeader] = "1";
        return Task.CompletedTask;
    }
}
