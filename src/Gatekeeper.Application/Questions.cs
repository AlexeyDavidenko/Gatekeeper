using Gatekeeper.Domain;

namespace Gatekeeper.Application;

// Only QuestionType.Text is wired up end to end (the bot always sends the prompt as plain text and
// accepts free text back) — SingleChoice/MultiChoice/Captcha exist on the domain/DB but have no
// rendering or answer-parsing behind them yet, so question management doesn't expose Type at all.

public sealed record CreateQuestionCommand(string PromptText, bool IsRequired);
public sealed record EditQuestionCommand(long QuestionId, string PromptText, bool IsRequired);
public sealed record MoveQuestionCommand(long QuestionId, bool Up);
public sealed record DeactivateQuestionCommand(long QuestionId);

public sealed class CreateQuestionHandler(IQuestionRepository questions, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task<long> HandleAsync(CreateQuestionCommand cmd, CancellationToken ct = default)
    {
        var now = clock.UtcNow;
        var all = await questions.GetAllAsync(ct);
        var nextPosition = all.Count == 0 ? 1 : all.Max(q => q.Position) + 1;

        var question = Question.Create(nextPosition, QuestionType.Text, cmd.PromptText, cmd.IsRequired, configJson: null, now);
        await questions.AddAsync(question, ct);
        await unitOfWork.SaveChangesAsync(ct);
        return question.Id;
    }
}

public sealed class EditQuestionHandler(IQuestionRepository questions, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task HandleAsync(EditQuestionCommand cmd, CancellationToken ct = default)
    {
        var question = await questions.GetByIdAsync(cmd.QuestionId, ct)
            ?? throw new InvalidOperationException($"Question {cmd.QuestionId} not found.");
        question.Edit(cmd.PromptText, cmd.IsRequired, question.ConfigJson, clock.UtcNow);
        await unitOfWork.SaveChangesAsync(ct);
    }
}

/// <summary>Swaps Position with the adjacent question (by Position, across active and inactive alike).</summary>
public sealed class MoveQuestionHandler(IQuestionRepository questions, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task HandleAsync(MoveQuestionCommand cmd, CancellationToken ct = default)
    {
        var ordered = await questions.GetAllAsync(ct);
        var index = -1;
        for (var i = 0; i < ordered.Count; i++)
            if (ordered[i].Id == cmd.QuestionId) { index = i; break; }
        if (index < 0) throw new InvalidOperationException($"Question {cmd.QuestionId} not found.");

        var swapIndex = cmd.Up ? index - 1 : index + 1;
        if (swapIndex < 0 || swapIndex >= ordered.Count) return;  // already at the edge — no-op

        var now = clock.UtcNow;
        var a = ordered[index];
        var b = ordered[swapIndex];
        (var aPos, var bPos) = (a.Position, b.Position);
        a.MoveTo(bPos, now);
        b.MoveTo(aPos, now);
        await unitOfWork.SaveChangesAsync(ct);
    }
}

public sealed class DeactivateQuestionHandler(IQuestionRepository questions, IUnitOfWork unitOfWork, IClock clock)
{
    public async Task HandleAsync(DeactivateQuestionCommand cmd, CancellationToken ct = default)
    {
        var question = await questions.GetByIdAsync(cmd.QuestionId, ct)
            ?? throw new InvalidOperationException($"Question {cmd.QuestionId} not found.");
        question.Deactivate(clock.UtcNow);
        await unitOfWork.SaveChangesAsync(ct);
    }
}
