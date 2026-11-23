using Xunit;

namespace DurableOutbox.Tests;

public sealed class OutcomeTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    [Fact]
    public async Task A_failed_attempt_is_retried_at_its_next_due_time_and_not_before()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        var (message, token) = await ClaimOneAsync();

        Assert.Equal(FailOutcome.Retried, await FailAsync(message.Id, token, "receiver down", delay: TimeSpan.FromSeconds(30)));

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Null(row.LeaseToken);
        Assert.Equal("receiver down", row.LastError);

        Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Empty((await ClaimAsync()).Messages);

        Time.Advance(TimeSpan.FromSeconds(1));
        var (retried, _) = await ClaimOneAsync();
        Assert.Equal(message.Id, retried.Id);
        Assert.Equal(2, retried.Attempt);
    }

    [Fact]
    public async Task A_rejected_message_is_finished_with_its_reason_and_never_claimed_again()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        var (message, token) = await ClaimOneAsync();

        Assert.True(await RejectAsync(message.Id, token, "no such order"));

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Rejected, row.Status);
        Assert.Equal("no such order", row.LastError);
        Assert.Equal(Time.GetUtcNow(), row.FinishedAt);
        Assert.Equal([(1, "no such order")], Errors(row));

        Time.Advance(TimeSpan.FromDays(1));
        Assert.Empty((await ClaimAsync()).Messages);
    }

    [Fact]
    public async Task The_error_history_keeps_the_most_recent_20()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));

        for (var attempt = 1; attempt <= 25; attempt++)
        {
            var (message, token) = await ClaimOneAsync(maxAttempts: 25);
            await FailAsync(message.Id, token, $"failure {attempt}", maxAttempts: 25);
        }

        var row = await RowAsync();
        Assert.Equal("failure 25", row.LastError);
        Assert.Equal(Enumerable.Range(6, 20).Select(attempt => (attempt, $"failure {attempt}")), Errors(row));
    }
}
