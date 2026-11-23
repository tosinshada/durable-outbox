using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

public sealed class FollowUpWriteTests(PostgresFixture postgres) : WorkerTestBase(postgres)
{
    [Fact]
    public async Task Follow_up_writes_commit_with_the_completion()
    {
        Handler.Behaviour = call => Task.FromResult(OutboxResult.Done(_ =>
        {
            call.Db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = "follow-up" });
            return Task.CompletedTask;
        }));
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));

        await Handler.CallAsync(0);
        await StopWorkersAsync();

        Assert.Equal(OutboxStatus.Completed, (await RowAsync()).Status);
        Assert.Equal(["follow-up"], (await OrdersAsync()).Select(order => order.Reference));
    }

    // Guarantee 3: a worker whose claim was taken over writes nothing, not even its follow-up writes.
    [Fact]
    public async Task Follow_up_writes_never_run_when_the_lease_was_lost()
    {
        var ran = false;
        Handler.Behaviour = call => call.ReleasedAsync();
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));
        var call = await Handler.CallAsync(0);

        var takenOverBy = await ReplaceTokenAsync(call.Delivery.Id);
        call.Release(OutboxResult.Done(_ =>
        {
            ran = true;
            call.Db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = "follow-up" });
            return Task.CompletedTask;
        }));
        await StopWorkersAsync();

        Assert.False(ran);
        Assert.Empty(await OrdersAsync());
        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(takenOverBy, row.LeaseToken);
    }

    [Fact]
    public async Task Follow_up_writes_that_fail_roll_back_with_the_completion()
    {
        Handler.Behaviour = call => Task.FromResult(OutboxResult.Done(async cancellationToken =>
        {
            call.Db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = "follow-up" });
            await call.Db.SaveChangesAsync(cancellationToken);
            throw new InvalidOperationException("ledger closed");
        }));
        await AddAsync(new OutboxMessage(Kind, "order-1"));
        await StartWorkersAsync(outbox => outbox.AddHandler<TestHandler>(Kind));

        await Handler.CallAsync(0);
        await StopWorkersAsync();

        Assert.Empty(await OrdersAsync());
        var row = await RowAsync();
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Null(row.LeaseToken);
        Assert.Contains("ledger closed", row.LastError, StringComparison.Ordinal);
    }

    private Task<List<Order>> OrdersAsync() =>
        InScopeAsync(scope => scope.GetRequiredService<TestDbContext>().Orders.AsNoTracking().ToListAsync(Ct));
}
