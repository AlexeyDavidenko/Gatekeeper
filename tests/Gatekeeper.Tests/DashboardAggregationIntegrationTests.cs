using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using Gatekeeper.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Xunit;
using DomainApplication = Gatekeeper.Domain.Application;

namespace Gatekeeper.Tests;

/// <summary>
/// Integration tests for the /dashboard endpoint's UTC-bucketed aggregations. Query is intentionally
/// duplicated from Endpoints.cs rather than extracted into a shared method, per project convention
/// for read queries (see docs/conventions.md "Как добавить чтение (query)").
/// </summary>
[Collection("Postgres")]
public sealed class DashboardAggregationIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private static readonly Func<string?, string?, string> SimpleNormalize =
        (first, last) => $"{first} {last}".Trim().ToLowerInvariant();

    [Fact]
    public async Task Decisions_At_TodayStart_Boundary_Count_As_Today_Not_Before()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        var todayStart = new DateTimeOffset(TestNow.UtcDateTime.Date, TimeSpan.Zero);
        var weekStart = todayStart.AddDays(-7);

        // Seed a decision exactly at todayStart
        var atBoundary = ModerationAction.ForDecision(
            telegramUserId: 1001, applicationId: 100, chatId: -999,
            action: ModerationActionType.Approve, reason: "Good application",
            performedByUserId: 500, performedByName: "Admin1", source: ActionSource.Web,
            now: todayStart);

        // Seed a decision one millisecond before todayStart (previous day)
        var beforeBoundary = ModerationAction.ForDecision(
            telegramUserId: 1002, applicationId: 101, chatId: -999,
            action: ModerationActionType.Approve, reason: "Good application",
            performedByUserId: 500, performedByName: "Admin1", source: ActionSource.Web,
            now: todayStart.AddMilliseconds(-1));

        db.ModerationActions.Add(atBoundary);
        db.ModerationActions.Add(beforeBoundary);
        await db.SaveChangesAsync();

        // Execute the exact query from Endpoints.cs /dashboard — two-step pattern
        var decisions = await db.ModerationActions.AsNoTracking()
            .Where(a => a.CreatedAt >= weekStart &&
                (a.Action == ModerationActionType.Approve || a.Action == ModerationActionType.Reject))
            .ToListAsync();

        var approvedToday = decisions.Count(a => a.CreatedAt >= todayStart && a.Action == ModerationActionType.Approve);

        // Assert: exactly 1 decision counts as "today" (the one at todayStart), but both are in the week range
        Assert.Equal(1, approvedToday);
        Assert.Equal(2, decisions.Count);
    }

    [Fact]
    public async Task Decisions_At_WeekStart_Boundary_Count_As_This_Week_Not_Before()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        var todayStart = new DateTimeOffset(TestNow.UtcDateTime.Date, TimeSpan.Zero);
        var weekStart = todayStart.AddDays(-7);

        // Seed a decision exactly at weekStart (7 days before todayStart)
        var atWeekBoundary = ModerationAction.ForDecision(
            telegramUserId: 2001, applicationId: 200, chatId: -999,
            action: ModerationActionType.Approve, reason: "Good application",
            performedByUserId: 500, performedByName: "Admin1", source: ActionSource.Web,
            now: weekStart);

        // Seed a decision one millisecond before weekStart
        var beforeWeekBoundary = ModerationAction.ForDecision(
            telegramUserId: 2002, applicationId: 201, chatId: -999,
            action: ModerationActionType.Approve, reason: "Good application",
            performedByUserId: 500, performedByName: "Admin1", source: ActionSource.Web,
            now: weekStart.AddMilliseconds(-1));

        db.ModerationActions.Add(atWeekBoundary);
        db.ModerationActions.Add(beforeWeekBoundary);
        await db.SaveChangesAsync();

        // Execute the first DB round-trip from Endpoints.cs /dashboard
        var decisions = await db.ModerationActions.AsNoTracking()
            .Where(a => a.CreatedAt >= weekStart &&
                (a.Action == ModerationActionType.Approve || a.Action == ModerationActionType.Reject))
            .ToListAsync();

        // Assert: exactly 1 decision is in the week range (the one at weekStart), the one before is excluded
        Assert.Single(decisions);
        Assert.Equal(weekStart, decisions[0].CreatedAt);
    }

    [Fact]
    public async Task Approve_And_Reject_Counted_Separately()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        var todayStart = new DateTimeOffset(TestNow.UtcDateTime.Date, TimeSpan.Zero);
        var weekStart = todayStart.AddDays(-7);

        // Seed 2 approvals and 3 rejections all within "today"
        for (int i = 0; i < 2; i++)
        {
            var action = ModerationAction.ForDecision(
                telegramUserId: (3000 + i), applicationId: (300 + i), chatId: -999,
                action: ModerationActionType.Approve, reason: "Good",
                performedByUserId: 500, performedByName: "Admin1", source: ActionSource.Web,
                now: todayStart.AddHours(1 + i));
            db.ModerationActions.Add(action);
        }

        for (int i = 0; i < 3; i++)
        {
            var action = ModerationAction.ForDecision(
                telegramUserId: (3100 + i), applicationId: (310 + i), chatId: -999,
                action: ModerationActionType.Reject, reason: "Bad",
                performedByUserId: 500, performedByName: "Admin1", source: ActionSource.Web,
                now: todayStart.AddHours(2 + i));
            db.ModerationActions.Add(action);
        }

        await db.SaveChangesAsync();

        // Execute the exact query pattern from Endpoints.cs /dashboard
        var decisions = await db.ModerationActions.AsNoTracking()
            .Where(a => a.CreatedAt >= weekStart &&
                (a.Action == ModerationActionType.Approve || a.Action == ModerationActionType.Reject))
            .ToListAsync();

        var approvedToday = decisions.Count(a => a.CreatedAt >= todayStart && a.Action == ModerationActionType.Approve);
        var rejectedToday = decisions.Count(a => a.CreatedAt >= todayStart && a.Action == ModerationActionType.Reject);

        // Assert: separate counts match what we seeded
        Assert.Equal(2, approvedToday);
        Assert.Equal(3, rejectedToday);
    }

    [Fact]
    public async Task Pending_Applications_Count_Only_Includes_AwaitingReview_Status()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        var user1 = TelegramUser.FirstSighting(
            4001, isBot: false, isPremium: false,
            username: "user1", firstName: "User", lastName: "One",
            languageCode: "en", bio: null, photoFileId: null,
            SimpleNormalize, TestNow);
        db.Users.Add(user1);
        await db.SaveChangesAsync();

        // Build an application in AwaitingReview status
        var awaitingReviewApp = DomainApplication.FromJoinRequest(4001, -999, 888, null, TestNow);
        awaitingReviewApp.MarkSurveyOffered();
        awaitingReviewApp.StartSurvey(1, TestNow);
        var answer = Answer.Create(1, "Question?", QuestionType.Text, 1, "Answer text", null, TestNow);
        awaitingReviewApp.AddAnswer(answer, nextQuestionId: null, TestNow);

        // Build an application in JoinRequested status
        var joinRequestedApp = DomainApplication.FromJoinRequest(4002, -999, 888, null, TestNow);

        // Build an application in SurveyOffered status
        var surveyOfferedApp = DomainApplication.FromJoinRequest(4003, -999, 888, null, TestNow);
        surveyOfferedApp.MarkSurveyOffered();

        db.Applications.Add(awaitingReviewApp);
        db.Applications.Add(joinRequestedApp);
        db.Applications.Add(surveyOfferedApp);
        await db.SaveChangesAsync();

        // Execute the exact query from Endpoints.cs /dashboard
        var pendingCount = await db.Applications.AsNoTracking()
            .CountAsync(a => a.Status == ApplicationStatus.AwaitingReview);

        // Assert: only the AwaitingReview one counts
        Assert.Equal(1, pendingCount);
    }

    [Fact]
    public async Task Outbox_Commands_Counted_By_Status()
    {
        await using var db = await fixture.CreateTenantDbAsync();

        var payloadJson = JsonSerializer.Serialize(new TelegramCommandPayload(
            ChatId: -999, UserId: 1001, MessageId: null,
            Text: "Approved", Buttons: null, PhotoFileId: null, IsPhotoCaption: false));

        // Seed commands in different statuses
        var pendingCmd1 = TelegramCommand.Enqueue(
            TelegramCommandType.SendMessage, payloadJson,
            applicationId: 500, telegramUserId: 1001, TestNow);
        var pendingCmd2 = TelegramCommand.Enqueue(
            TelegramCommandType.SendMessage, payloadJson,
            applicationId: 501, telegramUserId: 1002, TestNow);

        var inflightCmd = TelegramCommand.Enqueue(
            TelegramCommandType.SendMessage, payloadJson,
            applicationId: 502, telegramUserId: 1003, TestNow);
        inflightCmd.MarkInFlight();

        var failedCmd = TelegramCommand.Enqueue(
            TelegramCommandType.SendMessage, payloadJson,
            applicationId: 503, telegramUserId: 1004, TestNow);
        failedCmd.MarkFailed("Connection timeout", TestNow);

        db.TelegramCommands.Add(pendingCmd1);
        db.TelegramCommands.Add(pendingCmd2);
        db.TelegramCommands.Add(inflightCmd);
        db.TelegramCommands.Add(failedCmd);
        await db.SaveChangesAsync();

        // Execute the exact query pattern from Endpoints.cs /dashboard
        var outboxPending = await db.TelegramCommands.AsNoTracking()
            .CountAsync(c => c.Status == TelegramCommandStatus.Pending);
        var outboxInFlight = await db.TelegramCommands.AsNoTracking()
            .CountAsync(c => c.Status == TelegramCommandStatus.InFlight);
        var outboxFailed = await db.TelegramCommands.AsNoTracking()
            .CountAsync(c => c.Status == TelegramCommandStatus.Failed);

        // Assert: counts match what we seeded
        Assert.Equal(2, outboxPending);
        Assert.Equal(1, outboxInFlight);
        Assert.Equal(1, outboxFailed);
    }
}
