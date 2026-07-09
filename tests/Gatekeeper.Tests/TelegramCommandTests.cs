using Gatekeeper.Domain;
using Gatekeeper.Domain.Outbox;
using Xunit;

namespace Gatekeeper.Tests;

/// <summary>
/// Tests for TelegramCommand's stale-InFlight reclaim — recovers a command left stranded when the
/// bot that claimed it crashed/restarted before acking (nothing else ever re-queries a non-Pending row).
/// </summary>
public class TelegramCommandTests
{
    private static readonly DateTimeOffset TestNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static TelegramCommand NewCommand() =>
        TelegramCommand.Enqueue(TelegramCommandType.SendMessage, "{}", applicationId: 1, telegramUserId: 100, TestNow);

    [Fact]
    public void MarkInFlight_Sets_Status_And_InFlightAt_And_Increments_Attempts()
    {
        var cmd = NewCommand();

        cmd.MarkInFlight(TestNow);

        Assert.Equal(TelegramCommandStatus.InFlight, cmd.Status);
        Assert.Equal(TestNow, cmd.InFlightAt);
        Assert.Equal(1, cmd.Attempts);
    }

    [Fact]
    public void IsStale_False_While_Still_Pending()
    {
        var cmd = NewCommand();

        Assert.False(cmd.IsStale(TestNow.AddHours(1), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void IsStale_False_When_InFlight_But_Within_Threshold()
    {
        var cmd = NewCommand();
        cmd.MarkInFlight(TestNow);

        Assert.False(cmd.IsStale(TestNow.AddSeconds(30), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void IsStale_True_When_InFlight_Past_Threshold()
    {
        var cmd = NewCommand();
        cmd.MarkInFlight(TestNow);

        Assert.True(cmd.IsStale(TestNow.AddMinutes(5), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void IsStale_False_Once_Already_Succeeded()
    {
        var cmd = NewCommand();
        cmd.MarkInFlight(TestNow);
        cmd.MarkSucceeded(TestNow.AddSeconds(1));

        Assert.False(cmd.IsStale(TestNow.AddMinutes(5), TimeSpan.FromMinutes(2)));
    }

    [Fact]
    public void ReclaimStale_Resets_To_Pending_And_Clears_InFlightAt_While_Attempts_Remain()
    {
        var cmd = NewCommand();
        cmd.MarkInFlight(TestNow);   // Attempts = 1

        var reclaimTime = TestNow.AddMinutes(5);
        cmd.ReclaimStale(reclaimTime, maxAttempts: 5);

        Assert.Equal(TelegramCommandStatus.Pending, cmd.Status);
        Assert.Null(cmd.InFlightAt);
        Assert.Equal(1, cmd.Attempts);   // not incremented by reclaim itself — MarkInFlight does that on the next claim
    }

    [Fact]
    public void ReclaimStale_Gives_Up_As_Failed_Once_MaxAttempts_Reached()
    {
        var cmd = NewCommand();
        for (var i = 0; i < 5; i++)
        {
            cmd.MarkInFlight(TestNow);
            cmd.ReclaimStale(TestNow.AddMinutes(5), maxAttempts: 5);
        }
        // After 5 claim+reclaim cycles, Attempts == 5 and the command is back to Pending — one more
        // claim pushes Attempts to 6, at which point reclaiming should give up instead of retrying again.
        cmd.MarkInFlight(TestNow);   // Attempts = 6

        var reclaimTime = TestNow.AddMinutes(10);
        cmd.ReclaimStale(reclaimTime, maxAttempts: 5);

        Assert.Equal(TelegramCommandStatus.Failed, cmd.Status);
        Assert.Equal(reclaimTime, cmd.CompletedAt);
        Assert.Contains("max retry attempts", cmd.LastError);
    }

    [Fact]
    public void Reclaimed_Command_Can_Be_Claimed_Again_And_Eventually_Succeed()
    {
        var cmd = NewCommand();
        cmd.MarkInFlight(TestNow);
        cmd.ReclaimStale(TestNow.AddMinutes(5), maxAttempts: 5);

        // A later drain pass claims it again, same as any other Pending command.
        var secondClaim = TestNow.AddMinutes(6);
        cmd.MarkInFlight(secondClaim);
        cmd.MarkSucceeded(secondClaim.AddSeconds(1));

        Assert.Equal(TelegramCommandStatus.Succeeded, cmd.Status);
        Assert.Equal(2, cmd.Attempts);
    }
}
