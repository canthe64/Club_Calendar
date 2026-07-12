using Azure.Identity;
using FacilityScheduler;
using FacilityScheduler.Components;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.UI;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<GraphOptions>(builder.Configuration.GetSection(GraphOptions.SectionName));

builder.Services.AddSingleton(sp =>
{
    var options = sp.GetRequiredService<IOptions<GraphOptions>>().Value;
    var credential = new ClientSecretCredential(options.TenantId, options.ClientId, options.ClientSecret);
    return new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
});

builder.Services.AddSingleton<FacilityScheduler.Services.SheetBookingService>();

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

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorPages();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
