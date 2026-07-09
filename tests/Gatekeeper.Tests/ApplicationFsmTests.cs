using Gatekeeper.Domain;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for the Application aggregate's finite state machine and domain invariants.
/// Ensures all status transitions follow the rules and invalid transitions throw DomainException.
/// </summary>
public class ApplicationFsmTests
{
    private const long TestTelegramUserId = 123456789L;
    private const long TestMainChatId = -987654321L;
    private static readonly long? TestUserChatId = 111222333L;
    private const string TestInviteLink = "https://t.me/testgroup";
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FromJoinRequest_Creates_Application_In_JoinRequested_Status()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);

        Assert.Equal(ApplicationStatus.JoinRequested, application.Status);
        Assert.Equal(TestTelegramUserId, application.TelegramUserId);
        Assert.Equal(TestMainChatId, application.MainChatId);
        Assert.Equal(TestUserChatId, application.UserChatId);
        Assert.Equal(TestInviteLink, application.InviteLinkUsed);
        Assert.Equal(TestNow, application.CreatedAt);
    }

    [Fact]
    public void MarkSurveyOffered_Transitions_From_JoinRequested_To_SurveyOffered()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);

        application.MarkSurveyOffered();

        Assert.Equal(ApplicationStatus.SurveyOffered, application.Status);
    }

    [Fact]
    public void MarkSurveyOffered_Throws_When_Called_From_SurveyOffered()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();

        var ex = Assert.Throws<DomainException>(() => application.MarkSurveyOffered());
        Assert.Contains("Expected status", ex.Message);
    }

    [Fact]
    public void StartSurvey_Requires_SurveyOffered_Status()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);

        var ex = Assert.Throws<DomainException>(() => application.StartSurvey(1L, TestNow));
        Assert.Contains("Expected status", ex.Message);
    }

    [Fact]
    public void StartSurvey_Succeeds_From_SurveyOffered()
    {
        const long firstQuestionId = 42L;
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();

        application.StartSurvey(firstQuestionId, TestNow);

        Assert.Equal(ApplicationStatus.InSurvey, application.Status);
        Assert.NotNull(application.Session);
        Assert.Equal(firstQuestionId, application.Session.CurrentQuestionId);
        Assert.Equal(1, application.Session.CurrentPosition);
    }

    [Fact]
    public void AddAnswer_With_NextQuestionId_Stays_InSurvey_And_Advances()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);

        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: 2L, TestNow);

        Assert.Equal(ApplicationStatus.InSurvey, application.Status);
        Assert.NotNull(application.Session);
        Assert.Equal(2L, application.Session.CurrentQuestionId);
        Assert.Equal(2, application.Session.CurrentPosition);
    }

    [Fact]
    public void AddAnswer_With_Null_NextQuestionId_Transitions_To_AwaitingReview()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);

        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);

        Assert.Equal(ApplicationStatus.AwaitingReview, application.Status);
        Assert.Equal(TestNow, application.SubmittedAt);
        Assert.NotNull(application.Session);
        Assert.Null(application.Session.CurrentQuestionId);
        Assert.Equal(SurveyState.Completed, application.Session.State);
    }

    [Fact]
    public void AddAnswer_Throws_When_Not_InSurvey()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);

        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        var ex = Assert.Throws<DomainException>(() => application.AddAnswer(answer, 2L, TestNow));
        Assert.Contains("Expected status", ex.Message);
    }

    [Fact]
    public void Approve_Requires_AwaitingReview_Status()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);

        var ex = Assert.Throws<DomainException>(() => application.Approve(TestNow));
        Assert.Contains("Expected status", ex.Message);
    }

    [Fact]
    public void Approve_Succeeds_From_AwaitingReview()
    {
        var approvalTime = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);
        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);

        application.Approve(approvalTime);

        Assert.Equal(ApplicationStatus.Approved, application.Status);
        Assert.Equal(approvalTime, application.DecidedAt);
    }

    [Fact]
    public void Approve_Throws_When_Already_Approved()
    {
        var approvalTime = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);
        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);
        application.Approve(approvalTime);

        var ex = Assert.Throws<DomainException>(() => application.Approve(approvalTime));
        Assert.Contains("Expected status", ex.Message);
    }

    [Fact]
    public void Reject_Requires_AwaitingReview_Status()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);

        var ex = Assert.Throws<DomainException>(() => application.Reject(TestNow));
        Assert.Contains("Expected status", ex.Message);
    }

    [Fact]
    public void Reject_Succeeds_From_AwaitingReview()
    {
        var rejectionTime = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);
        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);

        application.Reject(rejectionTime);

        Assert.Equal(ApplicationStatus.Rejected, application.Status);
        Assert.Equal(rejectionTime, application.DecidedAt);
    }

    [Fact]
    public void Reject_Throws_When_Already_Rejected()
    {
        var rejectionTime = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);
        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);
        application.Reject(rejectionTime);

        var ex = Assert.Throws<DomainException>(() => application.Reject(rejectionTime));
        Assert.Contains("Expected status", ex.Message);
    }

    [Fact]
    public void Cancel_Succeeds_From_JoinRequested()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);

        application.Cancel();

        Assert.Equal(ApplicationStatus.Cancelled, application.Status);
    }

    [Fact]
    public void Cancel_Succeeds_From_SurveyOffered()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();

        application.Cancel();

        Assert.Equal(ApplicationStatus.Cancelled, application.Status);
    }

    [Fact]
    public void Cancel_Succeeds_From_InSurvey()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);

        application.Cancel();

        Assert.Equal(ApplicationStatus.Cancelled, application.Status);
    }

    [Fact]
    public void Cancel_Succeeds_From_AwaitingReview()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);
        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);

        application.Cancel();

        Assert.Equal(ApplicationStatus.Cancelled, application.Status);
    }

    [Fact]
    public void Cancel_Throws_From_Approved()
    {
        var approvalTime = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);
        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);
        application.Approve(approvalTime);

        var ex = Assert.Throws<DomainException>(() => application.Cancel());
        Assert.Contains("Cannot cancel a decided application", ex.Message);
    }

    [Fact]
    public void Cancel_Throws_From_Rejected()
    {
        var rejectionTime = new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);
        var answer = Answer.Create(1L, "Question 1?", QuestionType.Text, 1, "Answer text", null, TestNow);
        application.AddAnswer(answer, nextQuestionId: null, TestNow);
        application.Reject(rejectionTime);

        var ex = Assert.Throws<DomainException>(() => application.Cancel());
        Assert.Contains("Cannot cancel a decided application", ex.Message);
    }

    [Fact]
    public void Abandon_Requires_SurveyOffered_Or_InSurvey()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);

        var ex = Assert.Throws<DomainException>(() => application.Abandon(TestNow));
        Assert.Contains("Cannot abandon from", ex.Message);
    }

    [Fact]
    public void Abandon_Succeeds_From_SurveyOffered()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();

        application.Abandon(TestNow);

        Assert.Equal(ApplicationStatus.Abandoned, application.Status);
    }

    [Fact]
    public void Abandon_Succeeds_From_InSurvey()
    {
        var application = Application.FromJoinRequest(
            TestTelegramUserId, TestMainChatId, TestUserChatId, TestInviteLink, TestNow);
        application.MarkSurveyOffered();
        application.StartSurvey(1L, TestNow);

        application.Abandon(TestNow);

        Assert.Equal(ApplicationStatus.Abandoned, application.Status);
        Assert.Equal(SurveyState.Abandoned, application.Session!.State);
    }
}
