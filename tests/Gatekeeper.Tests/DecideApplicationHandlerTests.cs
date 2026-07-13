using System.Text.Json;
using Gatekeeper.Application;
using Gatekeeper.Application.Applications;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using DomainApplication = Gatekeeper.Domain.Application;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for DecideApplicationHandler, which approves or rejects applications awaiting review,
/// enqueuing outbox commands for the bot to execute and recording moderation audit entries.
/// Covers optimistic concurrency checks, localization, and admin card updates.
/// </summary>
public class DecideApplicationHandlerTests
{
    private const long TestTelegramUserId = 123456789L;
    private const long TestMainChatId = -987654321L;
    private const long TestUserChatId = 111222333L;
    private const string TestInviteLink = "https://t.me/testgroup";
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long AdminUserId = 999L;
    private const string AdminName = "admin";

    private static DecideApplicationHandler CreateHandler(
        FakeApplicationRepository? apps = null,
        FakeModerationRepository? moderation = null,
        FakeTelegramCommandQueue? queue = null,
        FakeTenantDirectory? directory = null,
        FakeTenantContext? tenant = null,
        FakeUserRepository? users = null,
        FakeTranslationRepository? translations = null,
        FakeUnitOfWork? unitOfWork = null,
        FakeClock? clock = null)
    {
        apps ??= new();
        moderation ??= new();
        queue ??= new();
        directory ??= new(adminChatId: -5000);
        tenant ??= new();
        users ??= new();
        translations ??= SeededTranslations();
        unitOfWork ??= new();
        clock ??= new(TestNow);

        return new(apps, moderation, queue, directory, tenant, users, translations, unitOfWork, clock);
    }

    /// <summary>Seeds just the decision-DM keys these tests assert on — full seed data lives in
    /// TranslationSeedData.cs, but these are unit tests for the handler's logic, not the seed.</summary>
    private static FakeTranslationRepository SeededTranslations()
    {
        var translations = new FakeTranslationRepository();
        translations.Store.Add(Translation.Create("bot.decision.approved", "ru", "✅ Ваша заявка одобрена, добро пожаловать!"));
        translations.Store.Add(Translation.Create("bot.decision.approved", "en", "✅ Your application has been approved, welcome!"));
        translations.Store.Add(Translation.Create("bot.decision.rejected", "ru", "❌ Ваша заявка отклонена."));
        translations.Store.Add(Translation.Create("bot.decision.rejected", "en", "❌ Your application was declined."));
        return translations;
    }

    private static DomainApplication CreateAwaitingReviewApplication()
    {
        var application = DomainApplication.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);

        var answer = Answer.Create(
            questionId: 1L, promptSnapshot: "Question?", type: QuestionType.Text,
            position: 1, text: "Some answer", optionsJson: null, now: TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);

