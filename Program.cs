using System.Security.Claims;
using System.Threading.RateLimiting;
using Azure.Identity;
using FacilityScheduler;
using FacilityScheduler.Components;
using FacilityScheduler.Endpoints;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection(GraphOptions.SectionName));
builder.Services.Configure<FacilityOptions>(builder.Configuration.GetSection(FacilityOptions.SectionName));
builder.Services.Configure<PracticeIceOptions>(builder.Configuration.GetSection(PracticeIceOptions.SectionName));
builder.Services.Configure<StaffAccessOptions>(builder.Configuration.GetSection(StaffAccessOptions.SectionName));
builder.Services.Configure<AppLogOptions>(builder.Configuration.GetSection(AppLogOptions.SectionName));

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<GraphOptions>>().Value;
    var credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
    return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
});
builder.Services.AddSingleton<FacilityScheduler.Services.Graph.IGraphEventGateway, FacilityScheduler.Services.Graph.GraphEventGateway>();
builder.Services.AddSingleton<FacilityScheduler.Services.Graph.IGraphMailGateway, FacilityScheduler.Services.Graph.GraphMailGateway>();
builder.Services.AddSingleton<FacilityScheduler.Services.Graph.IGraphGroupGateway, FacilityScheduler.Services.Graph.GraphGroupGateway>();

builder.Services.AddSingleton<FacilityScheduler.Services.FacilityConfiguration>();
builder.Services.AddSingleton<FacilityScheduler.Services.AppLogService>();
builder.Services.AddSingleton<FacilityScheduler.Services.StaffAccessService>();
builder.Services.AddSingleton<FacilityScheduler.Services.SheetBookingService>();
builder.Services.AddSingleton<FacilityScheduler.Services.ClubEventService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<FacilityScheduler.Services.PublicAvailabilityService>();
builder.Services.AddSingleton<FacilityScheduler.Services.PracticeIceRequestService>();

builder.Services.AddSingleton<FacilityScheduler.Services.BreelyBookingProcessor>();

// The public availability endpoint is the app's only internet-anonymous surface (architecture
// doc §6.4) - rate-limited and CORS-scoped to just that one route, not applied globally.
builder.Services.AddRateLimiter(options =>
{
    // Default is 503 Service Unavailable - every doc/comment in this app describing rate-limit
    // behavior says 429 Too Many Requests, which is the semantically correct code for this and what
    // a well-behaved client (or Breely, on the webhook limiter) would actually check for.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindowLimiter("public-api", limiterOptions =>
    {
        limiterOptions.PermitLimit = 60;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });

    // Separate from public-api deliberately - a spam flood aimed at the booking webhook shouldn't
    // starve the calendar/widget/search endpoints, and vice versa. Generous relative to this club's
    // actual booking cadence (a handful a day at most), tight relative to what a scanner could throw
    // at an anonymous POST endpoint.
    options.AddFixedWindowLimiter("booking-webhook", limiterOptions =>
    {
        limiterOptions.PermitLimit = 30;
        limiterOptions.Window = TimeSpan.FromMinutes(1);
        limiterOptions.QueueLimit = 0;
    });
});

builder.Services.AddCors(options =>
{
    // Safe to allow any origin here specifically because this data is intentionally public and
    // anonymous - no cookies or credentials ever flow through this route. The club website (on a
    // different origin than this app) needs this for the embed widget's fetch to succeed.
    options.AddPolicy("public-api", policy => policy.AllowAnyOrigin().WithMethods("GET"));
});

// Staff sign-in via Entra ID for identity/audit purposes only (architecture doc S6.2 fallback).
// Graph calls stay on the app-only credential registered above - this is NOT the delegated
// on-behalf-of flow, deliberately, to avoid the added complexity of per-request token
// acquisition and incremental consent for a benefit (native Exchange attribution) the design
// already said was fine to skip.
builder.Services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApp(builder.Configuration.GetSection("AzureAd"));

// Debug-tier "authorizations" logging (architecture doc/Settings page) - chains onto whatever
// OnTokenValidated handler Microsoft.Identity.Web already wired up above (token cache population,
// etc.) rather than replacing it, so this only adds a log line and never interferes with sign-in
// itself. A no-op unless the Settings page's logging level is currently set to Debug.
//
// Also where the Staff role claim gets added (added for practice ice hosting, architecture doc
// D74/§5.4.4) - this app has no Entra-native way to do that without an Entra ID P1 license (group-
// based app-role assignment) or an Entra admin action per staff change (individual assignment), so
// StaffAccessService checks live Entra group membership here instead, at sign-in, and the ownership
// of that group can be delegated to non-admins.
builder.Services.Configure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, options =>
{
    var inner = options.Events.OnTokenValidated;
    options.Events.OnTokenValidated = async ctx =>
    {
        if (inner is not null)
        {
            await inner(ctx);
        }

        var appLog = ctx.HttpContext.RequestServices.GetRequiredService<FacilityScheduler.Services.AppLogService>();
        var name = ctx.Principal?.Identity?.Name ?? "unknown";
        await appLog.LogDebugAsync("StaffSignIn", name);

        // GetObjectId() is Microsoft.Identity.Web's own blessed helper for this - deliberately not
        // hand-rolling a claim-type lookup here the way the practice ice request page's host-name
        // resolution originally did, since that exact class of mistake (checking the wrong claim
        // first) already caused a live bug this session (D71).
        var objectId = ctx.Principal?.GetObjectId();
        if (!string.IsNullOrWhiteSpace(objectId) && ctx.Principal?.Identity is ClaimsIdentity identity)
        {
            var staffAccess = ctx.HttpContext.RequestServices.GetRequiredService<FacilityScheduler.Services.StaffAccessService>();
            if (await staffAccess.IsStaffAsync(objectId))
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, FacilityScheduler.Services.StaffAccessService.StaffRoleClaim));
            }
        }
    };
});

