using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

// Section 9.11: the table is compared with the configuration when the host starts.
public sealed class SchemaCheckTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    [Theory]
    [InlineData(
        "ALTER TABLE svc.outbox DROP COLUMN trace_parent; ALTER TABLE svc.outbox ADD COLUMN service_note text",
        "column trace_parent is missing")]
    [InlineData("DROP INDEX svc.ix_outbox_finished", "the operator index on (kind, finished_at)")]
    public async Task A_table_that_does_not_match_stops_the_check_and_nothing_is_claimed(string breakTable, string difference)
    {
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await InScopeAsync(scope => scope.GetRequiredService<TestDbContext>().Database.ExecuteSqlRawAsync(breakTable, Ct));

        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        var mismatch = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Hosted<OutboxSchemaCheck<TestDbContext>>().ExecuteTask!);
        await Hosted<OutboxWorker<TestDbContext>>().ExecuteTask!;
        await StopWorkersAsync();

        // The message names the table and only what differs: an extra column of the service's own is allowed.
        Assert.Contains("table svc.outbox ", mismatch.Message, StringComparison.Ordinal);
        Assert.Contains(difference, mismatch.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("service_note", mismatch.Message, StringComparison.Ordinal);
        Assert.Single(mismatch.Message.Split("; "));
        Assert.Contains(Logs.Entries, entry => entry.Contains(difference, StringComparison.Ordinal));

        var (status, attempts) = Assert.Single(await InScopeAsync(scope =>
            scope.GetRequiredService<TestDbContext>().Database
                .SqlQueryRaw<StatusAndAttempts>("SELECT status AS \"Status\", attempts AS \"Attempts\" FROM svc.outbox")
                .ToListAsync(Ct)));
        Assert.Equal((OutboxStatus.Pending, 0), (status, attempts));
        Assert.Empty(Handler.Calls);
    }

    [Fact]
    public async Task A_database_unreachable_at_start_up_delays_the_check_without_stopping_it()
    {
        await SetDatabaseReachableAsync(false);
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        await AdvanceUntilAsync(() => Task.FromResult(FailedChecks() >= 3), Step);
        Assert.False(Hosted<OutboxSchemaCheck<TestDbContext>>().ExecuteTask!.IsCompleted);

        await SetDatabaseReachableAsync(true);
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await AdvanceUntilAsync(async () => (await RowAsync()).Status == OutboxStatus.Completed, Step);
        await StopWorkersAsync();

        Assert.Single(Handler.Calls);
    }

    private int FailedChecks() =>
        Logs.Entries.Count(entry => entry.Contains("could not be reached for the start-up check", StringComparison.Ordinal));
}
