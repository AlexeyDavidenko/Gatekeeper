namespace Gatekeeper.Bot;

using Contracts;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

/// <summary>
/// Inbound side. Long polling (getUpdates) — purely outbound network, so no public ingress and the
/// dynamic home IP is irrelevant. Maps Telegram types to plain DTOs and calls the API; never touches the DB.
/// </summary>
public sealed class TelegramUpdateWorker(
    ITelegramBotClient bot,
    IGatekeeperApiClient api,
    TenantRouter router,
    TranslationCache tr,
    ILogger<TelegramUpdateWorker> log) : BackgroundService
{
    // Only these two are supported — Telegram's own language_code covers dozens, anything not
    // Russian falls back to English. Applies to every bit of bot-generated text (greeting,
    // thank-you, decision DM, admin-group command replies) — question prompts stay whatever the
    // admin typed, unlocalized.
    private static bool IsRussian(string? languageCode) =>
        languageCode?.StartsWith("ru", StringComparison.OrdinalIgnoreCase) == true;

    private static string Lang(string? languageCode) => IsRussian(languageCode) ? "ru" : "en";

    private InlineKeyboardMarkup LanguageOfferKeyboard(string lang) => new(
    [
        [
            InlineKeyboardButton.WithCallbackData(tr.Get("bot.lang.switch_button", lang), "lang:switch"),
            InlineKeyboardButton.WithCallbackData(tr.Get("bot.lang.continue_button", lang), "lang:go"),
        ],
    ]);

    // Second row lets someone who tapped "🌐 Сменить язык" by mistake (or just changed their mind)
    // get back to continuing in their already-detected language, instead of being forced to pick
    // ru/en explicitly — reuses the existing "lang:go" action, same as the "▶️ Продолжить" button
    // on the offer keyboard one step earlier.
    private InlineKeyboardMarkup LanguagePickerKeyboard(string lang) => new(
    [
        [
            InlineKeyboardButton.WithCallbackData(tr.Get("bot.lang.ru_button", lang), "lang:ru"),
            InlineKeyboardButton.WithCallbackData(tr.Get("bot.lang.en_button", lang), "lang:en"),
        ],
        [
            InlineKeyboardButton.WithCallbackData(tr.Get("bot.lang.back_button", lang), "lang:go"),
        ],
    ]);

    // One option per row, tapping immediately submits — "sc:{questionId}:{optionIndex}".
    private static InlineKeyboardMarkup SingleChoiceKeyboard(long questionId, IReadOnlyList<string> options) => new(
        options.Select((opt, i) => new[] { InlineKeyboardButton.WithCallbackData(opt, $"sc:{questionId}:{i}") }));

    // Each row toggles its own bit in an int bitmask carried in its own callback data (no bot- or
    // server-side state needed between taps — the same trick already used for the admin card's
    // show/hide-answers toggle, just extended to N options instead of one). A final row submits.
    private InlineKeyboardMarkup MultiChoiceKeyboard(long questionId, IReadOnlyList<string> options, int mask, string lang)
    {
        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        for (var i = 0; i < options.Count; i++)
        {
            var isChecked = (mask & (1 << i)) != 0;
            var label = (isChecked ? "☑ " : "☐ ") + options[i];
            rows.Add([InlineKeyboardButton.WithCallbackData(label, $"mc:{questionId}:{mask}:{i}")]);
        }
        rows.Add([InlineKeyboardButton.WithCallbackData(tr.Get("bot.mc.done_button", lang), $"mcdone:{questionId}:{mask}")]);
        return new InlineKeyboardMarkup(rows);
    }

    private static string StripCheckbox(string label) => label.Length > 2 ? label[2..] : label;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RehydrateRouterAsync(stoppingToken);

        var options = new ReceiverOptions
        {
            AllowedUpdates = [UpdateType.ChatJoinRequest, UpdateType.Message, UpdateType.CallbackQuery],
        };
        await bot.ReceiveAsync(HandleUpdateAsync, HandleErrorAsync, options, stoppingToken);
    }

    /// <summary>
    /// Repopulates the in-memory TenantRouter from applications still mid-survey. Without this, any
    /// restart of this process — a deploy, a crash, or the whole box coming back after a power/internet
    /// outage — silently strands anyone who was answering questions: their next DM would carry no
    /// tenant mapping and get dropped instead of routed. Best-effort: a failure here logs and moves on
    /// to polling rather than blocking startup, since it can be retried by the next restart.
    /// </summary>
    private async Task RehydrateRouterAsync(CancellationToken ct)
    {
        try
        {
            var applicants = await api.GetInProgressApplicantsAsync(ct);
            foreach (var applicant in applicants)
                router.Remember(applicant.TelegramUserId, applicant.TenantId);
            log.LogInformation("Rehydrated TenantRouter with {Count} in-progress applicant(s).", applicants.Count);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to rehydrate TenantRouter from in-progress applications.");
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient _, Update update, CancellationToken ct)
    {
        switch (update)
        {
            case { ChatJoinRequest: { } jr }:
            {
                var t = await api.ResolveTenantByChatAsync(jr.Chat.Id, ct);
                if (t is null) break;
                router.Remember(jr.From.Id, t.Value.TenantId);

                string? photoFileId = null;
                try
                {
                    var photos = await bot.GetUserProfilePhotos(jr.From.Id, limit: 1, cancellationToken: ct);
                    photoFileId = photos.Photos.Length > 0 ? photos.Photos[0][^1].FileId : null;
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "Failed to fetch profile photo for user {UserId}", jr.From.Id);
                }

                var first = await api.CreateApplicationAsync(t.Value.TenantId, new CreateApplicationRequest(
                    jr.Chat.Id,
                    new TelegramUserDto(jr.From.Id, jr.From.IsBot, jr.From.IsPremium,
                        jr.From.Username, jr.From.FirstName, jr.From.LastName, jr.From.LanguageCode, photoFileId),
                    jr.UserChatId, jr.InviteLink?.InviteLink, jr.Bio), ct);

                if (first.QuestionId is not null)
                {
                    // Greet + offer a language switch; the first question is sent only once the user
                    // resolves this (via the "lang:*" callback below), not from here — so it can be
                    // sent in whichever language they end up choosing.
                    var lang = Lang(jr.From.LanguageCode);
                    await bot.SendMessage(jr.From.Id, tr.Get("bot.welcome", lang), replyMarkup: LanguageOfferKeyboard(lang), cancellationToken: ct);
                }
                else if (first.Prompt is not null)
                {
                    // No-questions-configured edge case already reads as a welcome on its own —
                    // nothing to gate behind a language choice, just deliver the fallback text as-is.
                    await bot.SendMessage(jr.From.Id, first.Prompt, cancellationToken: ct);
                }
                break;
            }

            // Applicant commands — checked ahead of the free-text-as-answer case below so typing
            // "/start"/"/status" mid-survey never gets misread as an answer to the current question.
            case { Message: { Chat.Type: ChatType.Private, From: { } from, Text: { } text } msg } when CommandOf(text) == "/start":
            {
                var lang = Lang(from.LanguageCode);
                var reply = await ResolveApplicantTenantAsync(from.Id, ct) is not null
                    ? tr.Get("bot.start.in_progress", lang)
                    : tr.Get("bot.start.greeting", lang);
                await bot.SendMessage(from.Id, reply, cancellationToken: ct);
                break;
            }

            case { Message: { Chat.Type: ChatType.Private, From: { } from } msg } when CommandOf(msg.Text ?? "") == "/status":
            {
                var status = await api.GetLatestApplicationStatusAsync(from.Id, ct);
                await bot.SendMessage(from.Id, FormatStatus(status, Lang(from.LanguageCode)), cancellationToken: ct);
                break;
            }

            case { Message: { Chat.Type: ChatType.Private, From: { } from } msg }:
            {
                if (await ResolveApplicantTenantAsync(from.Id, ct) is not { } tenantId) break;  // no active survey for this user
                var next = await api.SubmitAnswerAsync(tenantId, new SubmitAnswerRequest(from.Id, msg.Text), ct);
                if (next is null)
                {
                    // Sent free text instead of tapping a language-picker button — the survey hasn't
                    // actually started yet, so this isn't a real answer. Remind them instead of
                    // silently swallowing it (see StartSurveyHandler for why this can no longer
                    // silently start the survey).
                    await bot.SendMessage(from.Id, tr.Get("bot.stray_text_reminder", Lang(from.LanguageCode)), cancellationToken: ct);
                    break;
                }
                await AdvanceAsync(from.Id, next, ct);
                break;
            }

            // Admin-group commands. Only the admin group (Role == "AdminGroup") gets a reply — the
            // same bot can also sit in the main group, which resolves to a tenant too but must not
            // leak queue/stats data if someone types these there. Language resolves per-message from
            // the acting admin's own Telegram client (no persisted preference needed here, unlike
            // applicant DMs — every reply is a direct response to whoever just typed the command).
            case { Message: { Chat.Type: ChatType.Group or ChatType.Supergroup, From: { } from, Text: { } text } msg } when CommandOf(text) == "/stats":
            {
                if (await ResolveAdminGroupAsync(msg.Chat.Id, ct) is { } tenantId)
                {
                    var summary = await api.GetDashboardAsync(tenantId, ct);
                    await bot.SendMessage(msg.Chat.Id, FormatStats(summary, Lang(from.LanguageCode)), cancellationToken: ct);
                }
                break;
            }

            case { Message: { Chat.Type: ChatType.Group or ChatType.Supergroup, From: { } from, Text: { } text } msg } when CommandOf(text) == "/pending":
            {
                if (await ResolveAdminGroupAsync(msg.Chat.Id, ct) is { } tenantId)
                {
                    var lang = Lang(from.LanguageCode);
                    var pending = await api.GetApplicationsByStatusAsync(tenantId, "AwaitingReview", ct);
                    await bot.SendMessage(msg.Chat.Id, FormatApplicationList(pending, tr.Get("bot.admin.pending_empty", lang), lang), cancellationToken: ct);
                }
                break;
            }

            case { Message: { Chat.Type: ChatType.Group or ChatType.Supergroup, From: { } from, Text: { } text } msg } when CommandOf(text) == "/find":
            {
                if (await ResolveAdminGroupAsync(msg.Chat.Id, ct) is { } tenantId)
                {
                    var lang = Lang(from.LanguageCode);
                    var query = ArgumentOf(text);
                    var reply = string.IsNullOrWhiteSpace(query)
                        ? tr.Get("bot.admin.find_usage", lang)
                        : FormatApplicationList(await api.SearchApplicationsAsync(tenantId, query, ct), tr.Get("bot.admin.find_empty", lang), lang);
                    await bot.SendMessage(msg.Chat.Id, reply, cancellationToken: ct);
                }
                break;
            }

            // Declines the join request + notifies the applicant + stops their survey, in one step —
            // for when an admin wants to reject someone before the survey reaches AwaitingReview
            // (there's no admin card with buttons to tap yet at that stage). Also the only reliable
            // way to actually stop a survey after declining via Telegram's own native "manage join
            // requests" UI — Telegram sends the bot no event at all for that action (a Bot API
            // limitation), so the app has no way to react unless the decline goes through the bot.
            // Application ids are visible in /find and /pending's output ("#{id} ...").
            case { Message: { Chat.Type: ChatType.Group or ChatType.Supergroup, From: { } from, Text: { } text } msg } when CommandOf(text) == "/cancel":
            {
                if (await ResolveAdminGroupAsync(msg.Chat.Id, ct) is { } tenantId)
                {
                    var lang = Lang(from.LanguageCode);
                    string reply;
                    if (long.TryParse(ArgumentOf(text), out var applicationId))
                    {
                        var actingName = $"{from.FirstName} {from.LastName}".Trim();
                        await api.CancelApplicationAsync(tenantId, applicationId,
                            new CancelApplicationRequest(from.Id, actingName), ct);
                        reply = tr.Get("bot.admin.cancel_success", lang);
                    }
                    else
                    {
                        reply = tr.Get("bot.admin.cancel_usage", lang);
                    }
                    await bot.SendMessage(msg.Chat.Id, reply, cancellationToken: ct);
                }
                break;
            }

            // The applicant's own language-picker buttons — resolved via the same in-memory
            // TenantRouter used for DM routing (this is a private chat with the bot, not the admin
            // group, so /internal/tenants/resolve by chat id doesn't apply here).
            case { CallbackQuery: { Message: { } m, Data: { } data } cb } when data.StartsWith("lang:"):
            {
                if (await ResolveApplicantTenantAsync(cb.From.Id, ct) is { } tenantId)
                {
                    var action = data["lang:".Length..];
                    switch (action)
                    {
                        case "switch":
                            await bot.EditMessageReplyMarkup(cb.From.Id, m.MessageId,
                                LanguagePickerKeyboard(Lang(cb.From.LanguageCode)), cancellationToken: ct);
                            break;
                        case "go":
                            await SendFirstQuestionAsync(tenantId, cb.From.Id, ct);
                            break;
                        case "ru":
                        case "en":
                            await api.SetUserLanguageAsync(tenantId, cb.From.Id, action, ct);
                            await SendFirstQuestionAsync(tenantId, cb.From.Id, ct);
                            break;
                    }
                }
                await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                break;
            }

            // SingleChoice: one tap submits immediately — "sc:{questionId}:{optionIndex}".
            case { CallbackQuery: { Data: { } data } cb } when data.StartsWith("sc:"):
            {
                if (await ResolveApplicantTenantAsync(cb.From.Id, ct) is { } tenantId)
                {
                    var parts = data.Split(':');
                    var index = int.Parse(parts[2]);
                    var next = await api.SubmitAnswerAsync(tenantId, new SubmitAnswerRequest(cb.From.Id, null, [index]), ct);
                    if (next is not null) await AdvanceAsync(cb.From.Id, next, ct);
                }
                await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                break;
            }

            // MultiChoice toggle — "mc:{questionId}:{mask}:{optionIndex}". The current option labels
            // are read back from the message's own keyboard (stripping the ☐/☑ prefix), so no extra
            // API round-trip or bot-/server-side state is needed to rebuild it with the new mask.
            case { CallbackQuery: { Message: { ReplyMarkup.InlineKeyboard: { } kb } m, Data: { } data } cb } when data.StartsWith("mc:"):
            {
                var parts = data.Split(':');
                var questionId = long.Parse(parts[1]);
                var mask = int.Parse(parts[2]);
                var toggled = int.Parse(parts[3]);
                var newMask = mask ^ (1 << toggled);

                var options = kb.Take(kb.Count() - 1)   // last row is the Done button, not an option
                    .Select(row => StripCheckbox(row.First().Text)).ToList();
                await bot.EditMessageReplyMarkup(cb.From.Id, m.MessageId,
                    MultiChoiceKeyboard(questionId, options, newMask, Lang(cb.From.LanguageCode)), cancellationToken: ct);
                await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                break;
            }

            // MultiChoice submit — "mcdone:{questionId}:{mask}". Only indices travel to the API; the
            // server already has the question loaded and resolves them back to labels itself.
            case { CallbackQuery: { Data: { } data } cb } when data.StartsWith("mcdone:"):
            {
                if (await ResolveApplicantTenantAsync(cb.From.Id, ct) is { } tenantId)
                {
                    var parts = data.Split(':');
                    var mask = int.Parse(parts[2]);
                    var indices = Enumerable.Range(0, 31).Where(i => (mask & (1 << i)) != 0).ToList();
                    var next = await api.SubmitAnswerAsync(tenantId, new SubmitAnswerRequest(cb.From.Id, null, indices), ct);
                    if (next is not null) await AdvanceAsync(cb.From.Id, next, ct);
                }
                await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                break;
            }

            case { CallbackQuery: { Message: { } m } cb }:
                var tenant = await api.ResolveTenantByChatAsync(m.Chat.Id, ct);
                if (tenant is null) break;
                await api.HandleAdminCallbackAsync(tenant.Value.TenantId, new AdminCallbackRequest(
                    m.Chat.Id, m.MessageId, cb.From.Id,
                    $"{cb.From.FirstName} {cb.From.LastName}".Trim(), cb.Data ?? ""), ct);
                await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
                break;
        }
    }

    private async Task SendFirstQuestionAsync(long tenantId, long userId, CancellationToken ct)
    {
        var next = await api.StartSurveyAsync(tenantId, userId, ct);
        await AdvanceAsync(userId, next, ct);
    }

    /// <summary>Every applicant-side handler needs to know which tenant a Telegram user's in-progress
    /// application belongs to. The in-memory TenantRouter answers this instantly in the common case,
    /// but it can lose the mapping (most likely a bot restart between the join request and the
    /// applicant eventually responding, given this server's known periodic reboots) — when that
    /// happens, every handler gated on it would otherwise silently no-op forever, with zero
    /// user-visible feedback, until the next startup's RehydrateRouterAsync happens to run. Self-heal
    /// from durable DB state instead via the same cross-tenant lookup "/status" already uses.</summary>
    private async Task<long?> ResolveApplicantTenantAsync(long telegramUserId, CancellationToken ct)
    {
        if (router.Resolve(telegramUserId) is { } tenantId) return tenantId;

        var status = await api.GetLatestApplicationStatusAsync(telegramUserId, ct);
        if (status is null || status.Status is not ("SurveyOffered" or "InSurvey")) return null;

        router.Remember(telegramUserId, status.TenantId);
        return status.TenantId;
    }

    /// <summary>Completion or next-question dispatch — shared by the free-text and button-tap paths.</summary>
    private async Task AdvanceAsync(long userId, NextQuestionDto next, CancellationToken ct)
    {
        if (next.Completed)
        {
            router.Forget(userId);
            await bot.SendMessage(userId, tr.Get("bot.thankyou", Lang(next.LanguageCode)), cancellationToken: ct);
        }
        else if (next.Prompt is not null)
        {
            await SendQuestionAsync(userId, next.QuestionId!.Value, next.Prompt, next.Type, next.Options, Lang(next.LanguageCode), ct);
        }
    }

    // Prompt text is admin-authored HTML (Telegram-compatible subset, sanitized on save in
    // Gatekeeper.Web) — ParseMode.Html renders the same bold/italic/links seen on the site.
    // SingleChoice/MultiChoice questions get an inline keyboard instead of a "type your answer" prompt.
    private async Task SendQuestionAsync(
        long userId, long questionId, string promptText, string type, IReadOnlyList<string>? options, string lang, CancellationToken ct)
    {
        InlineKeyboardMarkup? keyboard = (type, options) switch
        {
            ("SingleChoice", { Count: > 0 } opts) => SingleChoiceKeyboard(questionId, opts),
            ("MultiChoice", { Count: > 0 } opts) => MultiChoiceKeyboard(questionId, opts, mask: 0, lang),
            _ => null,
        };
        await bot.SendMessage(userId, promptText, parseMode: ParseMode.Html, replyMarkup: keyboard, cancellationToken: ct);
    }

    // "/stats", "/find foo" — strips the leading slash-token and an optional "@BotUsername" suffix
    // (group chats with multiple bots disambiguate commands that way) from the first whitespace-
    // separated token; ignores anything else on the line.
    private static string CommandOf(string text) => text.Split([' ', '\n'], 2)[0].Split('@')[0];

    private static string ArgumentOf(string text)
    {
        var spaceIndex = text.IndexOf(' ');
        return spaceIndex < 0 ? "" : text[(spaceIndex + 1)..].Trim();
    }

    /// <summary>Resolves a chat to a tenant only if it's that tenant's admin group — commands like
    /// "/stats"/"/pending"/"/find" must never answer from the main (applicant-facing) group.</summary>
    private async Task<long?> ResolveAdminGroupAsync(long chatId, CancellationToken ct)
    {
        var tenant = await api.ResolveTenantByChatAsync(chatId, ct);
        return tenant is { Role: "AdminGroup" } ? tenant.Value.TenantId : null;
    }

    private string FormatStatus(LatestApplicationStatusDto? status, string lang)
    {
        if (status is null) return tr.Get("bot.status.none", lang);
        return status.Status switch
        {
            "Approved" => tr.Get("bot.status.approved", lang),
            "Rejected" => tr.Get("bot.status.rejected", lang),
            "AwaitingReview" => tr.Get("bot.status.awaiting_review", lang),
            _ => tr.Get("bot.status.in_survey", lang),
        };
    }

    private string FormatStats(DashboardSummary s, string lang) =>
        $"{tr.Get("bot.admin.stats_title", lang)}\n" +
        $"{tr.Get("bot.admin.stats_pending", lang)}: {s.PendingCount}\n" +
        $"{tr.Get("bot.admin.stats_today", lang)}: ✅ {s.ApprovedToday} / ❌ {s.RejectedToday}\n" +
        $"{tr.Get("bot.admin.stats_week", lang)}: ✅ {s.ApprovedWeek} / ❌ {s.RejectedWeek}\n" +
        $"Outbox: {tr.Get("bot.admin.stats_outbox_pending", lang)} {s.OutboxPending}, " +
        $"{tr.Get("bot.admin.stats_outbox_inflight", lang)} {s.OutboxInFlight}, " +
        $"{tr.Get("bot.admin.stats_outbox_failed", lang)} {s.OutboxFailed}";

    private string FormatApplicationList(IReadOnlyList<ApplicationSummary> apps, string emptyText, string lang)
    {
        if (apps.Count == 0) return emptyText;
        var dash = tr.Get("common.empty_dash", lang);
        var lines = apps.Select(a =>
        {
            var name = a.DisplayName ?? dash;
            var username = a.Username is null ? "" : $" (@{a.Username})";
            return $"#{a.Id} {name}{username} — {a.Status}";
        });
        return string.Join("\n", lines);
    }

    private Task HandleErrorAsync(ITelegramBotClient _, Exception ex, CancellationToken ct)
    {
        log.LogError(ex, "Telegram polling error");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Outbound side. Drains the transactional outbox via the API (the only DB owner) and executes each
/// command on Telegram. Single executor → idempotency and the "Telegram wins" guarantee.
/// </summary>
public sealed class OutboxDrainWorker(
    ITelegramBotClient bot,
    IGatekeeperApiClient api,
    ILogger<OutboxDrainWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var cmd in await api.GetPendingTelegramCommandsAsync(batch: 20, stoppingToken))
                {
                    var result = await ExecuteAsync(cmd, stoppingToken);
                    await api.AckTelegramCommandAsync(cmd.TenantId, cmd.CommandId, result, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Outbox drain cycle failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    private async Task<CommandResult> ExecuteAsync(PendingCommand cmd, CancellationToken ct)
    {
        try
        {
            long? sentMessageId = null;
            switch (cmd.Type)
            {
                case "ApproveJoinRequest": await bot.ApproveChatJoinRequest(cmd.ChatId, cmd.UserId, ct); break;
                case "DeclineJoinRequest": await bot.DeclineChatJoinRequest(cmd.ChatId, cmd.UserId, ct); break;
                case "SendMessage":
                    await bot.SendMessage(cmd.ChatId, cmd.Text ?? "", replyMarkup: Keyboard(cmd.Buttons), cancellationToken: ct);
                    break;
                case "SendAdminCard":
                    // Same idea as SendMessage, but the caller needs the message_id back to edit this
                    // card later — and a photo (the applicant's profile pic) needs SendPhoto instead.
                    var sent = cmd.PhotoFileId is { } photoId
                        ? await bot.SendPhoto(cmd.ChatId, InputFile.FromFileId(photoId), caption: cmd.Text,
                            replyMarkup: Keyboard(cmd.Buttons), cancellationToken: ct)
                        : await bot.SendMessage(cmd.ChatId, cmd.Text ?? "", replyMarkup: Keyboard(cmd.Buttons), cancellationToken: ct);
                    sentMessageId = sent.MessageId;
                    break;
                case "EditMessage":
                    if (cmd.MessageId is { } messageId)
                    {
                        if (cmd.IsPhotoCaption)
                            await bot.EditMessageCaption(cmd.ChatId, (int)messageId, cmd.Text ?? "",
                                replyMarkup: Keyboard(cmd.Buttons), cancellationToken: ct);
                        else
                            await bot.EditMessageText(cmd.ChatId, (int)messageId, cmd.Text ?? "",
                                replyMarkup: Keyboard(cmd.Buttons), cancellationToken: ct);
                    }
                    break;
                case "BanUser":            await bot.BanChatMember(cmd.ChatId, cmd.UserId, cancellationToken: ct); break;
                case "UnbanUser":          await bot.UnbanChatMember(cmd.ChatId, cmd.UserId, cancellationToken: ct); break;
                case "RestrictUser":       await bot.RestrictChatMember(cmd.ChatId, cmd.UserId, new ChatPermissions(), cancellationToken: ct); break;
                default:                   return new CommandResult(false, $"Unknown command type {cmd.Type}");
            }
            return new CommandResult(true, null, sentMessageId);
        }
        catch (Exception ex)
        {
            // e.g. "HIDE_REQUESTER_MISSING"/"USER_ALREADY_PARTICIPANT" — treated as already-resolved upstream.
            return new CommandResult(false, ex.Message);
        }
    }

    // The show/hide-answers toggle gets its own row above approve/reject, instead of all three
    // buttons crammed into a single row.
    private static InlineKeyboardMarkup? Keyboard(IReadOnlyList<CommandButton>? buttons)
    {
        if (buttons is not { Count: > 0 }) return null;

        var toggleRow = buttons.Where(IsAnswersToggle).Select(ToButton).ToList();
        var decisionRow = buttons.Where(b => !IsAnswersToggle(b)).Select(ToButton).ToList();

        var rows = new List<IEnumerable<InlineKeyboardButton>>();
        if (toggleRow.Count > 0) rows.Add(toggleRow);
        if (decisionRow.Count > 0) rows.Add(decisionRow);

        return new InlineKeyboardMarkup(rows);
    }

    private static bool IsAnswersToggle(CommandButton b) =>
        b.CallbackData.StartsWith("ans:") || b.CallbackData.StartsWith("hideans:");

    private static InlineKeyboardButton ToButton(CommandButton b) =>
        InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackData);
}
