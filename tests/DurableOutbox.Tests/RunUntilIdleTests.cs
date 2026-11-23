using DurableOutbox.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

// Section 9.12: RunUntilIdleAsync runs a service's handlers to completion in its own integration tests.
public sealed class RunUntilIdleTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    [Fact]
    public async Task The_handlers_run_until_nothing_is_due_without_workers_or_moving_the_clock()
    {
        var handler = new HandlerScript
        {
            Behaviour = call => Task.FromResult(
                call.Delivery.RecordRef == "order-3" ? OutboxResult.Retry("receiver busy") : OutboxResult.Done()),
        };
        await using var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddSingleton(handler)
            .AddDbContext<TestDbContext>(options => options.UseNpgsql(ConnectionString))
            .AddDurableOutbox<TestDbContext>(outbox =>
            {
                outbox.RunWorkers = false;
                outbox.AddHandler<TestHandler>(Kind, kind => kind.MaxConcurrency = 2);
            })
            .BuildServiceProvider();

        await AddAsync(new OutboxMessage(Kind, "order-1"), new OutboxMessage(Kind, "order-2"), new OutboxMessage(Kind, "order-3"));
        var before = Time.GetUtcNow();

        await services.RunUntilIdleAsync<TestDbContext>(Ct);

        Assert.Equal(before, Time.GetUtcNow());
        Assert.Equal(3, handler.Calls.Count);
        Assert.Equal(OutboxStatus.Completed, (await RowAsync("order-1")).Status);
        Assert.Equal(OutboxStatus.Completed, (await RowAsync("order-2")).Status);
        var retried = await RowAsync("order-3");
        Assert.Equal(OutboxStatus.Pending, retried.Status);
        Assert.Equal(1, retried.Attempts);
        Assert.True(retried.NextAttemptAt > Time.GetUtcNow());

        await using var noHandlers = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddDbContext<TestDbContext>(options => options.UseNpgsql(ConnectionString))
            .AddDurableOutbox<TestDbContext>()
            .BuildServiceProvider();
        await Assert.ThrowsAsync<InvalidOperationException>(() => noHandlers.RunUntilIdleAsync<TestDbContext>(Ct));
    }
}
