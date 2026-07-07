namespace Gatekeeper.Domain;

/// <summary>Lifecycle of a single join application. Mirrors the bot's state machine.</summary>
public enum ApplicationStatus
{
    JoinRequested,
    SurveyOffered,
    InSurvey,
    AwaitingReview,
    Approved,
    Rejected,
    Abandoned,
    Cancelled,
}

public enum SurveyState
{
    Offered,
    InProgress,
    Completed,
    Abandoned,
}

public enum QuestionType
{
    Text,
    SingleChoice,
    MultiChoice,
    Captcha,
}

/// <summary>Every admin action lands in the unified moderation log under one of these types.</summary>
public enum ModerationActionType
{
    Approve,
    Reject,
    Ban,
    Unban,
    Mute,
    Unmute,
    Kick,
    Warn,
}

/// <summary>Where an action originated. Used for audit and for the "Telegram wins" priority rule.</summary>
public enum ActionSource
{
    TelegramGroup,
    Web,
    System,
}

public enum TenantChatRole
{
    MainGroup,
    AdminGroup,
}

/// <summary>Outbound Telegram operations are persisted as commands and executed solely by the bot.</summary>
public enum TelegramCommandType
{
    ApproveJoinRequest,
    DeclineJoinRequest,
    SendMessage,
    EditMessage,
    BanUser,
    UnbanUser,
    RestrictUser,
    SendAdminCard,  // appended, not inserted — preserves existing numeric values already persisted
}

public enum TelegramCommandStatus
{
    Pending,
    InFlight,
    Succeeded,
    Failed,
}
