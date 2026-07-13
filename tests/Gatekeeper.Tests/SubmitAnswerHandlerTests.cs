using System.Text.Json;
using Gatekeeper.Application;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using DomainApplication = Gatekeeper.Domain.Application;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for SubmitAnswerHandler, which processes survey answers from applicants and advances
/// the survey state machine, enqueuing outbox commands for completion notifications.
/// </summary>
public class SubmitAnswerHandlerTests
{
    private const long TestTelegramUserId = 123456789L;
    private const long TestMainChatId = -987654321L;
    private const long TestUserChatId = 111222333L;
    private const string TestInviteLink = "https://t.me/testgroup";
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static SubmitAnswerHandler CreateHandler(
        FakeApplicationRepository? apps = null,
        FakeQuestionRepository? questions = null,
        FakeUserRepository? users = null,
        FakeTelegramCommandQueue? queue = null,
        FakeTenantDirectory? directory = null,
        FakeTenantContext? tenant = null,
        FakeUnitOfWork? unitOfWork = null,
        FakeClock? clock = null)
    {
        apps ??= new();
        questions ??= new();
        users ??= new();
        queue ??= new();
        directory ??= new(adminChatId: -5000);
        tenant ??= new();
        unitOfWork ??= new();
        clock ??= new(TestNow);

        return new(apps, questions, users, queue, directory, tenant, unitOfWork, clock);
    }

    [Fact]
    public async Task SubmitAnswer_Throws_SurveyNotStartedException_When_Still_At_SurveyOffered()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();

        // Seed an application at SurveyOffered status (not yet started) — same setup as before this
        // was fixed, but now a stray message must NOT silently start the survey and consume itself
        // as the answer to question #1 (see StartSurveyHandler for the correct, explicit way to start).
        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        apps.Seed(application, id: 1);

        var q1 = Question.Create(1, QuestionType.Text, "Question 1?", isRequired: true, configJson: null, TestNow);
        questions.Seed(q1);

        var handler = CreateHandler(apps, questions);

        await Assert.ThrowsAsync<SurveyNotStartedException>(() =>
            handler.HandleAsync(new SubmitAnswerCommand(TestTelegramUserId, Text: "First answer", null)));

