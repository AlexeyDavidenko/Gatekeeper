using Gatekeeper.Domain;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>Tests for Question's bilingual accessors — PromptTextFor/OptionsFor are the single
/// source of truth for what an applicant sees in their resolved language ("ru"/"en").</summary>
public class SurveysTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void PromptTextFor_Returns_English_When_Present_And_Requested()
    {
        var q = Question.Create(1, QuestionType.Text, "Русский?", isRequired: true, configJson: null, TestNow,
            promptTextEn: "English?");

        Assert.Equal("English?", q.PromptTextFor("en"));
        Assert.Equal("Русский?", q.PromptTextFor("ru"));
    }

    [Fact]
    public void PromptTextFor_Falls_Back_To_Russian_When_English_Not_Authored()
    {
        var q = Question.Create(1, QuestionType.Text, "Русский?", isRequired: true, configJson: null, TestNow);

        Assert.Equal("Русский?", q.PromptTextFor("en"));
    }

    [Fact]
    public void PromptTextFor_Falls_Back_To_Russian_When_English_Is_Blank()
    {
        var q = Question.Create(1, QuestionType.Text, "Русский?", isRequired: true, configJson: null, TestNow,
            promptTextEn: "   ");

        Assert.Equal("Русский?", q.PromptTextFor("en"));
    }

    [Fact]
    public void OptionsFor_Returns_English_Options_When_Count_Matches()
    {
        var ru = ChoiceOptions.SerializeQuestionOptions(["Красный", "Синий"]);
        var en = ChoiceOptions.SerializeQuestionOptions(["Red", "Blue"]);
        var q = Question.Create(1, QuestionType.SingleChoice, "Colour?", isRequired: true, configJson: ru, TestNow,
            configJsonEn: en);

        Assert.Equal(["Red", "Blue"], q.OptionsFor("en"));
        Assert.Equal(["Красный", "Синий"], q.OptionsFor("ru"));
    }

    [Fact]
    public void OptionsFor_Falls_Back_To_Russian_When_English_Count_Mismatches()
    {
        var ru = ChoiceOptions.SerializeQuestionOptions(["Красный", "Синий", "Зелёный"]);
        var en = ChoiceOptions.SerializeQuestionOptions(["Red", "Blue"]);   // only 2 of 3 translated
        var q = Question.Create(1, QuestionType.SingleChoice, "Colour?", isRequired: true, configJson: ru, TestNow,
            configJsonEn: en);

        // All-or-nothing: a partial translation would desync the positional answer index, so the
        // whole English list is discarded in favor of the always-consistent Russian one.
        Assert.Equal(["Красный", "Синий", "Зелёный"], q.OptionsFor("en"));
    }

    [Fact]
    public void OptionsFor_Falls_Back_To_Russian_When_English_Options_Not_Authored()
    {
        var ru = ChoiceOptions.SerializeQuestionOptions(["Красный", "Синий"]);
        var q = Question.Create(1, QuestionType.SingleChoice, "Colour?", isRequired: true, configJson: ru, TestNow);

        Assert.Equal(["Красный", "Синий"], q.OptionsFor("en"));
    }
}
