using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

public sealed class OperatorTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    [Fact]
    public async Task Parked_and_rejected_messages_are_listed_by_kind_newest_first()
    {
        await AddAsync(new OutboxMessage(Kind, "parked-first"), new OutboxMessage(OtherKind, "other-parked"));
        await ParkAllDueAsync(Kind);
        await ParkAllDueAsync(OtherKind);
        Time.Advance(TimeSpan.FromSeconds(1));
        await AddAsync(new OutboxMessage(Kind, "parked-second"));
        await ParkAllDueAsync(Kind);

        await AddAsync(new OutboxMessage(Kind, "completed"));
        var (completed, completedToken) = await ClaimOneAsync();
        Assert.True(await CompleteAsync(completed.Id, completedToken));

        await AddAsync(new OutboxMessage(Kind, "rejected"));
        var (rejected, rejectedToken) = await ClaimOneAsync();
        Assert.True(await RejectAsync(rejected.Id, rejectedToken, "unknown customer"));

        var parked = await ListAsync(new OutboxQuery(OutboxMessageStatus.Parked) { Kind = Kind });
        Assert.Equal(["parked-second", "parked-first"], parked.Select(message => message.RecordRef));
        Assert.All(parked, message =>
        {
            Assert.Equal(OutboxMessageStatus.Parked, message.Status);
            Assert.Equal("down", message.LastError);
            Assert.Equal((1, "down"), (Assert.Single(message.Errors).Attempt, message.Errors[0].Error));
        });

        var rejectedList = await ListAsync(new OutboxQuery(OutboxMessageStatus.Rejected));
        Assert.Equal("rejected", Assert.Single(rejectedList).RecordRef);

        await Assert.ThrowsAsync<ArgumentException>(() => ListAsync(new OutboxQuery(OutboxMessageStatus.Pending)));
    }

    [Fact]
    public async Task Pages_continue_after_the_last_entry_without_skipping_or_repeating()
    {
        await AddAsync(new OutboxMessage(Kind, "older-1"), new OutboxMessage(Kind, "older-2"));
        await ParkAllDueAsync(Kind);
        Time.Advance(TimeSpan.FromSeconds(1));

        // Three finished in the same instant: the ID breaks the tie.
        await AddAsync(new OutboxMessage(Kind, "same-1"), new OutboxMessage(Kind, "same-2"), new OutboxMessage(Kind, "same-3"));
        await ParkAllDueAsync(Kind);

        var seen = new List<OutboxMessageInfo>();
        OutboxMessageInfo? after = null;
        for (var page = 0; page < 3; page++)
        {
            var entries = await ListAsync(new OutboxQuery(OutboxMessageStatus.Parked) { Limit = 2, After = after });
            seen.AddRange(entries);
            after = entries[^1];
        }

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Select(message => message.Id).Distinct().Count());
        Assert.Equal(["same-1", "same-2", "same-3"], seen.Take(3).Select(message => message.RecordRef).Order());
        Assert.Equal(["older-1", "older-2"], seen.Skip(3).Select(message => message.RecordRef).Order());
    }

    [Fact]
    public async Task Retry_and_cancel_apply_only_to_the_statuses_they_are_for()
    {
        await AddAsync(new OutboxMessage(Kind, "waiting"));
        var waiting = await RowAsync("waiting");

        Assert.False(await AdminAsync(admin => admin.RetryAsync(waiting.Id, Ct)));
        Assert.Equivalent(waiting, await RowAsync("waiting"));

        Assert.True(await AdminAsync(admin => admin.CancelAsync(waiting.Id, Ct)));
        var cancelled = await RowAsync("waiting");
        Assert.Equal(OutboxStatus.Cancelled, cancelled.Status);
        Assert.Equal(Time.GetUtcNow(), cancelled.FinishedAt);
        Assert.Empty((await ClaimAsync()).Messages);

        await AddAsync(new OutboxMessage(Kind, "completed"));
        var (completed, token) = await ClaimOneAsync();
        Assert.True(await CompleteAsync(completed.Id, token));

        Assert.False(await AdminAsync(admin => admin.CancelAsync(completed.Id, Ct)));
        Assert.Equal(OutboxStatus.Completed, (await RowAsync("completed")).Status);
    }

    private const string OtherKind = "invoice.file";

    /// <summary>Claims every due message of a kind and fails each with no attempts left, so all park in one instant.</summary>
    private async Task ParkAllDueAsync(string kind)
    {
        var batch = await ClaimAsync(kind, maxAttempts: 1);
        foreach (var message in batch.Messages)
        {
            Assert.Equal(FailOutcome.Parked, await FailAsync(message.Id, batch.Token, "down", maxAttempts: 1));
        }
    }

    private Task<IReadOnlyList<OutboxMessageInfo>> ListAsync(OutboxQuery query) =>
        AdminAsync(admin => admin.ListAsync(query, Ct));

    private Task<T> AdminAsync<T>(Func<IOutboxAdmin, Task<T>> work) =>
        InScopeAsync(scope => work(scope.GetRequiredService<IOutboxAdmin>()));
}