        return application;
    }

    [Fact]
    public async Task Approve_From_AwaitingReview_Succeeds()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var unitOfWork = new FakeUnitOfWork();

        var application = CreateAwaitingReviewApplication();
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue, unitOfWork: unitOfWork);
        await handler.HandleAsync(new DecideApplicationCommand(
            ApplicationId: 1,
            Approve: true,
            Reason: null,
            ActingUserId: AdminUserId,
            ActingUserName: AdminName,
            Source: ActionSource.Web,
            ExpectedRowVersion: null));

        // Verify status changed
        var updated = apps.Store[1];
        Assert.Equal(ApplicationStatus.Approved, updated.Status);
        Assert.Equal(TestNow, updated.DecidedAt);

        // Verify audit entry
        var audit = Assert.Single(moderation.Store);
        Assert.Equal(ModerationActionType.Approve, audit.Action);

        // Verify two commands enqueued (ApproveJoinRequest + SendMessage)
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.Equal(TelegramCommandType.ApproveJoinRequest, queue.Enqueued[0].Type);
        Assert.Equal(TelegramCommandType.SendMessage, queue.Enqueued[1].Type);

        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Reject_From_AwaitingReview_Succeeds()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var unitOfWork = new FakeUnitOfWork();

        var application = CreateAwaitingReviewApplication();
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue, unitOfWork: unitOfWork);
        await handler.HandleAsync(new DecideApplicationCommand(
            ApplicationId: 1,
            Approve: false,
            Reason: null,
            ActingUserId: AdminUserId,
            ActingUserName: AdminName,
            Source: ActionSource.Web,
            ExpectedRowVersion: null));

        var updated = apps.Store[1];
        Assert.Equal(ApplicationStatus.Rejected, updated.Status);
        Assert.Equal(TestNow, updated.DecidedAt);

        var audit = Assert.Single(moderation.Store);
        Assert.Equal(ModerationActionType.Reject, audit.Action);

        // DeclineJoinRequest + SendMessage
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.Equal(TelegramCommandType.DeclineJoinRequest, queue.Enqueued[0].Type);
        Assert.Equal(TelegramCommandType.SendMessage, queue.Enqueued[1].Type);

        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task AlreadyDecided_Application_Throws_ConcurrencyConflictException_No_SideEffects()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();

        var application = CreateAwaitingReviewApplication();
        // Pre-decide it
        application.Approve(TestNow);
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue);

        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => handler.HandleAsync(new DecideApplicationCommand(
                ApplicationId: 1,
                Approve: false,
                Reason: null,
                ActingUserId: AdminUserId,
                ActingUserName: AdminName,
                Source: ActionSource.Web,
                ExpectedRowVersion: null)));
        Assert.Contains("already", ex.Message);

        // Verify no side effects
        Assert.Empty(moderation.Store);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task RowVersion_Mismatch_Throws_ConcurrencyConflictException_No_SideEffects()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();

        var application = CreateAwaitingReviewApplication();
        // Get its current RowVersion (likely 0 for a freshly created in-memory app)
        var currentRowVersion = application.RowVersion;
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue);

        // Pass a different expected row version
        var ex = await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => handler.HandleAsync(new DecideApplicationCommand(
                ApplicationId: 1,
                Approve: true,
                Reason: null,
                ActingUserId: AdminUserId,
                ActingUserName: AdminName,
                Source: ActionSource.Web,
                ExpectedRowVersion: currentRowVersion + 1)));
        Assert.Contains("modified", ex.Message);

        // Verify no side effects
        Assert.Empty(moderation.Store);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task Approval_Sends_Localized_Russian_DM_For_Russian_User()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var users = new FakeUserRepository();

        var application = CreateAwaitingReviewApplication();
        apps.Seed(application, id: 1);

        // Seed a Russian user
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId, isBot: false, isPremium: false,
            username: "ivan", firstName: "Ivan", lastName: null,
            languageCode: "ru_RU",
            bio: null, photoFileId: null,
            normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        users.Seed(user);

        var handler = CreateHandler(apps, moderation, queue, users: users);
        await handler.HandleAsync(new DecideApplicationCommand(
            ApplicationId: 1,
            Approve: true,
            Reason: null,
            ActingUserId: AdminUserId,
            ActingUserName: AdminName,
            Source: ActionSource.Web,
            ExpectedRowVersion: null));

        // Find the SendMessage command (second command)
        var sendMsgCmd = queue.Enqueued.First(c => c.Type == TelegramCommandType.SendMessage);
        var payload = JsonSerializer.Deserialize<TelegramCommandPayload>(sendMsgCmd.PayloadJson)
            ?? throw new InvalidOperationException("Payload should deserialize");

        Assert.NotNull(payload.Text);
        Assert.Contains("одобрена", payload.Text);
    }

    [Fact]
    public async Task Approval_Sends_English_DM_For_Non_Russian_User()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var users = new FakeUserRepository();

        var application = CreateAwaitingReviewApplication();
        apps.Seed(application, id: 1);

        // Seed an English user
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId, isBot: false, isPremium: false,
            username: "bob", firstName: "Bob", lastName: null,
            languageCode: "en_US",
            bio: null, photoFileId: null,
            normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        users.Seed(user);

        var handler = CreateHandler(apps, moderation, queue, users: users);
        await handler.HandleAsync(new DecideApplicationCommand(
            ApplicationId: 1,
            Approve: true,
            Reason: null,
            ActingUserId: AdminUserId,
            ActingUserName: AdminName,
            Source: ActionSource.Web,
            ExpectedRowVersion: null));

        var sendMsgCmd = queue.Enqueued.First(c => c.Type == TelegramCommandType.SendMessage);
        var payload = JsonSerializer.Deserialize<TelegramCommandPayload>(sendMsgCmd.PayloadJson)
            ?? throw new InvalidOperationException("Payload should deserialize");

        Assert.NotNull(payload.Text);
        Assert.Contains("approved", payload.Text);
    }

    [Fact]
    public async Task AdminCardMessageId_Set_Enqueues_EditMessage_Command()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var directory = new FakeTenantDirectory(adminChatId: -5000);

        var application = CreateAwaitingReviewApplication();
        // Set the admin card message id
        application.SetAdminCardMessageId(messageId: 999, hasPhoto: false);
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue, directory);
        await handler.HandleAsync(new DecideApplicationCommand(
            ApplicationId: 1,
            Approve: true,
            Reason: null,
            ActingUserId: AdminUserId,
            ActingUserName: AdminName,
            Source: ActionSource.Web,
            ExpectedRowVersion: null));

        // Should have 3 commands: ApproveJoinRequest, SendMessage, EditMessage
        Assert.Equal(3, queue.Enqueued.Count);
        var editCmd = queue.Enqueued.First(c => c.Type == TelegramCommandType.EditMessage);
        var payload = JsonSerializer.Deserialize<TelegramCommandPayload>(editCmd.PayloadJson)
            ?? throw new InvalidOperationException("Payload should deserialize");

        Assert.Equal(999, payload.MessageId);
        Assert.Equal(-5000, payload.ChatId);
    }

    [Fact]
    public async Task AdminCardMessageId_Set_But_No_AdminChat_Silently_Skips_EditMessage()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var directory = new FakeTenantDirectory(adminChatId: null);

        var application = CreateAwaitingReviewApplication();
        application.SetAdminCardMessageId(messageId: 999, hasPhoto: false);
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue, directory);
        // Should not throw, just skip the edit
        await handler.HandleAsync(new DecideApplicationCommand(
            ApplicationId: 1,
            Approve: true,
            Reason: null,
            ActingUserId: AdminUserId,
            ActingUserName: AdminName,
            Source: ActionSource.Web,
            ExpectedRowVersion: null));

        // Only 2 commands: ApproveJoinRequest and SendMessage (no EditMessage)
        Assert.Equal(2, queue.Enqueued.Count);
        Assert.DoesNotContain(queue.Enqueued, c => c.Type == TelegramCommandType.EditMessage);
    }

    [Fact]
    public async Task Application_Not_Found_Throws()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();

        // Don't seed anything

        var handler = CreateHandler(apps, moderation, queue);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new DecideApplicationCommand(
                ApplicationId: 999,
                Approve: true,
                Reason: null,
                ActingUserId: AdminUserId,
                ActingUserName: AdminName,
                Source: ActionSource.Web,
                ExpectedRowVersion: null)));
        Assert.Contains("not found", ex.Message);
    }
}
