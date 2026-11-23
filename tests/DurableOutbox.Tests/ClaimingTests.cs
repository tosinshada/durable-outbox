using System.Collections.Concurrent;
using System.Text.Json;
using Xunit;

namespace DurableOutbox.Tests;

public sealed class ClaimingTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    [Fact]
    public async Task A_due_message_is_claimed_under_a_lease_and_stays_pending()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));

        var (message, token) = await ClaimOneAsync();

        Assert.Equal("order-1", message.RecordRef);
        Assert.Equal(OutboxStatus.Pending, message.Status);
        Assert.Equal(1, message.Attempt);

        var row = await RowAsync();
        Assert.Equal(message.Id, row.Id);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(token, row.LeaseToken);
        Assert.Equal(Time.GetUtcNow() + Lease, row.NextAttemptAt);

        // A live claim is not given out again.
        Time.Advance(Lease - TimeSpan.FromSeconds(1));
        Assert.Empty((await ClaimAsync()).Messages);
    }

    // Guarantee 3: one owner at a time, for any number of claimers.
    [Fact]
    public async Task Four_concurrent_claimers_take_1000_messages_exactly_once()
    {
        const int total = 1000;
        await AddAsync([.. Enumerable.Range(0, total).Select(i => new OutboxMessage(Kind, $"order-{i}"))]);

        var claimed = new ConcurrentBag<Guid>();

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (true)
            {
                var batch = await ClaimAsync(limit: 25);
                if (batch.Messages.Count == 0)
                {
                    return;
                }

                foreach (var message in batch.Messages)
                {
                    claimed.Add(message.Id);
                }
            }
        }, Ct)));

        Assert.Equal(total, claimed.Count);
        Assert.Equal(total, claimed.Distinct().Count());
    }

    [Fact]
    public async Task Claims_take_higher_priority_first_then_earliest_due_up_to_the_limit()
    {
        await AddAsync(new OutboxMessage(Kind, "low"));
        await AddAsync(new OutboxMessage(Kind, "high-first") { Priority = 5 });
        await AddAsync(new OutboxMessage("other.kind", "other") { Priority = 9 });
        Time.Advance(TimeSpan.FromSeconds(1));
        await AddAsync(new OutboxMessage(Kind, "high-second") { Priority = 5 });

        // Not yet due: a failed attempt waiting for its retry time.
        await AddAsync(new OutboxMessage(Kind, "waiting") { Priority = 7 });
        var waiting = await ClaimAsync(limit: 1);
        Assert.Equal("waiting", Assert.Single(waiting.Messages).RecordRef);
        await FailAsync(waiting.Messages[0].Id, waiting.Token, "not yet", delay: TimeSpan.FromHours(1));

        var first = await ClaimAsync(limit: 2);
        Assert.Equal(["high-first", "high-second"], first.Messages.Select(message => message.RecordRef));

        var rest = await ClaimAsync(limit: 10);
        Assert.Equal("low", Assert.Single(rest.Messages).RecordRef);
    }

    // Guarantee 4: a crash is an attempt, so a message that kills its worker ends up parked.
    [Fact]
    public async Task An_attempt_that_never_reports_is_claimed_again_and_parked_at_the_attempt_limit()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        var tokens = new List<Guid>();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var (message, token) = await ClaimOneAsync(maxAttempts: 3);
            Assert.Equal(OutboxStatus.Pending, message.Status);
            Assert.Equal(attempt, message.Attempt);
            tokens.Add(token);

            Time.Advance(Lease);
        }

        Assert.Equal(3, tokens.Distinct().Count());

        var (parked, _) = await ClaimOneAsync(maxAttempts: 3);
        Assert.Equal(OutboxStatus.Parked, parked.Status);

        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Parked, row.Status);
        Assert.Equal(3, row.Attempts);
        Assert.Null(row.LeaseToken);
        Assert.Equal(Time.GetUtcNow(), row.FinishedAt);
        Assert.Equal(OutboxStore<TestDbContext>.AbandonedError, row.LastError);
        Assert.Equal([1, 2, 3], Attempts(row));

        Time.Advance(TimeSpan.FromDays(1));
        Assert.Empty((await ClaimAsync(maxAttempts: 3)).Messages);
    }

    [Fact]
    public async Task A_message_due_after_its_retry_window_is_parked_by_the_claim()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        var (message, token) = await ClaimOneAsync();
        await FailAsync(message.Id, token, "down", delay: TimeSpan.FromMinutes(30), retryWindow: TimeSpan.FromHours(1));

        Time.Advance(TimeSpan.FromHours(1));
        var batch = await ClaimAsync(retryWindow: TimeSpan.FromHours(1));

        Assert.Equal(OutboxStatus.Parked, Assert.Single(batch.Messages).Status);
        Assert.Equal(OutboxStatus.Parked, (await RowAsync()).Status);
    }

    [Fact]
    public async Task A_message_past_its_deadline_expires_when_claimed()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1") { Deadline = Time.GetUtcNow() + TimeSpan.FromMinutes(5) });
        Time.Advance(TimeSpan.FromMinutes(5));

        var batch = await ClaimAsync();

        Assert.Equal(OutboxStatus.Expired, Assert.Single(batch.Messages).Status);
        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Expired, row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Equal(Time.GetUtcNow(), row.FinishedAt);
        Assert.Empty((await ClaimAsync()).Messages);
    }

    [Fact]
    public async Task A_message_claimed_before_its_deadline_can_finish_after_it()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1") { Deadline = Time.GetUtcNow() + TimeSpan.FromSeconds(10) });
        var (message, token) = await ClaimOneAsync();

        Time.Advance(TimeSpan.FromSeconds(30));

        Assert.True(await CompleteAsync(message.Id, token));
        Assert.Equal(OutboxStatus.Completed, (await RowAsync()).Status);
    }

    private static int[] Attempts(OutboxMessageRow row) =>
        [.. JsonDocument.Parse(row.Errors).RootElement.EnumerateArray().Select(entry => entry.GetProperty("attempt").GetInt32())];
}
