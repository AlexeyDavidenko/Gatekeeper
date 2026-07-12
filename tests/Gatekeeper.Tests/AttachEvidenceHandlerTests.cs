using Gatekeeper.Application;
using Gatekeeper.Domain;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for AttachEvidenceHandler — attaching a screenshot to an existing ModerationAction.
/// Deliberately independent of ModerateUserHandler (see its own doc comment): these tests attach
/// to an action seeded directly, not one created by ModerateUserHandler in the same test.
/// </summary>
public class AttachEvidenceHandlerTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 10, 12, 0, 0, TimeSpan.Zero);
    private const long AdminUserId = 999L;

    private static ModerationAction CreateAction() => ModerationAction.ForMember(
        telegramUserId: 1001, chatId: -999, action: ModerationActionType.Ban, reason: "Spam",
        notes: null, performedByUserId: AdminUserId, performedByName: "Admin", source: ActionSource.Web,
        expiresAt: null, now: TestNow);

    [Fact]
    public async Task Attaches_Evidence_To_Existing_Action()
    {
        var moderation = new FakeModerationRepository();
        var storage = new FakeEvidenceStorage();
        var unitOfWork = new FakeUnitOfWork();
        var clock = new FakeClock(TestNow.AddMinutes(5));

        var action = CreateAction();
        moderation.Seed(action, id: 1);

        var bytes = "screenshot bytes"u8.ToArray();
        using var stream = new MemoryStream(bytes);

        var handler = new AttachEvidenceHandler(moderation, storage, unitOfWork, clock);
        await handler.HandleAsync(new AttachEvidenceCommand(1, stream, "image/png", AdminUserId));

        var attachment = Assert.Single(action.Attachments);
        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(bytes.LongLength, attachment.SizeBytes);
        Assert.Equal(AdminUserId, attachment.UploadedBy);
        Assert.StartsWith("evidence/", attachment.StoragePath);
        Assert.NotEmpty(attachment.ContentHash);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task Throws_When_Action_Not_Found()
    {
        var moderation = new FakeModerationRepository();
        var storage = new FakeEvidenceStorage();
        var unitOfWork = new FakeUnitOfWork();
        var clock = new FakeClock(TestNow);

        using var stream = new MemoryStream("x"u8.ToArray());
        var handler = new AttachEvidenceHandler(moderation, storage, unitOfWork, clock);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new AttachEvidenceCommand(999, stream, "image/png", AdminUserId)));
        Assert.Contains("not found", ex.Message);
        Assert.Equal(0, unitOfWork.SaveCount);
    }
}
