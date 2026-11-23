using Xunit;

namespace DurableOutbox.Tests;

public sealed class OutcomeTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    // Guarantee 3: an outcome from a worker whose claim was taken over is refused and changes nothing.
    [Fact]
    public async Task An_outcome_from_a_claim_that_was_taken_over_is_refused()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        var (message, staleToken) = await ClaimOneAsync();

        Time.Advance(Lease);
        var (_, currentToken) = await ClaimOneAsync();

        Assert.False(await CompleteAsync(message.Id, staleToken));
        Assert.Equal(FailOutcome.Refused, await FailAsync(message.Id, staleToken, "late failure"));
        Assert.False(await RejectAsync(message.Id, staleToken, "late refusal"));

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(currentToken, row.LeaseToken);
        Assert.Equal(2, row.Attempts);
        Assert.Equal(OutboxStore<TestDbContext>.AbandonedError, row.LastError);

        // The current owner still can, once.
        Assert.True(await CompleteAsync(message.Id, currentToken));
        Assert.False(await CompleteAsync(message.Id, currentToken));
        Assert.Equal(OutboxStatus.Completed, (await RowAsync()).Status);
    }

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

    // Guarantee 5: a message that cannot be delivered is parked with the error from each attempt.
    [Fact]
    public async Task A_message_that_fails_every_attempt_is_parked_with_each_error()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (message, token) = await ClaimOneAsync(maxAttempts: 3);
            var outcome = await FailAsync(message.Id, token, $"failure {attempt}", maxAttempts: 3);
            Assert.Equal(attempt < 3 ? FailOutcome.Retried : FailOutcome.Parked, outcome);
        }

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Parked, row.Status);
        Assert.Equal(Time.GetUtcNow(), row.FinishedAt);
        Assert.Null(row.LeaseToken);
        Assert.Equal(
            [(1, "failure 1"), (2, "failure 2"), (3, "failure 3")],
            Errors(row));

        Time.Advance(TimeSpan.FromDays(1));
        Assert.Empty((await ClaimAsync(maxAttempts: 3)).Messages);
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
