using Gatekeeper.Application;
using Gatekeeper.Domain;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for soft-archiving of ModerationAction rows — an Owner-triggered, reversible way to hide
/// old log entries (History.razor) without deleting them. No automatic/scheduled purge exists.
/// </summary>
public class ModerationArchivingTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const long AdminUserId = 999L;
    private const string AdminName = "admin";

    private static ModerationAction CreateDecision(DateTimeOffset createdAt, long telegramUserId = 1001) =>
        ModerationAction.ForDecision(
            telegramUserId, applicationId: telegramUserId, chatId: -999,
            action: ModerationActionType.Approve, reason: "Good application",
            performedByUserId: AdminUserId, performedByName: AdminName, source: ActionSource.Web, now: createdAt);

    [Fact]
    public void Archive_Then_Unarchive_Is_Fully_Reversible()
    {
        var action = CreateDecision(TestNow);
        Assert.False(action.IsArchived);
        Assert.Null(action.ArchivedAt);

        var archivedAt = TestNow.AddDays(30);
        action.Archive(archivedAt);
        Assert.True(action.IsArchived);
        Assert.Equal(archivedAt, action.ArchivedAt);

        action.Unarchive();
        Assert.False(action.IsArchived);
        Assert.Null(action.ArchivedAt);
    }

    [Fact]
    public async Task ArchiveModerationActionsHandler_Archives_Only_Active_Rows_Older_Than_Cutoff()
    {
        var moderation = new FakeModerationRepository();
        var unitOfWork = new FakeUnitOfWork();
        var clock = new FakeClock(TestNow);

        var old = CreateDecision(TestNow.AddDays(-30), telegramUserId: 1);
        moderation.Seed(old, id: 1);

        var alreadyArchived = CreateDecision(TestNow.AddDays(-40), telegramUserId: 2);
        alreadyArchived.Archive(TestNow.AddDays(-10));
        moderation.Seed(alreadyArchived, id: 2);

        var recent = CreateDecision(TestNow.AddDays(-1), telegramUserId: 3);
        moderation.Seed(recent, id: 3);

        var handler = new ArchiveModerationActionsHandler(moderation, unitOfWork, clock);
        var count = await handler.HandleAsync(new ArchiveModerationActionsCommand(TestNow.AddDays(-7)));

        Assert.Equal(1, count);
        Assert.True(old.IsArchived);
        Assert.Equal(TestNow, old.ArchivedAt);
        Assert.False(recent.IsArchived);
        Assert.Equal(TestNow.AddDays(-10), alreadyArchived.ArchivedAt);   // untouched, not re-stamped
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task UnarchiveModerationActionHandler_Reverses_Archive()
    {
        var moderation = new FakeModerationRepository();
        var unitOfWork = new FakeUnitOfWork();

        var action = CreateDecision(TestNow.AddDays(-30));
        action.Archive(TestNow);
        moderation.Seed(action, id: 1);

        var handler = new UnarchiveModerationActionHandler(moderation, unitOfWork);
        await handler.HandleAsync(new UnarchiveModerationActionCommand(1));

        Assert.False(action.IsArchived);
        Assert.Null(action.ArchivedAt);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task UnarchiveModerationActionHandler_Throws_When_Not_Found()
    {
        var moderation = new FakeModerationRepository();
        var unitOfWork = new FakeUnitOfWork();
        var handler = new UnarchiveModerationActionHandler(moderation, unitOfWork);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new UnarchiveModerationActionCommand(999)));
        Assert.Contains("not found", ex.Message);
    }

    [Fact]
    public async Task UnarchiveModerationActionsHandler_Restores_All_Given_Ids_With_One_SaveChanges()
    {
        var moderation = new FakeModerationRepository();
        var unitOfWork = new FakeUnitOfWork();

        var first = CreateDecision(TestNow.AddDays(-30), telegramUserId: 1);
        first.Archive(TestNow);
        moderation.Seed(first, id: 1);

        var second = CreateDecision(TestNow.AddDays(-20), telegramUserId: 2);
        second.Archive(TestNow);
        moderation.Seed(second, id: 2);

        // A third, untouched row makes sure the batch only affects the ids it was given.
        var untouched = CreateDecision(TestNow.AddDays(-10), telegramUserId: 3);
        untouched.Archive(TestNow);
        moderation.Seed(untouched, id: 3);

        var handler = new UnarchiveModerationActionsHandler(moderation, unitOfWork);
        await handler.HandleAsync(new UnarchiveModerationActionsCommand([1, 2]));

        Assert.False(first.IsArchived);
        Assert.False(second.IsArchived);
        Assert.True(untouched.IsArchived);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task UnarchiveModerationActionsHandler_Throws_When_Any_Id_Not_Found()
    {
        var moderation = new FakeModerationRepository();
        var unitOfWork = new FakeUnitOfWork();

        var first = CreateDecision(TestNow.AddDays(-30));
        first.Archive(TestNow);
        moderation.Seed(first, id: 1);

        var handler = new UnarchiveModerationActionsHandler(moderation, unitOfWork);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new UnarchiveModerationActionsCommand([1, 999])));
        Assert.Contains("not found", ex.Message);
    }
}
