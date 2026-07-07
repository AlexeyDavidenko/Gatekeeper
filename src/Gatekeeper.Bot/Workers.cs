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
    ILogger<TelegramUpdateWorker> log) : BackgroundService
{
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

                if (first.Prompt is not null)
                    await bot.SendMessage(jr.From.Id, first.Prompt, cancellationToken: ct);
                break;
            }

            case { Message: { Chat.Type: ChatType.Private, From: { } from } msg }:
            {
                if (router.Resolve(from.Id) is not { } tenantId) break;  // no active survey for this user
                var next = await api.SubmitAnswerAsync(tenantId, new SubmitAnswerRequest(from.Id, msg.Text), ct);
                if (next.Completed) router.Forget(from.Id);
                else if (next.Prompt is not null) await bot.SendMessage(from.Id, next.Prompt, cancellationToken: ct);
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
                            await bot.EditMessageCaption(cmd.ChatId, (int)messageId, cmd.Text ?? "", cancellationToken: ct);
                        else
                            await bot.EditMessageText(cmd.ChatId, (int)messageId, cmd.Text ?? "", cancellationToken: ct);
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

    private static InlineKeyboardMarkup? Keyboard(IReadOnlyList<CommandButton>? buttons) =>
        buttons is { Count: > 0 }
            ? new InlineKeyboardMarkup(buttons.Select(b => InlineKeyboardButton.WithCallbackData(b.Text, b.CallbackData)))
            : null;
}
