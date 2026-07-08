using Gatekeeper.Web;
using Gatekeeper.Web.Auth;
using Gatekeeper.Web.Components;
using Gatekeeper.Web.Endpoints;
using Gatekeeper.Web.Services;
using Gatekeeper.ServiceDefaults;
using Microsoft.AspNetCore.Authentication.Cookies;
using Telegram.Bot;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();   // Aspire: OTel, health checks, and — critically — HTTP service discovery + resilience

builder.Services.AddRazorComponents();               // static SSR (no interactive render modes)
builder.Services.AddCascadingAuthenticationState();  // exposes HttpContext.User to components

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

// One shared bot client — used only for the live getChatMember admin check.
builder.Services.AddSingleton<ITelegramBotClient>(_ =>
    new TelegramBotClient(builder.Configuration["Telegram:BotToken"]!));
builder.Services.AddScoped<TelegramAdminChecker>();

// Typed client to the API. Reachable on the private Docker network; carries the internal key.
builder.Services.AddHttpClient<AdminApiClient>(http =>
{
    http.BaseAddress = new Uri(builder.Configuration["Api:BaseUrl"] ?? "http://api");
    http.DefaultRequestHeaders.Add("X-Internal-Key", builder.Configuration["Internal:ApiKey"]!);
});

// Visitor log: request pipeline hands off to a bounded in-memory queue, drained by a background
// worker — see SiteVisitLogging.cs for why this must never sit on the request-serving path.
builder.Services.AddSingleton<SiteVisitQueue>();
builder.Services.AddHostedService<SiteVisitFlushWorker>();

var app = builder.Build();

// Without this, an unhandled exception anywhere in the pipeline (e.g. a routing conflict, a bad
// API call) renders as a blank white page in Production — no Development exception page, and
// nothing here to catch it and show something. Error.razor already existed but was never wired up.
app.UseExceptionHandler("/Error", createScopeForErrors: true);

app.UseStaticFiles();          // wwwroot/* (localtime.js, app.css) — was missing entirely, so these 404'd
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<SiteVisitMiddleware>();   // after auth (needs ctx.User); static files never reach it
app.UseAntiforgery();

app.MapWebEndpoints();            // /auth/* and /applications/* form posts
app.MapRazorComponents<App>();

app.Run();