builder.Services.AddAuthorization(options =>
{
    // Secure by default, tightened from "any authenticated user" to "authenticated AND staff" once
    // practice ice hosting (§5.4.4) introduced member (non-staff) guest sign-in for the first time -
    // previously every guest in the tenant was staff/committee by the club's own existing process,
    // so "authenticated" and "staff" were the same set. They no longer are (architecture doc D74).
    var staffOnly = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole(FacilityScheduler.Services.StaffAccessService.StaffRoleClaim)
        .Build();
    options.FallbackPolicy = staffOnly;
    // Named so SettingsLogsEndpoint (a Minimal API endpoint, not covered by FallbackPolicy since it
    // already opts into authorization explicitly) can require the same thing without the two
    // definitions drifting apart.
    options.AddPolicy("StaffOnly", staffOnly);

    // The one deliberate carve-out: any signed-in user (staff or member) may submit a practice ice
    // request - the Staff-only default above would otherwise lock members out of the one page
    // meant for them (Components/Pages/PracticeIceRequest.razor).
    options.AddPolicy("AnyAuthenticatedUser", policy => policy.RequireAuthenticatedUser());
});
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddRazorPages()
    .AddMicrosoftIdentityUI();

var app = builder.Build();

// Force FacilityConfiguration to construct now, not lazily on first request - a misconfigured
// deployment (missing tenant domain/sheet mailboxes/timezone) should fail immediately and visibly
// at startup, not surface as a confusing error to the first user.
app.Services.GetRequiredService<FacilityScheduler.Services.FacilityConfiguration>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Clickjacking/MIME-sniffing hardening - previously sent on no response at all. X-Frame-Options and
// a frame-ancestors CSP are withheld only from /public/calendar, the one page deliberately built to
// be iframed on the club's own site (architecture doc §5.4.2); the staff app (Calendar, Settings,
// Diagnostics) and every other public surface (/public/search, the JSON API) has no reason to ever
// run inside another site's frame. X-Content-Type-Options applies to every response regardless.
app.Use(async (context, next) =>
{
    if (!context.Request.Path.StartsWithSegments("/public/calendar"))
    {
        context.Response.Headers.Append("X-Frame-Options", "DENY");
        context.Response.Headers.Append("Content-Security-Policy", "frame-ancestors 'none'");
    }
    context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
    await next();
});

app.UseCors();
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Static assets (CSS, JS, images) carry nothing sensitive - allow them anonymously rather than
// leaving them behind the global sign-in fallback policy. This was already true for every asset
// before Club Events/the public embed existed; it just went unnoticed because nothing anonymous
// needed to load one (the actual sign-in screen is hosted by Microsoft, not styled by this app).
app.MapStaticAssets().AllowAnonymous();
app.MapRazorPages();
// Do NOT call .AllowAnonymous() on this registration (tried once, reverted 2026-07-15):
// MapRazorComponents<App>() maps every routable component through one shared endpoint set, and
// ASP.NET Core's "AllowAnonymous always wins" rule means marking it anonymous here disables
// authorization for every page, not just an intended public one - live-verified to fully expose
// every staff page. Any future public/anonymous page belongs outside this component tree entirely
// (see PublicCalendarEndpoint/PublicAvailabilityEndpoints - plain endpoints, no Blazor circuit),
// not carved out of the shared circuit's own authorization.
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapPublicAvailabilityEndpoints();
app.MapPublicCalendarEndpoint();
app.MapPublicSearchEndpoint();
app.MapPracticeIcePublicEndpoint();
app.MapBreelyBookingWebhookEndpoint();
app.MapSettingsLogsEndpoint();

// Debug-tier only - lets a Settings-page reader see "the app restarted at X" without needing Azure
// portal access to the platform's own Activity Log. ApplicationStopping fires on a graceful
// shutdown (recycle, deploy swap, manual stop) with a short grace period to finish this write; it
// won't fire on a hard crash/OOM kill, so a missing "AppStopping" line doesn't rule that out.
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
var lifetimeAppLog = app.Services.GetRequiredService<FacilityScheduler.Services.AppLogService>();
lifetime.ApplicationStarted.Register(() => lifetimeAppLog.LogDebugAsync("AppStarted", "system").GetAwaiter().GetResult());
lifetime.ApplicationStopping.Register(() => lifetimeAppLog.LogDebugAsync("AppStopping", "system").GetAwaiter().GetResult());

app.Run();
