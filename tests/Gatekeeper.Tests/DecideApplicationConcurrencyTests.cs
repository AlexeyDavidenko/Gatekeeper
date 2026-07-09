using Gatekeeper.Application;
using Gatekeeper.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;
using DomainApplication = Gatekeeper.Domain.Application;

namespace Gatekeeper.Tests;

/// <summary>
/// The one thing a fake repository can never honestly prove: a genuine race on Postgres's own xmin
/// system column. DecideApplicationHandler's pre-check (comparing ExpectedRowVersion against the
/// freshly-loaded RowVersion) only catches a race where both sides re-read after the first commit —
/// it does nothing for the narrower window where a second decision already holds a stale in-memory
/// copy loaded BEFORE the first one committed (e.g. the admin-group path, which always passes
/// ExpectedRowVersion: null and skips the pre-check entirely, racing a website decision that read
/// the row a moment earlier). That gap can only be exercised against a real Postgres instance.
///
/// This test caught a real gap: TenantDbContext's IUnitOfWork.SaveChangesAsync used to let EF's raw
/// DbUpdateConcurrencyException escape uncaught in exactly this window — the API's
/// "catch (ConcurrencyConflictException)" around the decision endpoint never fired, so a genuine
/// concurrent decision would have surfaced as an unhandled 500 instead of the intended clean 409.
/// Fixed in Persistence.cs by translating the exception at the actual commit boundary — see
/// docs/changelog.md 2026-07-09 for the full story. This test asserts the fixed behavior.
/// </summary>
[Collection("Postgres")]
public sealed class DecideApplicationConcurrencyTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static DomainApplication BuildAwaitingReviewApplication()
    {
        var application = DomainApplication.FromJoinRequest(111, -222, 333, null, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1, TestNow);
        var answer = Answer.Create(1, "Q?", QuestionType.Text, 1, "A", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);
        return application;
    }

    [Fact]
    public async Task Stale_Concurrent_Decision_Raises_A_Concurrency_Exception_On_SaveChanges()
    {
        await using var seedDb = await fixture.CreateTenantDbAsync();

        var application = BuildAwaitingReviewApplication();
        seedDb.Applications.Add(application);
        await seedDb.SaveChangesAsync();
        var applicationId = application.Id;
        var databaseName = seedDb.Database.GetDbConnection().Database;

        // Two independent DbContexts, each loading its own in-memory copy of the same row — this is
        // what two concurrent requests (admin-group callback vs website decision) actually look like.
        await using var db1 = fixture.CreateContextFor(databaseName);
        await using var db2 = fixture.CreateContextFor(databaseName);

        var app1 = await db1.Applications.FirstAsync(a => a.Id == applicationId);
        var app2 = await db2.Applications.FirstAsync(a => a.Id == applicationId);

        // First decision commits and bumps xmin.
        app1.Approve(TestNow);
        await db1.SaveChangesAsync();

        // Second decision still holds the pre-commit snapshot — its own RowVersion pre-check (if
        // any) would compare against the value it read, not the one that's now actually in the
        // database, so only a real SaveChanges against Postgres can catch this. Go through
        // IUnitOfWork specifically — that's the interface DecideApplicationHandler actually calls
        // in production, and the fix lives in its explicit interface implementation.
        app2.Reject(TestNow);
        IUnitOfWork unitOfWork2 = db2;
        var ex = await Record.ExceptionAsync(() => unitOfWork2.SaveChangesAsync());

        Assert.NotNull(ex);
        Assert.IsType<ConcurrencyConflictException>(ex);
        Assert.IsType<DbUpdateConcurrencyException>(ex.InnerException);
    }
}
