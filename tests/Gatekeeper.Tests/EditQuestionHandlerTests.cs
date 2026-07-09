using Gatekeeper.Application;
using Gatekeeper.Domain;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for Question.Activate()/Deactivate() and EditQuestionHandler's reactivation toggle — until
/// this feature, nothing could ever flip a deactivated question's IsActive back to true (the site's
/// "Скрыть" quick-action could only turn it off, with no way back short of editing the database).
/// </summary>
public class EditQuestionHandlerTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Question_Deactivate_Then_Activate_Restores_IsActive()
    {
        var question = Question.Create(1, QuestionType.Text, "Q?", isRequired: true, configJson: null, TestNow);
        Assert.True(question.IsActive);

        var deactivatedAt = TestNow.AddDays(1);
        question.Deactivate(deactivatedAt);
        Assert.False(question.IsActive);

        var reactivatedAt = TestNow.AddDays(2);
        question.Activate(reactivatedAt);

        Assert.True(question.IsActive);
        Assert.Equal(reactivatedAt, question.UpdatedAt);
    }

    [Fact]
    public async Task EditQuestionHandler_Reactivates_A_Previously_Deactivated_Question()
    {
        var questions = new FakeQuestionRepository();
        var question = Question.Create(1, QuestionType.Text, "Q?", isRequired: true, configJson: null, TestNow);
        question.Deactivate(TestNow);
        questions.Seed(question);

        var handler = new EditQuestionHandler(questions, new FakeUnitOfWork(), new FakeClock(TestNow.AddDays(1)));
        await handler.HandleAsync(new EditQuestionCommand(
            question.Id, QuestionType.Text, "Q?", IsRequired: true, ConfigJson: null, IsActive: true));

        Assert.True(questions.Store.Single(q => q.Id == question.Id).IsActive);
    }

    [Fact]
    public async Task EditQuestionHandler_Deactivates_An_Active_Question_When_Asked()
    {
        var questions = new FakeQuestionRepository();
        var question = Question.Create(1, QuestionType.Text, "Q?", isRequired: true, configJson: null, TestNow);
        questions.Seed(question);

        var handler = new EditQuestionHandler(questions, new FakeUnitOfWork(), new FakeClock(TestNow.AddDays(1)));
        await handler.HandleAsync(new EditQuestionCommand(
            question.Id, QuestionType.Text, "Q?", IsRequired: true, ConfigJson: null, IsActive: false));

        Assert.False(questions.Store.Single(q => q.Id == question.Id).IsActive);
    }

    [Fact]
    public async Task EditQuestionHandler_Leaves_IsActive_Alone_When_Unchanged()
    {
        var questions = new FakeQuestionRepository();
        var question = Question.Create(1, QuestionType.Text, "Old prompt", isRequired: true, configJson: null, TestNow);
        questions.Seed(question);

        var handler = new EditQuestionHandler(questions, new FakeUnitOfWork(), new FakeClock(TestNow.AddDays(1)));
        await handler.HandleAsync(new EditQuestionCommand(
            question.Id, QuestionType.Text, "New prompt", IsRequired: true, ConfigJson: null, IsActive: true));

        var updated = questions.Store.Single(q => q.Id == question.Id);
        Assert.True(updated.IsActive);
        Assert.Equal("New prompt", updated.PromptText);
    }
}
