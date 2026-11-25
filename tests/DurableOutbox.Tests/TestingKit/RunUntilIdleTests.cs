using DurableOutbox.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace DurableOutbox.Tests;

// Section 9.12: RunUntilIdleAsync runs a service's handlers to completion in its own integration tests.
public sealed class RunUntilIdleTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    private readonly HandlerScript _handler = new();

    [Fact]
    public async Task The_handlers_run_until_nothing_is_due_without_workers_or_moving_the_clock()
    {
        _handler.Behaviour = call => Task.FromResult(
            call.Delivery.RecordRef == "order-3" ? OutboxResult.Retry("receiver busy") : OutboxResult.Done());
        await using var services = Build(outbox => outbox.AddHandler<TestHandler>(Kind, kind => kind.MaxConcurrency = 2));

        await AddAsync(new OutboxMessage(Kind, "order-1"), new OutboxMessage(Kind, "order-2"), new OutboxMessage(Kind, "order-3"));
        var before = Time.GetUtcNow();

        await services.RunUntilIdleAsync<TestDbContext>(Ct);

        Assert.Equal(before, Time.GetUtcNow());
        Assert.Equal(3, _handler.Calls.Count);
        Assert.Equal(OutboxStatus.Completed, (await RowAsync("order-1")).Status);
        Assert.Equal(OutboxStatus.Completed, (await RowAsync("order-2")).Status);
        var retried = await RowAsync("order-3");
        Assert.Equal(OutboxStatus.Pending, retried.Status);
        Assert.Equal(1, retried.Attempts);
        Assert.True(retried.NextAttemptAt > Time.GetUtcNow());

        await using var noHandlers = Build(_ => { });
        await Assert.ThrowsAsync<InvalidOperationException>(() => noHandlers.RunUntilIdleAsync<TestDbContext>(Ct));
    }

    // Each kind's loop stops by itself once nothing of its kind is due or running. A give-up hook then adds a
    // message for the other kind, whose loop has stopped, and that kind's hook does the same in return.
    [Fact]
    public async Task A_message_that_one_kind_adds_for_another_is_handled_in_the_same_run()
    {
        await using var services = Build(outbox =>
        {
            outbox.AddHandler<RelayGiveUpHandler>(RelayGiveUpHandler.FirstKind, kind => kind.MaxAttempts = 1);
            outbox.AddHandler<RelayGiveUpHandler>(RelayGiveUpHandler.SecondKind, kind => kind.MaxAttempts = 1);
        });
        await AddAsync(new OutboxMessage(RelayGiveUpHandler.FirstKind, "hop-1"));

        await services.RunUntilIdleAsync<TestDbContext>(Ct);

        var rows = await RowsAsync();
        Assert.Equal(RelayGiveUpHandler.Hops, rows.Count);
        Assert.All(rows, row => Assert.Equal(OutboxStatus.Parked, row.Status));
    }

    // The clock is the test's and nothing moves it, so a wait to claim again would never end.
    [Fact]
    public async Task A_claim_that_fails_ends_the_run_with_its_error()
    {
        await using var services = Build(outbox => outbox.AddHandler<TestHandler>(Kind));
        await AddAsync(new OutboxMessage(Kind, "order-1"));

        // The table still matches the start-up check, and every claim of it fails.
        await InScopeAsync(scope => scope.GetRequiredService<TestDbContext>().Database.ExecuteSqlRawAsync(
            """
            CREATE FUNCTION svc.refuse_update() RETURNS trigger LANGUAGE plpgsql AS
                $$ BEGIN RAISE EXCEPTION 'the outbox is read-only'; END $$;
            CREATE TRIGGER refuse_update BEFORE UPDATE ON svc.outbox
                FOR EACH STATEMENT EXECUTE FUNCTION svc.refuse_update();
            """,
            Ct));

        var error = await Assert.ThrowsAsync<PostgresException>(() => services.RunUntilIdleAsync<TestDbContext>(Ct));

        Assert.Equal("the outbox is read-only", error.MessageText);
        Assert.Empty(_handler.Calls);
        var row = await RowAsync();
        Assert.Equal((OutboxStatus.Pending, 0), (row.Status, row.Attempts));
    }

    /// <summary>A test host as a service registers it: the outbox with no workers.</summary>
    private ServiceProvider Build(Action<OutboxOptions> configure) => new ServiceCollection()
        .AddSingleton<TimeProvider>(Time)
        .AddSingleton(_handler)
        .AddDbContext<TestDbContext>(options => options.UseNpgsql(ConnectionString))
        .AddDurableOutbox<TestDbContext>(outbox =>
        {
            outbox.RunWorkers = false;
            configure(outbox);
        })
        .BuildServiceProvider();
}
