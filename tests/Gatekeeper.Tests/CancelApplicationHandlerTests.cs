using System.Text.Json;
using Gatekeeper.Application;
using Gatekeeper.Application.Applications;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using DomainApplication = Gatekeeper.Domain.Application;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for CancelApplicationHandler — the admin-driven "/cancel {id}" command that stops an
/// applicant's survey mid-flight (e.g. after a native-Telegram-UI decline the bot was never told
/// about). Covers cancelling from SurveyOffered/InSurvey, the enqueued outbox commands, and the
/// guard against cancelling an already-decided application.
/// </summary>
public class CancelApplicationHandlerTests
{
    private const long TestTelegramUserId = 123456789L;
    private const long TestMainChatId = -987654321L;
    private const long TestUserChatId = 111222333L;
    private const string TestInviteLink = "https://t.me/testgroup";
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long AdminUserId = 999L;
    private const string AdminName = "admin";

    private static CancelApplicationHandler CreateHandler(
        FakeApplicationRepository? apps = null,
        FakeModerationRepository? moderation = null,
        FakeTelegramCommandQueue? queue = null,
        FakeUserRepository? users = null,
        FakeUnitOfWork? unitOfWork = null,
        FakeClock? clock = null)
    {
        apps ??= new();
        moderation ??= new();
        queue ??= new();
        users ??= new();
        unitOfWork ??= new();
        clock ??= new(TestNow);

        return new(apps, moderation, queue, users, unitOfWork, clock);
    }

    private static DomainApplication CreateSurveyOfferedApplication()
    {
        var application = DomainApplication.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        return application;
    }

    private static DomainApplication CreateInSurveyApplication()
    {
        var application = CreateSurveyOfferedApplication();
        application.StartSurvey(1L, TestNow);
        return application;
    }

    private static DomainApplication CreateAwaitingReviewApplication()
    {
        var application = CreateInSurveyApplication();
        var answer = Answer.Create(
            questionId: 1L, promptSnapshot: "Question?", type: QuestionType.Text,
            position: 1, text: "Some answer", optionsJson: null, now: TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);
        return application;
    }

    [Fact]
    public async Task Cancel_From_SurveyOffered_Succeeds()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var unitOfWork = new FakeUnitOfWork();

        var application = CreateSurveyOfferedApplication();
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue, unitOfWork: unitOfWork);
        await handler.HandleAsync(new CancelApplicationCommand(
            ApplicationId: 1, ActingUserId: AdminUserId, ActingUserName: AdminName));

        var updated = apps.Store[1];
        Assert.Equal(ApplicationStatus.Cancelled, updated.Status);

        var audit = Assert.Single(moderation.Store);
        Assert.Equal(ModerationActionType.Reject, audit.Action);

        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Cancel_From_InSurvey_Succeeds()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var unitOfWork = new FakeUnitOfWork();

        var application = CreateInSurveyApplication();
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue, unitOfWork: unitOfWork);
        await handler.HandleAsync(new CancelApplicationCommand(
            ApplicationId: 1, ActingUserId: AdminUserId, ActingUserName: AdminName));

        var updated = apps.Store[1];
        Assert.Equal(ApplicationStatus.Cancelled, updated.Status);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Cancel_Enqueues_DeclineJoinRequest_And_SendMessage()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();

        var application = CreateInSurveyApplication();
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue);
        await handler.HandleAsync(new CancelApplicationCommand(
            ApplicationId: 1, ActingUserId: AdminUserId, ActingUserName: AdminName));

        Assert.Equal(2, queue.Enqueued.Count);

        var declineCmd = queue.Enqueued[0];
        Assert.Equal(TelegramCommandType.DeclineJoinRequest, declineCmd.Type);
        var declinePayload = JsonSerializer.Deserialize<TelegramCommandPayload>(declineCmd.PayloadJson)
            ?? throw new InvalidOperationException("Payload should deserialize");
        Assert.Equal(TestMainChatId, declinePayload.ChatId);
        Assert.Equal(TestTelegramUserId, declinePayload.UserId);

        var sendMsgCmd = queue.Enqueued[1];
        Assert.Equal(TelegramCommandType.SendMessage, sendMsgCmd.Type);
        var sendMsgPayload = JsonSerializer.Deserialize<TelegramCommandPayload>(sendMsgCmd.PayloadJson)
            ?? throw new InvalidOperationException("Payload should deserialize");
        Assert.Equal(TestTelegramUserId, sendMsgPayload.ChatId);
        Assert.NotNull(sendMsgPayload.Text);
    }

    [Fact]
    public async Task Already_Approved_Application_Throws()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();

        var application = CreateAwaitingReviewApplication();
        application.Approve(TestNow);
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue);

        await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(new CancelApplicationCommand(
                ApplicationId: 1, ActingUserId: AdminUserId, ActingUserName: AdminName)));

        Assert.Empty(moderation.Store);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task Already_Rejected_Application_Throws()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();

        var application = CreateAwaitingReviewApplication();
        application.Reject(TestNow);
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, moderation, queue);

        await Assert.ThrowsAsync<DomainException>(
            () => handler.HandleAsync(new CancelApplicationCommand(
                ApplicationId: 1, ActingUserId: AdminUserId, ActingUserName: AdminName)));

        Assert.Empty(moderation.Store);
        Assert.Empty(queue.Enqueued);
    }

    [Fact]
    public async Task Application_Not_Found_Throws()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();

        var handler = CreateHandler(apps, moderation, queue);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new CancelApplicationCommand(
                ApplicationId: 999, ActingUserId: AdminUserId, ActingUserName: AdminName)));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task Cancel_Sends_Localized_Russian_DM_For_Russian_User()
    {
        var apps = new FakeApplicationRepository();
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var users = new FakeUserRepository();

        var application = CreateInSurveyApplication();
        apps.Seed(application, id: 1);

        var user = TelegramUser.FirstSighting(
            TestTelegramUserId, isBot: false, isPremium: false,
            username: "ivan", firstName: "Ivan", lastName: null,
            languageCode: "ru_RU",
            bio: null, photoFileId: null,
            normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        users.Seed(user);

        var handler = CreateHandler(apps, moderation, queue, users: users);
        await handler.HandleAsync(new CancelApplicationCommand(
            ApplicationId: 1, ActingUserId: AdminUserId, ActingUserName: AdminName));

        var sendMsgCmd = queue.Enqueued.First(c => c.Type == TelegramCommandType.SendMessage);
        var payload = JsonSerializer.Deserialize<TelegramCommandPayload>(sendMsgCmd.PayloadJson)
            ?? throw new InvalidOperationException("Payload should deserialize");

        Assert.NotNull(payload.Text);
        Assert.Contains("отклонена", payload.Text);
    }
}
