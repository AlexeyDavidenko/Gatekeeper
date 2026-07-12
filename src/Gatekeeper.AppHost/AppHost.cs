using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Docker.Resources.ServiceNodes;

var builder = DistributedApplication.CreateBuilder(args);

// The auto-added Aspire dashboard has no auth token configured for docker-compose publish, which
// would otherwise land a telemetry UI on an unauthenticated host port on the production server.
// Not part of what this deployment needs — disabled rather than left open.
builder.AddDockerComposeEnvironment("compose").WithDashboard(enabled: false);

// Secrets/config that vary per deployment — supplied via user-secrets locally, env/secret-store in prod.
var internalApiKey = builder.AddParameter("internal-api-key", secret: true);
var botToken = builder.AddParameter("telegram-bot-token", secret: true);
var botUsername = builder.AddParameter("telegram-bot-username", secret: false);
var adminChatId = builder.AddParameter("telegram-admin-chat-id", secret: false);
var tenantId = builder.AddParameter("tenant-id", secret: false);
var tunnelToken = builder.AddParameter("cloudflare-tunnel-token", secret: true);
var minioRootUser = builder.AddParameter("minio-root-user", secret: false);
var minioRootPassword = builder.AddParameter("minio-root-password", secret: true);

// Only the Postgres server + Catalog DB are modeled here — tenant DBs are provisioned at runtime
// (see runbook.md), so they don't have a fixed place in this topology.
// Image pinned to 16 to match what's actually been verified (migrations, pg_trgm) — Aspire's
// current default tag is newer and untested here.
var postgres = builder.AddPostgres("postgres")
    .WithImageTag("16")
    .WithDataVolume()
    .WithLifetime(ContainerLifetime.Persistent)
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Restart = "unless-stopped";
        service.Healthcheck = new Healthcheck
        {
            Test = ["CMD-SHELL", "pg_isready -U postgres"],
            Interval = "5s",
            Timeout = "5s",
            Retries = 5,
            StartPeriod = "5s",
        };
    });

var catalogDb = postgres.AddDatabase("catalog");

// Screenshot-evidence storage for moderation actions (Ban/Mute/Kick/Warn) — file lives here, only
// the path + SHA-256 hash go in Postgres. Console (9001) isn't tunneled/published anywhere; only
// the S3 API port (9000) needs to be reachable, and only by api, over the private compose network.
var minio = builder.AddContainer("minio", "minio/minio")
    .WithArgs("server", "/data", "--console-address", ":9001")
    .WithEnvironment("MINIO_ROOT_USER", minioRootUser)
    .WithEnvironment("MINIO_ROOT_PASSWORD", minioRootPassword)
    .WithVolume("minio-data", "/data")
    .WithLifetime(ContainerLifetime.Persistent)
    .PublishAsDockerComposeService((_, service) => service.Restart = "unless-stopped");

var api = builder.AddProject<Projects.Gatekeeper_Api>("api")
    .WithReference(catalogDb)
    .WaitFor(catalogDb)
    .WaitFor(minio)
    .WithEnvironment("Postgres__Server", postgres.Resource.ConnectionStringExpression)
    .WithEnvironment("Internal__ApiKey", internalApiKey)
    .WithEnvironment("Minio__Endpoint", "minio:9000")
    .WithEnvironment("Minio__AccessKey", minioRootUser)
    .WithEnvironment("Minio__SecretKey", minioRootPassword)
    .WithEnvironment("Minio__Bucket", "evidence")
    .PublishAsDockerComposeService((_, service) =>
    {
        service.Restart = "unless-stopped";
        // Wait for Postgres to actually accept connections, not just for its container to start —
        // MigrationRunner connects immediately on api's own startup.
        if (service.DependsOn.TryGetValue("postgres", out var dependsOnPostgres))
            dependsOnPostgres.Condition = "service_healthy";
    });

// Worker: long polling outward, talks to the API over the private network. No public ingress.
var bot = builder.AddProject<Projects.Gatekeeper_Bot>("bot")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("Internal__ApiKey", internalApiKey)
    .WithEnvironment("Telegram__BotToken", botToken)
    .PublishAsDockerComposeService((_, service) => service.Restart = "unless-stopped");

// The only service that needs to be reachable from the internet — via the cloudflared tunnel below.
var web = builder.AddProject<Projects.Gatekeeper_Web>("web")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("Internal__ApiKey", internalApiKey)
    .WithEnvironment("Telegram__BotToken", botToken)
    .WithEnvironment("Telegram__BotUsername", botUsername)
    .WithEnvironment("Telegram__AdminChatId", adminChatId)
    .WithEnvironment("Tenant__Id", tenantId)
    .WithEnvironment("Api__BaseUrl", "http://api")
    .PublishAsDockerComposeService((_, service) => service.Restart = "unless-stopped");

builder.AddContainer("cloudflared", "cloudflare/cloudflared")
    .WithArgs("tunnel", "--no-autoupdate", "run")
    .WithEnvironment("TUNNEL_TOKEN", tunnelToken)
    .WaitFor(web)
    .PublishAsDockerComposeService((_, service) => service.Restart = "unless-stopped");

builder.Build().Run();
