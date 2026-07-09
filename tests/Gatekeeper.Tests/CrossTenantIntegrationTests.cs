using System.Text.Json;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using Gatekeeper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainApplication = Gatekeeper.Domain.Application;

namespace Gatekeeper.Tests;

/// <summary>
/// Integration tests for the tenant-agnostic /internal/* endpoints in Endpoints.cs
/// (InternalEndpoints — "/applications/in-progress", "/applications/by-user/{id}",
/// "/telegram-commands/pending", "/telegram-commands/{tenantId}/{commandId}/result"), each of which
/// loops over every active row in CatalogDbContext.Tenants and fans out to that tenant's own
/// database via TenantDbContextFactory. Query/loop logic is intentionally duplicated from
/// Endpoints.cs rather than extracted into a shared method, per project convention for read
/// queries (see docs/conventions.md "Как добавить чтение (query)") — these endpoints in particular
/// have no reusable method to call short of booting the full HTTP host, which is out of scope.
///
/// The one thing a single-tenant test (or a fake ITenantConnectionFactory) can't prove: that the
/// loop actually reaches into MULTIPLE real, independently migrated databases and merges results
/// correctly — so every test here seeds at least two real tenant databases via
/// PostgresFixture.CreateTenantDbAsync(), registered in one real Catalog database via
/// PostgresFixture.CreateCatalogDbAsync().
/// </summary>
[Collection("Postgres")]
public sealed class CrossTenantIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task<(CatalogDbContext Catalog, TenantDbContext TenantA, TenantDbContext TenantB)> SeedTwoTenantsAsync()
    {
        var catalog = await fixture.CreateCatalogDbAsync();
        var tenantA = await fixture.CreateTenantDbAsync();
        var tenantB = await fixture.CreateTenantDbAsync();

        var databaseNameA = tenantA.Database.GetDbConnection().Database;
        var databaseNameB = tenantB.Database.GetDbConnection().Database;

        catalog.Tenants.Add(Tenant.Create("tenant-a", "Tenant A", databaseNameA, TestNow));
        catalog.Tenants.Add(Tenant.Create("tenant-b", "Tenant B", databaseNameB, TestNow));
        await catalog.SaveChangesAsync();

        return (catalog, tenantA, tenantB);
    }

    [Fact]
    public async Task InProgress_Scans_All_Active_Tenants_And_Only_Returns_SurveyOffered_Or_InSurvey()
    {
        var (catalog, tenantA, tenantB) = await SeedTwoTenantsAsync();

        // Tenant A: one InSurvey application — should show up.
        var inSurveyApp = DomainApplication.FromJoinRequest(1001, -100, 200, null, TestNow);
        inSurveyApp.MarkSurveyOffered();
        inSurveyApp.StartSurvey(1, TestNow);
        tenantA.Applications.Add(inSurveyApp);
        await tenantA.SaveChangesAsync();

        // Tenant B: one SurveyOffered application (should show up) and one AwaitingReview (should NOT).
        var surveyOfferedApp = DomainApplication.FromJoinRequest(2001, -200, 300, null, TestNow);
        surveyOfferedApp.MarkSurveyOffered();
        tenantB.Applications.Add(surveyOfferedApp);

        var awaitingReviewApp = DomainApplication.FromJoinRequest(2002, -200, 301, null, TestNow);
        awaitingReviewApp.MarkSurveyOffered();
        awaitingReviewApp.StartSurvey(1, TestNow);
        var answer = Answer.Create(1, "Q?", QuestionType.Text, 1, "A", null, TestNow);
        awaitingReviewApp.AddAnswer(answer, nextQuestionId: null, TestNow);
        tenantB.Applications.Add(awaitingReviewApp);
        await tenantB.SaveChangesAsync();

        // Replicate the exact loop from Endpoints.cs "/internal/applications/in-progress".
        var result = new List<(long TenantId, long TelegramUserId)>();
        var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync();
        foreach (var tenant in tenants)
        {
            await using var db = fixture.CreateContextFor(tenant.DatabaseName);
            var userIds = await db.Applications.AsNoTracking()
                .Where(a => a.Status == ApplicationStatus.SurveyOffered || a.Status == ApplicationStatus.InSurvey)
                .Select(a => a.TelegramUserId)
                .ToListAsync();
            result.AddRange(userIds.Select(userId => (tenant.Id, userId)));
        }

        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.TelegramUserId == 1001);
        Assert.Contains(result, r => r.TelegramUserId == 2001);
        Assert.DoesNotContain(result, r => r.TelegramUserId == 2002);
    }

    [Fact]
    public async Task ByUser_Returns_The_Most_Recent_Application_Across_Tenants()
    {
        var (catalog, tenantA, tenantB) = await SeedTwoTenantsAsync();
        const long telegramUserId = 3001;

        // The SAME Telegram account applied to both communities — tenant A's is older.
        var olderApp = DomainApplication.FromJoinRequest(telegramUserId, -100, 200, null, TestNow);
        tenantA.Applications.Add(olderApp);
        await tenantA.SaveChangesAsync();

        var newerNow = TestNow.AddHours(1);
        var newerApp = DomainApplication.FromJoinRequest(telegramUserId, -200, 300, null, newerNow);
        newerApp.MarkSurveyOffered();
        tenantB.Applications.Add(newerApp);
        await tenantB.SaveChangesAsync();

        // Replicate the exact loop from Endpoints.cs "/internal/applications/by-user/{id}".
        Domain.Application? best = null;
        var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync();
        foreach (var tenant in tenants)
        {
            await using var db = fixture.CreateContextFor(tenant.DatabaseName);
            var candidate = await db.Applications.AsNoTracking()
                .Where(a => a.TelegramUserId == telegramUserId)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync();
            if (candidate is not null && (best is null || candidate.CreatedAt > best.CreatedAt))
                best = candidate;
        }

        Assert.NotNull(best);
        Assert.Equal(newerNow, best.CreatedAt);
        Assert.Equal(ApplicationStatus.SurveyOffered, best.Status);
    }

    [Fact]
    public async Task ByUser_Returns_Null_When_No_Tenant_Has_An_Application_For_This_User()
    {
        var (catalog, tenantA, tenantB) = await SeedTwoTenantsAsync();

        // Seed an unrelated application so the tenant databases aren't empty.
        tenantA.Applications.Add(DomainApplication.FromJoinRequest(9999, -100, 200, null, TestNow));
        await tenantA.SaveChangesAsync();

        Domain.Application? best = null;
        var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync();
        foreach (var tenant in tenants)
        {
            await using var db = fixture.CreateContextFor(tenant.DatabaseName);
            var candidate = await db.Applications.AsNoTracking()
                .Where(a => a.TelegramUserId == 424242)
                .OrderByDescending(a => a.CreatedAt)
                .FirstOrDefaultAsync();
            if (candidate is not null && (best is null || candidate.CreatedAt > best.CreatedAt))
                best = candidate;
        }

        Assert.Null(best);
        _ = tenantB; // seeded for symmetry with the other tests, deliberately left empty here
    }

    [Fact]
    public async Task PendingCommands_Claims_Across_Tenants_Up_To_The_Requested_Batch_And_Marks_InFlight()
    {
        var (catalog, tenantA, tenantB) = await SeedTwoTenantsAsync();

        var payload = JsonSerializer.Serialize(new TelegramCommandPayload(ChatId: -1, Text: "hi"));
        for (var i = 0; i < 3; i++)
            tenantA.TelegramCommands.Add(TelegramCommand.Enqueue(TelegramCommandType.SendMessage, payload, null, 1000 + i, TestNow));
        await tenantA.SaveChangesAsync();

        for (var i = 0; i < 2; i++)
            tenantB.TelegramCommands.Add(TelegramCommand.Enqueue(TelegramCommandType.SendMessage, payload, null, 2000 + i, TestNow));
        await tenantB.SaveChangesAsync();

        // Replicate the exact loop from Endpoints.cs "/internal/telegram-commands/pending", take=4 —
        // fewer than the 5 total Pending commands across both tenants, so the batch cap must bite.
        const int take = 4;
        var claimed = new List<(long TenantId, long CommandId)>();
        var tenants = await catalog.Tenants.AsNoTracking().Where(t => t.IsActive).ToListAsync();
        foreach (var tenant in tenants)
        {
            if (claimed.Count >= take) break;
            await using var db = fixture.CreateContextFor(tenant.DatabaseName);

            var pending = await db.TelegramCommands
                .Where(c => c.Status == TelegramCommandStatus.Pending)
                .OrderBy(c => c.Id)
                .Take(take - claimed.Count)
                .ToListAsync();
            if (pending.Count == 0) continue;

            foreach (var cmd in pending) cmd.MarkInFlight(TestNow);
            await db.SaveChangesAsync();

            claimed.AddRange(pending.Select(c => (tenant.Id, c.Id)));
        }

        Assert.Equal(4, claimed.Count);

        // The 5th (unclaimed) command must still be Pending; every claimed one must now be InFlight —
        // this is the actual point of the endpoint (a second drain pass can't double-claim).
        var stillPendingA = await tenantA.TelegramCommands.AsNoTracking().CountAsync(c => c.Status == TelegramCommandStatus.Pending);
        var stillPendingB = await tenantB.TelegramCommands.AsNoTracking().CountAsync(c => c.Status == TelegramCommandStatus.Pending);
        Assert.Equal(1, stillPendingA + stillPendingB);

        var inFlightA = await tenantA.TelegramCommands.AsNoTracking().CountAsync(c => c.Status == TelegramCommandStatus.InFlight);
        var inFlightB = await tenantB.TelegramCommands.AsNoTracking().CountAsync(c => c.Status == TelegramCommandStatus.InFlight);
        Assert.Equal(4, inFlightA + inFlightB);
    }

    [Fact]
    public async Task Ack_Success_Marks_Succeeded_And_Sets_AdminCardMessageId_For_SendAdminCard()
    {
        var (catalog, tenantA, _) = await SeedTwoTenantsAsync();

        var application = DomainApplication.FromJoinRequest(4001, -100, 200, null, TestNow);
        tenantA.Applications.Add(application);
        await tenantA.SaveChangesAsync();   // assigns application.Id — must happen before it's referenced below

        var payload = JsonSerializer.Serialize(new TelegramCommandPayload(ChatId: -1, Text: "card"));
        var cmd = TelegramCommand.Enqueue(TelegramCommandType.SendAdminCard, payload, application.Id, 4001, TestNow);
        tenantA.TelegramCommands.Add(cmd);
        await tenantA.SaveChangesAsync();

        var tenantRow = await catalog.Tenants.AsNoTracking().FirstAsync(t => t.Slug == "tenant-a");

        // Replicate the success branch of Endpoints.cs "/internal/telegram-commands/{tenantId}/{commandId}/result".
        await using (var db = fixture.CreateContextFor(tenantRow.DatabaseName))
        {
            var loadedCmd = await db.TelegramCommands.FirstAsync(c => c.Id == cmd.Id);
            loadedCmd.MarkSucceeded(TestNow.AddSeconds(1));

            const long messageId = 777;
            if (loadedCmd.Type == TelegramCommandType.SendAdminCard && loadedCmd.ApplicationId is { } appId)
            {
                var app = await db.Applications.FirstOrDefaultAsync(a => a.Id == appId);
                var p = JsonSerializer.Deserialize<TelegramCommandPayload>(loadedCmd.PayloadJson)!;
                app?.SetAdminCardMessageId(messageId, hasPhoto: p.PhotoFileId is not null);
            }
            await db.SaveChangesAsync();
        }

        await using (var verify = fixture.CreateContextFor(tenantRow.DatabaseName))
        {
            var storedCmd = await verify.TelegramCommands.AsNoTracking().FirstAsync(c => c.Id == cmd.Id);
            Assert.Equal(TelegramCommandStatus.Succeeded, storedCmd.Status);

            var storedApp = await verify.Applications.AsNoTracking().FirstAsync(a => a.Id == application.Id);
            Assert.Equal(777, storedApp.AdminCardMessageId);
            Assert.False(storedApp.AdminCardHasPhoto);
        }
    }

    [Fact]
    public async Task Ack_Failure_Marks_Failed_With_The_Reported_Error()
    {
        var (catalog, tenantA, _) = await SeedTwoTenantsAsync();

        var payload = JsonSerializer.Serialize(new TelegramCommandPayload(ChatId: -1, Text: "hi"));
        var cmd = TelegramCommand.Enqueue(TelegramCommandType.SendMessage, payload, null, 5001, TestNow);
        tenantA.TelegramCommands.Add(cmd);
        await tenantA.SaveChangesAsync();

        var tenantRow = await catalog.Tenants.AsNoTracking().FirstAsync(t => t.Slug == "tenant-a");

        await using (var db = fixture.CreateContextFor(tenantRow.DatabaseName))
        {
            var loadedCmd = await db.TelegramCommands.FirstAsync(c => c.Id == cmd.Id);
            loadedCmd.MarkFailed("USER_ALREADY_PARTICIPANT", TestNow.AddSeconds(1));
            await db.SaveChangesAsync();
        }

        await using var verify = fixture.CreateContextFor(tenantRow.DatabaseName);
        var storedCmd = await verify.TelegramCommands.AsNoTracking().FirstAsync(c => c.Id == cmd.Id);
        Assert.Equal(TelegramCommandStatus.Failed, storedCmd.Status);
        Assert.Equal("USER_ALREADY_PARTICIPANT", storedCmd.LastError);
    }
}
