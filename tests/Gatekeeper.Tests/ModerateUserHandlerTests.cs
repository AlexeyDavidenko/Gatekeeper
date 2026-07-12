using System.Text.Json;
using Gatekeeper.Application;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for ModerateUserHandler — the Ban/Mute/Kick/Warn/Unban/Unmute path, now wired up to
/// Detail.razor's moderation UI. Covers the ModerationAction it records, the outbox command it maps
/// each action type to (or doesn't, for actions with no Telegram side effect), and the returned id
/// (needed by the evidence-upload follow-up call).
/// </summary>
public class ModerateUserHandlerTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const long TelegramUserId = 1001L;
    private const long ChatId = -999L;
    private const long AdminUserId = 999L;
    private const string AdminName = "Admin";

    private static ModerateUserHandler CreateHandler(
        FakeModerationRepository moderation, FakeTelegramCommandQueue queue, FakeUnitOfWork unitOfWork) =>
        new(moderation, queue, unitOfWork, new FakeClock(TestNow));

    private static ModerateUserCommand Command(ModerationActionType action) => new(
        TelegramUserId, ChatId, action, Reason: "Test reason", Notes: null,
        AdminUserId, AdminName, ActionSource.Web, ExpiresAt: null);

    [Theory]
    [InlineData(ModerationActionType.Ban, TelegramCommandType.BanUser)]
    [InlineData(ModerationActionType.Kick, TelegramCommandType.BanUser)]
    [InlineData(ModerationActionType.Unban, TelegramCommandType.UnbanUser)]
    [InlineData(ModerationActionType.Mute, TelegramCommandType.RestrictUser)]
    [InlineData(ModerationActionType.Unmute, TelegramCommandType.RestrictUser)]
    public async Task Enqueues_The_Right_Telegram_Command(ModerationActionType action, TelegramCommandType expectedCommand)
    {
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var unitOfWork = new FakeUnitOfWork();
        var handler = CreateHandler(moderation, queue, unitOfWork);

        await handler.HandleAsync(Command(action));

        var cmd = Assert.Single(queue.Enqueued);
        Assert.Equal(expectedCommand, cmd.Type);
        var payload = JsonSerializer.Deserialize<TelegramCommandPayload>(cmd.PayloadJson)!;
        Assert.Equal(ChatId, payload.ChatId);
        Assert.Equal(TelegramUserId, payload.UserId);
    }

    [Fact]
    public async Task Warn_Records_Action_But_Enqueues_No_Telegram_Command()
    {
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var unitOfWork = new FakeUnitOfWork();
        var handler = CreateHandler(moderation, queue, unitOfWork);

        await handler.HandleAsync(Command(ModerationActionType.Warn));

        Assert.Empty(queue.Enqueued);
        var recorded = Assert.Single(moderation.Store);
        Assert.Equal(ModerationActionType.Warn, recorded.Action);
    }

    [Fact]
    public async Task Returns_The_New_Actions_Id()
    {
        var moderation = new FakeModerationRepository();
        var queue = new FakeTelegramCommandQueue();
        var unitOfWork = new FakeUnitOfWork();
        var handler = CreateHandler(moderation, queue, unitOfWork);

        var id = await handler.HandleAsync(Command(ModerationActionType.Ban));

        Assert.Equal(moderation.Store[0].Id, id);
        Assert.Equal(1, unitOfWork.SaveCount);
    }
}
