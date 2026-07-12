using Gatekeeper.Api;
using Gatekeeper.Application;
using Gatekeeper.Application.Applications;
using Gatekeeper.Application.Tenants;
using Gatekeeper.Infrastructure;
using Gatekeeper.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();   // Aspire: OpenTelemetry, health checks, resilient HttpClient defaults

// Tenancy
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
builder.Services.AddSingleton<ITenantConnectionFactory, TenantConnectionFactory>();
builder.Services.AddSingleton<IClock, SystemClock>();

// Catalog (control-plane) DB — fixed connection string.
builder.Services.AddDbContext<CatalogDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("catalog")));

// Tenant DB — connection resolved per request from the (middleware-populated) ITenantContext.
builder.Services.AddDbContext<TenantDbContext>((sp, o) =>
{
    var tenant = sp.GetRequiredService<ITenantContext>();
    var conn = sp.GetRequiredService<ITenantConnectionFactory>();
    if (tenant.IsResolved)
        o.UseNpgsql(conn.ForDatabase(tenant.DatabaseName));
});
builder.Services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<TenantDbContext>());

// Repositories + handlers
builder.Services.AddScoped<IApplicationRepository, ApplicationRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IQuestionRepository, QuestionRepository>();
builder.Services.AddScoped<IModerationRepository, ModerationRepository>();
builder.Services.AddScoped<ISiteVisitRepository, SiteVisitRepository>();
builder.Services.AddScoped<ITelegramCommandQueue, TelegramCommandQueue>();
builder.Services.AddScoped<ITenantDirectory, TenantDirectory>();
builder.Services.AddScoped<TenantDbContextFactory>();        // cross-tenant outbox drain
builder.Services.AddScoped<ITenantCatalogRepository, TenantCatalogRepository>();
builder.Services.AddScoped<ITenantProvisioner, TenantProvisioner>();
builder.Services.AddScoped<ProvisionTenantHandler>();
builder.Services.AddScoped<DecideApplicationHandler>();
builder.Services.AddScoped<CancelApplicationHandler>();
builder.Services.AddScoped<ToggleCardAnswersHandler>();
builder.Services.AddScoped<CreateApplicationHandler>();
builder.Services.AddScoped<SubmitAnswerHandler>();
builder.Services.AddScoped<StartSurveyHandler>();
builder.Services.AddScoped<ModerateUserHandler>();
builder.Services.AddScoped<ArchiveModerationActionsHandler>();
builder.Services.AddScoped<UnarchiveModerationActionHandler>();
builder.Services.AddScoped<CreateQuestionHandler>();
builder.Services.AddScoped<EditQuestionHandler>();
builder.Services.AddScoped<MoveQuestionHandler>();
builder.Services.AddScoped<DeactivateQuestionHandler>();
builder.Services.AddScoped<SetUserLanguageHandler>();
builder.Services.AddScoped<RecordSiteVisitHandler>();

var app = builder.Build();

using (var migrationScope = app.Services.CreateScope())
{
    var catalog = migrationScope.ServiceProvider.GetRequiredService<CatalogDbContext>();
    var tenantFactory = migrationScope.ServiceProvider.GetRequiredService<TenantDbContextFactory>();
    await MigrationRunner.MigrateAllAsync(catalog, tenantFactory);
}

app.MapDefaultEndpoints();                       // /health, /alive (Aspire)
app.UseMiddleware<InternalApiKeyMiddleware>();   // service-to-service auth on the private network
app.UseMiddleware<TenantContextMiddleware>();    // resolves X-Tenant-Id -> ITenantContext

app.MapApplicationsEndpoints();
app.MapModerationEndpoints();
app.MapInternalEndpoints();                       // tenant resolve + outbox drain (tenant-agnostic)
app.MapQuestionsEndpoints();
app.MapDashboardEndpoints();
app.MapSiteVisitsEndpoints();

app.Run();
