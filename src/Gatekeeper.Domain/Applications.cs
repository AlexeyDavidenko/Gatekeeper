namespace Gatekeeper.Domain;

/// <summary>
/// One attempt by a user to join. The aggregate enforces the state machine; no caller can
/// move it through an illegal transition. RowVersion maps to Postgres xmin for the
/// "Telegram-priority" race guard between the group buttons and the website.
/// </summary>
public sealed class Application : AggregateRoot
{
    private readonly List<Answer> _answers = [];

    public long TelegramUserId { get; private set; }
    public long? IdentitySnapshotId { get; private set; }
    public long MainChatId { get; private set; }
    public long? UserChatId { get; private set; }      // ChatJoinRequest.user_chat_id, used to DM the applicant
    public string? InviteLinkUsed { get; private set; }
    public ApplicationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? DecidedAt { get; private set; }
    public uint RowVersion { get; private set; }       // concurrency token (xmin)

    public SurveySession? Session { get; private set; }
    public IReadOnlyCollection<Answer> Answers => _answers.AsReadOnly();

    private Application() { }

    public static Application FromJoinRequest(
        long telegramUserId, long mainChatId, long? userChatId, string? inviteLink, DateTimeOffset now) => new()
    {
        TelegramUserId = telegramUserId,
        MainChatId = mainChatId,
        UserChatId = userChatId,
        InviteLinkUsed = inviteLink,
        Status = ApplicationStatus.JoinRequested,
        CreatedAt = now,
    };

    public void LinkIdentitySnapshot(long snapshotId) => IdentitySnapshotId = snapshotId;

    public void MarkSurveyOffered() => Transition(ApplicationStatus.JoinRequested, ApplicationStatus.SurveyOffered);

    public void StartSurvey(long firstQuestionId, DateTimeOffset now)
    {
        Transition(ApplicationStatus.SurveyOffered, ApplicationStatus.InSurvey);
        Session = SurveySession.Start(Id, firstQuestionId, now);
    }

    /// <summary>Records an answer and either advances to the next question or submits for review.</summary>
    public void AddAnswer(Answer answer, long? nextQuestionId, DateTimeOffset now)
    {
        EnsureStatus(ApplicationStatus.InSurvey);
        _answers.Add(answer);

        if (nextQuestionId is null)
        {
            Session!.Complete(now);
            Status = ApplicationStatus.AwaitingReview;
            SubmittedAt = now;
        }
        else
        {
            Session!.Advance(nextQuestionId.Value, now);
        }
    }

    public void Approve(DateTimeOffset now) => Decide(ApplicationStatus.Approved, now);
    public void Reject(DateTimeOffset now) => Decide(ApplicationStatus.Rejected, now);

    public void Abandon(DateTimeOffset now)
    {
        if (Status is not (ApplicationStatus.SurveyOffered or ApplicationStatus.InSurvey))
            throw new DomainException($"Cannot abandon from {Status}.");
        Status = ApplicationStatus.Abandoned;
        Session?.Abandon(now);
    }

    public void Cancel()  // join request withdrawn or user left before a decision
    {
        if (Status is ApplicationStatus.Approved or ApplicationStatus.Rejected)
            throw new DomainException($"Cannot cancel a decided application ({Status}).");
        Status = ApplicationStatus.Cancelled;
    }

    private void Decide(ApplicationStatus target, DateTimeOffset now)
    {
        EnsureStatus(ApplicationStatus.AwaitingReview);
        Status = target;
        DecidedAt = now;
    }

    private void Transition(ApplicationStatus from, ApplicationStatus to)
    {
        EnsureStatus(from);
        Status = to;
    }

    private void EnsureStatus(ApplicationStatus expected)
    {
        if (Status != expected)
            throw new DomainException($"Expected status {expected} but was {Status}.");
    }
}

public sealed class SurveySession : Entity
{
    public long ApplicationId { get; private set; }
    public long? CurrentQuestionId { get; private set; }
    public int CurrentPosition { get; private set; }
    public SurveyState State { get; private set; }
    public DateTimeOffset LastInteractionAt { get; private set; }

    private SurveySession() { }

    public static SurveySession Start(long applicationId, long firstQuestionId, DateTimeOffset now) => new()
    {
        ApplicationId = applicationId,
        CurrentQuestionId = firstQuestionId,
        CurrentPosition = 1,
        State = SurveyState.InProgress,
        LastInteractionAt = now,
    };

    public void Advance(long nextQuestionId, DateTimeOffset now)
    {
        CurrentQuestionId = nextQuestionId;
        CurrentPosition++;
        LastInteractionAt = now;
    }

    public void Complete(DateTimeOffset now)
    {
        CurrentQuestionId = null;
        State = SurveyState.Completed;
        LastInteractionAt = now;
    }

    public void Abandon(DateTimeOffset now)
    {
        State = SurveyState.Abandoned;
        LastInteractionAt = now;
    }
}

/// <summary>
/// An answer carries a snapshot of the question (text/type/position) at answer time, so editing or
/// reordering the live questions later never corrupts the historical record.
/// </summary>
public sealed class Answer : Entity
{
    public long ApplicationId { get; private set; }
    public long? QuestionId { get; private set; }      // nullable: question may be soft-deleted later
    public string PromptSnapshot { get; private set; } = default!;
    public QuestionType TypeSnapshot { get; private set; }
    public int PositionSnapshot { get; private set; }
    public string? Text { get; private set; }
    public string? OptionsJson { get; private set; }   // for choice answers (jsonb)
    public DateTimeOffset AnsweredAt { get; private set; }

    private Answer() { }

    public static Answer Create(
        long questionId, string promptSnapshot, QuestionType type, int position,
        string? text, string? optionsJson, DateTimeOffset now) => new()
    {
        QuestionId = questionId,
        PromptSnapshot = promptSnapshot,
        TypeSnapshot = type,
        PositionSnapshot = position,
        Text = text,
        OptionsJson = optionsJson,
        AnsweredAt = now,
    };
}
