namespace Gatekeeper.Contracts;

// Plain DTOs only — this assembly has NO dependency on Telegram.Bot, EF, or Domain.
// The bot maps Telegram types into these before calling the API; the web uses them directly.

public sealed record TelegramUserDto(
    long Id, bool IsBot, bool IsPremium, string? Username,
    string? FirstName, string? LastName, string? LanguageCode, string? PhotoFileId = null);

public sealed record CreateApplicationRequest(
    long ChatId, TelegramUserDto User, long? UserChatId, string? InviteLink, string? Bio);

public sealed record SubmitAnswerRequest(long TelegramUserId, string? Text);

public sealed record NextQuestionDto(long? QuestionId, string? Prompt, string Type, bool Completed);

public sealed record DecideRequest(
    bool Approve, string? Reason, long ActingUserId, string? ActingUserName, string Source);

public sealed record ModerationRequest(
    string Action, string? Reason, string? Notes, long ChatId,
    long ActingUserId, string? ActingUserName, string Source, DateTimeOffset? ExpiresAt);

public sealed record AdminCallbackRequest(
    long AdminChatId, long MessageId, long ActingUserId, string? ActingUserName, string Data);

// --- Admin site read models ---

public sealed record ApplicationSummary(
    long Id, long TelegramUserId, string? Username, string? DisplayName,
    string Status, DateTimeOffset? SubmittedAt, string? PhotoFileId);

public sealed record ApplicationCard(
    long Id, long TelegramUserId, string? Username, string? DisplayName,
    string Status, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt,
    uint RowVersion, IReadOnlyList<AnswerDto> Answers, string? Bio, string? PhotoFileId);

public sealed record AnswerDto(string Prompt, string Type, int Position, string? Text);

// Question management. Only plain text prompts are exposed here — the survey engine has no
// rendering/answer-parsing for the other QuestionType values yet (see docs/backlog.md).
public sealed record QuestionDto(long Id, int Position, string PromptText, bool IsRequired, bool IsActive);
public sealed record CreateQuestionRequest(string PromptText, bool IsRequired);
public sealed record EditQuestionRequest(string PromptText, bool IsRequired);

public sealed record ModerationLogEntry(
    long Id, long TelegramUserId, string? Username, string? DisplayName, long? ApplicationId,
    string Action, string? Reason, string? Notes, long PerformedByUserId, string? PerformedByName,
    string Source, DateTimeOffset CreatedAt);

public sealed record DashboardSummary(
    int PendingCount,
    int ApprovedToday, int RejectedToday,
    int ApprovedWeek, int RejectedWeek,
    int OutboxPending, int OutboxInFlight, int OutboxFailed,
    IReadOnlyList<ModerationLogEntry> RecentDecisions);

// --- Tenant provisioning (control-plane, tenant-agnostic) ---

public sealed record ProvisionTenantRequest(
    string Slug, string Name, long MainChatId, long AdminChatId,
    string? MainChatTitle, string? AdminChatTitle);

public sealed record ProvisionTenantResponse(long TenantId, string DatabaseName, bool WasCreated);

// --- Outbox drain (control-plane, tenant-agnostic) ---

public sealed record PendingCommand(
    long TenantId, long CommandId, string Type, long ChatId, long UserId, string? Text, long? MessageId,
    IReadOnlyList<CommandButton>? Buttons, string? PhotoFileId = null, bool IsPhotoCaption = false);

public sealed record CommandButton(string Text, string CallbackData);

public sealed record CommandResult(bool Success, string? Error, long? MessageId = null);

/// <summary>An applicant whose survey is still in progress (SurveyOffered/InSurvey) — used to
/// rehydrate the bot's in-memory TenantRouter after a restart.</summary>
public sealed record InProgressApplicant(long TenantId, long TelegramUserId);

/// <summary>The only surface Bot and Web use to reach data — they never touch the database.</summary>
public interface IGatekeeperApiClient
{
    Task<NextQuestionDto> CreateApplicationAsync(long tenantId, CreateApplicationRequest request, CancellationToken ct = default);
    Task<NextQuestionDto> SubmitAnswerAsync(long tenantId, SubmitAnswerRequest request, CancellationToken ct = default);
    Task DecideAsync(long tenantId, long applicationId, uint rowVersion, DecideRequest request, CancellationToken ct = default);
    Task ModerateAsync(long tenantId, long telegramUserId, ModerationRequest request, CancellationToken ct = default);
    Task HandleAdminCallbackAsync(long tenantId, AdminCallbackRequest request, CancellationToken ct = default);

    Task<(long TenantId, string Role)?> ResolveTenantByChatAsync(long chatId, CancellationToken ct = default);
    Task<IReadOnlyList<PendingCommand>> GetPendingTelegramCommandsAsync(int batch, CancellationToken ct = default);
    Task AckTelegramCommandAsync(long tenantId, long commandId, CommandResult result, CancellationToken ct = default);
    Task<IReadOnlyList<InProgressApplicant>> GetInProgressApplicantsAsync(CancellationToken ct = default);
}
