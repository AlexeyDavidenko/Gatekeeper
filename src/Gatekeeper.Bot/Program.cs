using Gatekeeper.Bot;
using Gatekeeper.Contracts;
using Gatekeeper.ServiceDefaults;
using Telegram.Bot;

var builder = Host.CreateApplicationBuilder(args);
builder.AddServiceDefaults();   // Aspire: OpenTelemetry, health, resilient HttpClient defaults

// One shared bot client for all communities.
builder.Services.AddSingleton<ITelegramBotClient>(_ =>
    new TelegramBotClient(builder.Configuration["Telegram:BotToken"]!));

// Typed client to the API. "http://api" is resolved by service discovery on the Aspire/Docker network.
builder.Services.AddHttpClient<IGatekeeperApiClient, GatekeeperApiClient>(http =>
{
    http.BaseAddress = new Uri("http://api");
    http.DefaultRequestHeaders.Add("X-Internal-Key", builder.Configuration["Internal:ApiKey"]!);
});

builder.Services.AddSingleton<TenantRouter>();        // applicant→tenant map for DM survey routing
builder.Services.AddSingleton<TranslationCache>();
builder.Services.AddHostedService<TelegramUpdateWorker>();  // inbound: long polling
builder.Services.AddHostedService<OutboxDrainWorker>();     // outbound: drains the transactional outbox
builder.Services.AddHostedService<TranslationCacheRefreshWorker>();

builder.Build().Run();
