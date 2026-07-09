using System.Text.Json;
using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Integration test for the stale-InFlight reclaim query added to Endpoints.cs's
/// "/internal/telegram-commands/pending" (InFlightAt is a timestamptz column — this proves the
/// real comparison against a real Postgres clock, not just that the domain method's own logic is
/// right in isolation, which TelegramCommandTests.cs already covers). Query is intentionally
/// duplicated from Endpoints.cs rather than extracted into a shared method, per project convention
/// for read queries (see docs/conventions.md "Как добавить чтение (query)").
/// </summary>
[Collection("Postgres")]
public sealed class OutboxReclaimIntegrationTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan StaleThreshold = TimeSpan.FromMinutes(2);
    private const int MaxAttempts = 5;

    [Fact]
    public async Task Reclaim_Resets_Only_Commands_Stuck_Past_The_Threshold()
    {
        await using var db = await fixture.CreateTenantDbAsync();
        var payload = JsonSerializer.Serialize(new TelegramCommandPayload(ChatId: -1, Text: "hi"));

        // Claimed 5 minutes ago — past the 2-minute threshold, should be reclaimed.
        var stuck = TelegramCommand.Enqueue(TelegramCommandType.SendMessage, payload, null, 1001, TestNow);
        stuck.MarkInFlight(TestNow.AddMinutes(-5));

        // Claimed 30 seconds ago — still within the threshold, must be left alone (a real in-flight
        // Telegram call, not a stranded one).
        var recentlyClaimed = TelegramCommand.Enqueue(TelegramCommandType.SendMessage, payload, null, 1002, TestNow);
        recentlyClaimed.MarkInFlight(TestNow.AddSeconds(-30));

        db.TelegramCommands.AddRange(stuck, recentlyClaimed);
        await db.SaveChangesAsync();

        // Replicate the exact reclaim query from Endpoints.cs "/internal/telegram-commands/pending".
        var staleCutoff = TestNow - StaleThreshold;
        var stale = await db.TelegramCommands
            .Where(c => c.Status == TelegramCommandStatus.InFlight && c.InFlightAt < staleCutoff)
            .ToListAsync();
        foreach (var cmd in stale) cmd.ReclaimStale(TestNow, MaxAttempts);
        await db.SaveChangesAsync();

        var reloadedStuck = await db.TelegramCommands.AsNoTracking().FirstAsync(c => c.Id == stuck.Id);
        var reloadedRecent = await db.TelegramCommands.AsNoTracking().FirstAsync(c => c.Id == recentlyClaimed.Id);

        Assert.Equal(TelegramCommandStatus.Pending, reloadedStuck.Status);
        Assert.Null(reloadedStuck.InFlightAt);
        Assert.Equal(TelegramCommandStatus.InFlight, reloadedRecent.Status);
    }

    [Fact]
    public async Task Reclaimed_Command_Is_Picked_Up_By_The_Next_Pending_Claim_Pass()
    {
        await using var db = await fixture.CreateTenantDbAsync();
        var payload = JsonSerializer.Serialize(new TelegramCommandPayload(ChatId: -1, Text: "hi"));

        var stuck = TelegramCommand.Enqueue(TelegramCommandType.SendMessage, payload, null, 1001, TestNow);
        stuck.MarkInFlight(TestNow.AddMinutes(-5));
        db.TelegramCommands.Add(stuck);
        await db.SaveChangesAsync();

        var staleCutoff = TestNow - StaleThreshold;
        var stale = await db.TelegramCommands
            .Where(c => c.Status == TelegramCommandStatus.InFlight && c.InFlightAt < staleCutoff)
            .ToListAsync();
        foreach (var cmd in stale) cmd.ReclaimStale(TestNow, MaxAttempts);
        await db.SaveChangesAsync();

        // Same claim query the endpoint runs right after reclaiming.
        var pending = await db.TelegramCommands
            .Where(c => c.Status == TelegramCommandStatus.Pending)
            .OrderBy(c => c.Id)
            .Take(32)
            .ToListAsync();

        Assert.Single(pending);
        Assert.Equal(stuck.Id, pending[0].Id);
    }
}
