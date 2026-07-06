using Aspire.Hosting.ApplicationModel;

var builder = DistributedApplication.CreateBuilder(args);

builder.AddDockerComposeEnvironment("compose");

// Secrets/config that vary per deployment — supplied via user-secrets locally, env/secret-store in prod.
var internalApiKey = builder.AddParameter("internal-api-key", secret: true);
var botToken = builder.AddParameter("telegram-bot-token", secret: true);
var botUsername = builder.AddParameter("telegram-bot-username", secret: false);
var adminChatId = builder.AddParameter("telegram-admin-chat-id", secret: false);
var tenantId = builder.AddParameter("tenant-id", secret: false);
var tunnelToken = builder.AddParameter("cloudflare-tunnel-token", secret: true);

// Only the Postgres server + Catalog DB are modeled here — tenant DBs are provisioned at runtime
// (see runbook.md), so they don't have a fixed place in this topology.
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent);

var catalogDb = postgres.AddDatabase("catalog");

var api = builder.AddProject<Projects.Gatekeeper_Api>("api")
    .WithReference(catalogDb)
    .WaitFor(catalogDb)
    .WithEnvironment("Postgres__Server", postgres.Resource.ConnectionStringExpression)
    .WithEnvironment("Internal__ApiKey", internalApiKey);

// Worker: long polling outward, talks to the API over the private network. No public ingress.
var bot = builder.AddProject<Projects.Gatekeeper_Bot>("bot")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("Internal__ApiKey", internalApiKey)
    .WithEnvironment("Telegram__BotToken", botToken);

// The only service that needs to be reachable from the internet — via the cloudflared tunnel below.
var web = builder.AddProject<Projects.Gatekeeper_Web>("web")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("Internal__ApiKey", internalApiKey)
    .WithEnvironment("Telegram__BotToken", botToken)
    .WithEnvironment("Telegram__BotUsername", botUsername)
    .WithEnvironment("Telegram__AdminChatId", adminChatId)
    .WithEnvironment("Tenant__Id", tenantId)
    .WithEnvironment("Api__BaseUrl", "http://api");

builder.AddContainer("cloudflared", "cloudflare/cloudflared")
    .WithArgs("tunnel", "--no-autoupdate", "run")
    .WithEnvironment("TUNNEL_TOKEN", tunnelToken)
    .WaitFor(web);

builder.Build().Run();