        // Nothing recorded, status untouched — the stray message was rejected, not consumed.
        Assert.Empty(apps.Store[1].Answers);
        Assert.Equal(ApplicationStatus.SurveyOffered, apps.Store[1].Status);
    }

    [Fact]
    public async Task TextQuestion_Records_PlainText_Answer_And_Advances()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();

        // Seed an InSurvey application
        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        var q1 = Question.Create(1, QuestionType.Text, "Question 1?", isRequired: true, configJson: null, TestNow);
        questions.Seed(q1);
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        // Seed a second question
        var q2 = Question.Create(2, QuestionType.Text, "Question 2?", isRequired: true, configJson: null, TestNow);
        questions.Seed(q2);

        var handler = CreateHandler(apps, questions);
        var result = await handler.HandleAsync(new SubmitAnswerCommand(TestTelegramUserId, Text: "my answer", null));

        // Verify the answer was recorded
        var updated = apps.Store[1];
        var answer = Assert.Single(updated.Answers);
        Assert.Equal("my answer", answer.Text);
        Assert.Null(answer.OptionsJson);

        // Verify advanced to next question
        Assert.False(result.Completed);
        Assert.Equal(q2.Id, result.QuestionId);
    }

    [Fact]
    public async Task SingleChoice_Question_Joins_Selected_Labels()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();
        var queue = new FakeTelegramCommandQueue();

        // Seed a SingleChoice question as the last (only) question
        var q1 = Question.Create(
            1, QuestionType.SingleChoice, "Pick one",
            isRequired: true,
            configJson: ChoiceOptions.SerializeQuestionOptions(new[] { "Ads", "Friends", "Search" }),
            TestNow);
        questions.Seed(q1);

        // Seed an InSurvey application at this question
        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        // Seed a user so we can create the admin card
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId, isBot: false, isPremium: false,
            username: "testuser", firstName: "Test", lastName: "User",
            languageCode: "en", bio: null, photoFileId: null,
            normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        var userRepo = new FakeUserRepository();
        userRepo.Seed(user);

        var handler = CreateHandler(apps, questions, userRepo, queue);
        var result = await handler.HandleAsync(new SubmitAnswerCommand(TestTelegramUserId, Text: null, SelectedOptionIndexes: new[] { 1 }));

        // Verify the answer was recorded with the label
        var updated = apps.Store[1];
        var answer = Assert.Single(updated.Answers);
        Assert.Equal("Friends", answer.Text);

        // Verify OptionsJson round-trips correctly
        var indices = ChoiceOptions.ParseSelectedIndices(answer.OptionsJson);
        Assert.Equal(new[] { 1 }, indices);

        // Verify survey completed
        Assert.True(result.Completed);
        Assert.Null(result.QuestionId);

        // Verify admin card was enqueued
        Assert.Single(queue.Enqueued);
        Assert.Equal(TelegramCommandType.SendAdminCard, queue.Enqueued[0].Type);
    }

    [Fact]
    public async Task SingleChoice_Question_Records_English_Label_For_An_English_Language_User()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();
        var queue = new FakeTelegramCommandQueue();

        // Seed a SingleChoice question with a matching-count English translation.
        var q1 = Question.Create(
            1, QuestionType.SingleChoice, "Выберите один",
            isRequired: true,
            configJson: ChoiceOptions.SerializeQuestionOptions(["Реклама", "Друзья", "Поиск"]),
            TestNow,
            promptTextEn: "Pick one",
            configJsonEn: ChoiceOptions.SerializeQuestionOptions(["Ads", "Friends", "Search"]));
        questions.Seed(q1);

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        var user = TelegramUser.FirstSighting(
            TestTelegramUserId, isBot: false, isPremium: false,
            username: "bob", firstName: "Bob", lastName: null,
            languageCode: "en_US", bio: null, photoFileId: null,
            normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        var userRepo = new FakeUserRepository();
        userRepo.Seed(user);

        var handler = CreateHandler(apps, questions, userRepo, queue);
        var result = await handler.HandleAsync(new SubmitAnswerCommand(TestTelegramUserId, Text: null, SelectedOptionIndexes: new[] { 1 }));

        var answer = Assert.Single(apps.Store[1].Answers);
        Assert.Equal("Friends", answer.Text);
        Assert.Equal("Pick one", answer.PromptSnapshot);
        Assert.True(result.Completed);
    }

    [Fact]
    public async Task MultiChoice_Question_Joins_Multiple_Labels_With_Comma_Space()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();
        var queue = new FakeTelegramCommandQueue();

        // Seed a MultiChoice question as the last (only) question
        var q1 = Question.Create(
            1, QuestionType.MultiChoice, "Pick multiple",
            isRequired: true,
            configJson: ChoiceOptions.SerializeQuestionOptions(new[] { "Sport", "Music", "Film", "Games" }),
            TestNow);
        questions.Seed(q1);

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        var user = TelegramUser.FirstSighting(
            TestTelegramUserId, isBot: false, isPremium: false,
            username: "testuser", firstName: "Test", lastName: "User",
            languageCode: "en", bio: null, photoFileId: null,
            normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        var userRepo = new FakeUserRepository();
        userRepo.Seed(user);

        var handler = CreateHandler(apps, questions, userRepo, queue);
        var result = await handler.HandleAsync(
            new SubmitAnswerCommand(TestTelegramUserId, Text: null, SelectedOptionIndexes: new[] { 0, 2, 3 }));

        var updated = apps.Store[1];
        var answer = Assert.Single(updated.Answers);
        Assert.Equal("Sport, Film, Games", answer.Text);

        Assert.True(result.Completed);
    }

    [Fact]
    public async Task Stray_FreeText_On_Choice_Question_Does_Not_Advance()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();

        // Seed a SingleChoice question
        var q1 = Question.Create(
            1, QuestionType.SingleChoice, "Pick one",
            isRequired: true,
            configJson: ChoiceOptions.SerializeQuestionOptions(new[] { "Option A", "Option B" }),
            TestNow);
        questions.Seed(q1);

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, questions);
        // Submit text instead of selecting an option
        var result = await handler.HandleAsync(
            new SubmitAnswerCommand(TestTelegramUserId, Text: "some text", SelectedOptionIndexes: null));

        // Should re-prompt the same question
        Assert.False(result.Completed);
        Assert.Equal(q1.Id, result.QuestionId);
        Assert.NotNull(result.Options);

        // No answer recorded
        var updated = apps.Store[1];
        Assert.Empty(updated.Answers);

        // Survey did not advance
        Assert.Equal(q1.Id, updated.Session!.CurrentQuestionId);
    }

    [Fact]
    public async Task Empty_SelectedOptionIndexes_List_Does_Not_Advance()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();

        var q1 = Question.Create(
            1, QuestionType.SingleChoice, "Pick one",
            isRequired: true,
            configJson: ChoiceOptions.SerializeQuestionOptions(new[] { "Option A", "Option B" }),
            TestNow);
        questions.Seed(q1);

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, questions);
        // Submit with empty list instead of null
        var result = await handler.HandleAsync(
            new SubmitAnswerCommand(TestTelegramUserId, Text: null, SelectedOptionIndexes: new int[] { }));

        Assert.False(result.Completed);
        Assert.Equal(q1.Id, result.QuestionId);

        var updated = apps.Store[1];
        Assert.Empty(updated.Answers);
        Assert.Equal(q1.Id, updated.Session!.CurrentQuestionId);
    }

    [Fact]
    public async Task Survey_Completion_Enqueues_SendAdminCard_With_ThreeButtons()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();
        var queue = new FakeTelegramCommandQueue();

        // Seed a Text question as the last (only) question
        var q1 = Question.Create(1, QuestionType.Text, "Last question", isRequired: true, configJson: null, TestNow);
        questions.Seed(q1);

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        // Seed a user
        var user = TelegramUser.FirstSighting(
            TestTelegramUserId, isBot: false, isPremium: false,
            username: "alice", firstName: "Alice", lastName: "Smith",
            languageCode: "en", bio: "A bio", photoFileId: null,
            normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        var userRepo = new FakeUserRepository();
        userRepo.Seed(user);

        var handler = CreateHandler(apps, questions, userRepo, queue);
        var result = await handler.HandleAsync(
            new SubmitAnswerCommand(TestTelegramUserId, Text: "Final answer", null));

        Assert.True(result.Completed);

        // Verify one command enqueued
        var cmd = Assert.Single(queue.Enqueued);
        Assert.Equal(TelegramCommandType.SendAdminCard, cmd.Type);

        // Deserialize and verify payload
        var payload = JsonSerializer.Deserialize<TelegramCommandPayload>(cmd.PayloadJson)
            ?? throw new InvalidOperationException("Payload should deserialize");
        Assert.Equal(-5000, payload.ChatId);
        Assert.NotNull(payload.Buttons);
        Assert.Equal(3, payload.Buttons.Count);

        // Verify button callback data
        Assert.Equal($"appr:{application.Id}", payload.Buttons[0].CallbackData);
        Assert.Equal($"rej:{application.Id}", payload.Buttons[1].CallbackData);
        Assert.Equal($"ans:{application.Id}", payload.Buttons[2].CallbackData);
    }

    [Fact]
    public async Task Survey_Completion_Without_AdminChat_Configured_Throws()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();
        var queue = new FakeTelegramCommandQueue();

        var q1 = Question.Create(1, QuestionType.Text, "Question", isRequired: true, configJson: null, TestNow);
        questions.Seed(q1);

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        var user = TelegramUser.FirstSighting(
            TestTelegramUserId, isBot: false, isPremium: false,
            username: "bob", firstName: "Bob", lastName: null,
            languageCode: "en", bio: null, photoFileId: null,
            normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        var userRepo = new FakeUserRepository();
        userRepo.Seed(user);

        // No admin chat configured
        var directory = new FakeTenantDirectory(adminChatId: null);
        var handler = CreateHandler(apps, questions, userRepo, queue, directory);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new SubmitAnswerCommand(TestTelegramUserId, Text: "answer", null)));
        Assert.Contains("Admin chat", ex.Message);
    }

    [Fact]
    public async Task No_Active_Application_For_User_Throws()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();

        // Don't seed anything

        var handler = CreateHandler(apps, questions);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new SubmitAnswerCommand(TestTelegramUserId, Text: "answer", null)));
        Assert.Contains("No active application", ex.Message);
    }

    [Fact]
    public async Task UnitOfWork_SaveChangesAsync_Called_Once_Per_Successful_Call()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();
        var unitOfWork = new FakeUnitOfWork();

        var q1 = Question.Create(1, QuestionType.Text, "Question 1?", isRequired: true, configJson: null, TestNow);
        questions.Seed(q1);
        var q2 = Question.Create(2, QuestionType.Text, "Question 2?", isRequired: true, configJson: null, TestNow);
        questions.Seed(q2);

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(q1.Id, TestNow);
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, questions, unitOfWork: unitOfWork);
        await handler.HandleAsync(new SubmitAnswerCommand(TestTelegramUserId, Text: "answer", null));

        Assert.Equal(1, unitOfWork.SaveCount);
    }
}
