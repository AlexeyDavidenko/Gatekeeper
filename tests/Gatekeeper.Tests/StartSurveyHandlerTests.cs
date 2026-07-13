using Gatekeeper.Application;
using Gatekeeper.Domain;
using Xunit;
using DomainApplication = Gatekeeper.Domain.Application;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for StartSurveyHandler — the explicit "start the survey" trigger the language-picker's
/// "lang:go"/"lang:ru"/"lang:en" callbacks now call, replacing the old implicit auto-start that used
/// to let a stray message bypass the language picker entirely (see SubmitAnswerHandlerTests'
/// SubmitAnswer_Throws_SurveyNotStartedException_When_Still_At_SurveyOffered for the other half of
/// this fix).
/// </summary>
public class StartSurveyHandlerTests
{
    private const long TestTelegramUserId = 123456789L;
    private const long TestMainChatId = -987654321L;
    private const long TestUserChatId = 111222333L;
    private const string TestInviteLink = "https://t.me/testgroup";
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static StartSurveyHandler CreateHandler(
        FakeApplicationRepository apps, FakeQuestionRepository questions, FakeUserRepository? users = null) =>
        new(apps, questions, users ?? new FakeUserRepository(), new FakeUnitOfWork(), new FakeClock(TestNow));

    [Fact]
    public async Task Transitions_SurveyOffered_To_InSurvey_And_Returns_First_Question()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        apps.Seed(application, id: 1);

        var q1 = Question.Create(1, QuestionType.Text, "Question 1?", isRequired: true, configJson: null, TestNow);
        questions.Seed(q1);

        var handler = CreateHandler(apps, questions);
        var result = await handler.HandleAsync(new StartSurveyCommand(TestTelegramUserId));

        Assert.False(result.Completed);
        Assert.Equal(q1.Id, result.QuestionId);
        Assert.Equal(ApplicationStatus.InSurvey, apps.Store[1].Status);
        Assert.NotNull(apps.Store[1].Session);
        Assert.Equal(q1.Id, apps.Store[1].Session!.CurrentQuestionId);
    }

    [Fact]
    public async Task Is_Idempotent_When_Already_InSurvey()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        var q1 = Question.Create(1, QuestionType.Text, "Question 1?", isRequired: true, configJson: null, TestNow);
        questions.Seed(q1);
        application.StartSurvey(q1.Id, TestNow);   // already started, e.g. via a normal answer already submitted
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, questions);

        // A second "lang:go"/"lang:ru" tap (double-tap, or tapping the stale welcome message again
        // after the survey already progressed via free text) must not throw or reset progress.
        var result = await handler.HandleAsync(new StartSurveyCommand(TestTelegramUserId));

        Assert.False(result.Completed);
        Assert.Equal(q1.Id, result.QuestionId);
        Assert.Equal(ApplicationStatus.InSurvey, apps.Store[1].Status);
    }

    [Fact]
    public async Task Returns_English_Prompt_And_Options_For_An_English_Language_User()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();
        var users = new FakeUserRepository();

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        apps.Seed(application, id: 1);

        var ru = ChoiceOptions.SerializeQuestionOptions(["Да", "Нет"]);
        var en = ChoiceOptions.SerializeQuestionOptions(["Yes", "No"]);
        var q1 = Question.Create(1, QuestionType.SingleChoice, "Согласны?", isRequired: true, configJson: ru, TestNow,
            promptTextEn: "Agree?", configJsonEn: en);
        questions.Seed(q1);

        var user = TelegramUser.FirstSighting(TestTelegramUserId, isBot: false, isPremium: false,
            username: "bob", firstName: "Bob", lastName: null, languageCode: "en_US",
            bio: null, photoFileId: null, normalize: (f, l) => $"{f} {l}".Trim().ToLowerInvariant(), TestNow);
        users.Seed(user);

        var handler = CreateHandler(apps, questions, users);
        var result = await handler.HandleAsync(new StartSurveyCommand(TestTelegramUserId));

        Assert.Equal("Agree?", result.Prompt);
        Assert.Equal(["Yes", "No"], result.Options);
    }

    [Fact]
    public async Task Throws_When_No_Active_Questions_Configured()
    {
        var apps = new FakeApplicationRepository();
        var questions = new FakeQuestionRepository();   // empty — no active questions

        var application = DomainApplication.FromJoinRequest(TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        apps.Seed(application, id: 1);

        var handler = CreateHandler(apps, questions);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(new StartSurveyCommand(TestTelegramUserId)));
    }
}
