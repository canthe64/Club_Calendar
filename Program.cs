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

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<GraphOptions>>().Value;
    var credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
    return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
});

builder.Services.AddSingleton<FacilityScheduler.Services.FacilityConfiguration>();
builder.Services.AddSingleton<FacilityScheduler.Services.SheetBookingService>();
builder.Services.AddSingleton<FacilityScheduler.Services.ClubEventService>();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<FacilityScheduler.Services.PublicAvailabilityService>();

// Temporary diagnostic buffer for WebhookCaptureEndpoint - see that file's doc comment. Remove once
// the real, signature-verified booking-notification endpoint replaces it.
builder.Services.AddSingleton<FacilityScheduler.Services.WebhookCaptureService>();

// The public availability endpoint is the app's only internet-anonymous surface (architecture
// doc §6.4) - rate-limited and CORS-scoped to just that one route, not applied globally.
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("public-api", limiterOptions =>
    {
        limiterOptions.PermitLimit = 60;
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

builder.Services.AddAuthorization(options =>
{
    // Secure by default: every page requires sign-in unless explicitly marked [AllowAnonymous].
    // The Phase 8 public availability endpoint will be that deliberate, explicit exception.
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
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
app.MapWebhookCaptureEndpoint();

app.Run();
