namespace Gatekeeper.Contracts;

// Plain DTOs only — this assembly has NO dependency on Telegram.Bot, EF, or Domain.
// The bot maps Telegram types into these before calling the API; the web uses them directly.

public sealed record TelegramUserDto(
    long Id, bool IsBot, bool IsPremium, string? Username,
    string? FirstName, string? LastName, string? LanguageCode, string? PhotoFileId = null);

public sealed record CreateApplicationRequest(
    long ChatId, TelegramUserDto User, long? UserChatId, string? InviteLink, string? Bio);

public sealed record SubmitAnswerRequest(long TelegramUserId, string? Text, IReadOnlyList<int>? SelectedOptionIndexes = null);

public sealed record NextQuestionDto(
    long? QuestionId, string? Prompt, string Type, bool Completed,
    string? LanguageCode = null, IReadOnlyList<string>? Options = null);

public sealed record SetLanguageRequest(string LanguageCode);

public sealed record DecideRequest(
    bool Approve, string? Reason, long ActingUserId, string? ActingUserName, string Source);

public sealed record CancelApplicationRequest(long ActingUserId, string? ActingUserName);

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

// Question management. Type is "Text"/"SingleChoice"/"MultiChoice" — Captcha exists on the domain
// but has no rendering/answer-parsing behind it, so it's not exposed as a choosable type here.
// Options carries the choice labels (null/empty for Text questions).
public sealed record QuestionDto(
    long Id, int Position, string PromptText, bool IsRequired, bool IsActive,
    string Type, IReadOnlyList<string>? Options);
public sealed record CreateQuestionRequest(string Type, string PromptText, bool IsRequired, IReadOnlyList<string>? Options);
public sealed record EditQuestionRequest(string Type, string PromptText, bool IsRequired, IReadOnlyList<string>? Options, bool IsActive);

public sealed record ModerationLogEntry(
    long Id, long TelegramUserId, string? Username, string? DisplayName, long? ApplicationId,
    string Action, string? Reason, string? Notes, long PerformedByUserId, string? PerformedByName,
    string Source, DateTimeOffset CreatedAt, bool IsArchived, DateTimeOffset? ArchivedAt);

// Owner-triggered bulk cleanup of old moderation-log entries — no automatic/scheduled purge exists.
public sealed record ArchiveModerationActionsRequest(DateTimeOffset OlderThan);
public sealed record ArchiveModerationActionsResponse(int ArchivedCount);

// Site visitor log — page views against the admin site itself, not moderation actions.
public sealed record RecordSiteVisitRequest(
    long? UserId, string? UserName, string SessionId, string? IpAddress, string Path, string Method,
    int StatusCode, long DurationMs, string? UserAgent, string? Referrer);

public sealed record SiteVisitDto(
    long Id, long? UserId, string? UserName, string SessionId, string? IpAddress, string Path, string Method,
    int StatusCode, long DurationMs, string? UserAgent, string? Referrer, DateTimeOffset CreatedAt);

public sealed record DashboardSummary(
    int PendingCount,
    int ApprovedToday, int RejectedToday,
    int ApprovedWeek, int RejectedWeek,
    int OutboxPending, int OutboxInFlight, int OutboxFailed,
    IReadOnlyList<ModerationLogEntry> RecentDecisions);

// The bot's own "/status" DM command — a cross-tenant lookup (see the endpoint doc comment), so
// unlike everything else here it carries no tenant id: the caller doesn't need one to show it.
public sealed record LatestApplicationStatusDto(long TenantId, string Status, DateTimeOffset? SubmittedAt);

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
    // Null means the survey hasn't been explicitly started yet (409 from the API) — a stray message
    // sent instead of tapping a language-picker button, not a real answer. See StartSurveyAsync.
    Task<NextQuestionDto?> SubmitAnswerAsync(long tenantId, SubmitAnswerRequest request, CancellationToken ct = default);
    Task DecideAsync(long tenantId, long applicationId, uint rowVersion, DecideRequest request, CancellationToken ct = default);
    // Admin-driven mid-survey cancel — bot's "/cancel {id}" — see CancelApplicationHandler.
    Task CancelApplicationAsync(long tenantId, long applicationId, CancelApplicationRequest request, CancellationToken ct = default);
    Task ModerateAsync(long tenantId, long telegramUserId, ModerationRequest request, CancellationToken ct = default);
    Task HandleAdminCallbackAsync(long tenantId, AdminCallbackRequest request, CancellationToken ct = default);

    Task<(long TenantId, string Role)?> ResolveTenantByChatAsync(long chatId, CancellationToken ct = default);
    Task<IReadOnlyList<PendingCommand>> GetPendingTelegramCommandsAsync(int batch, CancellationToken ct = default);
    Task AckTelegramCommandAsync(long tenantId, long commandId, CommandResult result, CancellationToken ct = default);
    Task<IReadOnlyList<InProgressApplicant>> GetInProgressApplicantsAsync(CancellationToken ct = default);

    // Bot-side language picker: persisting an explicit language choice so later messages —
    // including ones sent by a different process, like a decision made from the website — pick it up.
    Task SetUserLanguageAsync(long tenantId, long telegramUserId, string languageCode, CancellationToken ct = default);

    // Explicitly starts the survey (SurveyOffered → InSurvey) — called only from the language-picker's
    // "lang:go"/"lang:ru"/"lang:en" callbacks, never implicitly from a plain message.
    Task<NextQuestionDto> StartSurveyAsync(long tenantId, long telegramUserId, CancellationToken ct = default);

    // Admin-group bot commands ("/stats", "/pending", "/find") and the applicant's own "/status".
    Task<DashboardSummary> GetDashboardAsync(long tenantId, CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationSummary>> GetApplicationsByStatusAsync(long tenantId, string status, CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationSummary>> SearchApplicationsAsync(long tenantId, string query, CancellationToken ct = default);
    Task<LatestApplicationStatusDto?> GetLatestApplicationStatusAsync(long telegramUserId, CancellationToken ct = default);
}
