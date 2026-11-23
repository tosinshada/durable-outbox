using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

public sealed class WritingTests(PostgresFixture postgres) : OutboxTestBase(postgres)
{
    // Guarantee 1: a message commits with the business change or not at all.
    [Fact]
    public async Task A_transaction_that_rolls_back_leaves_neither_the_change_nor_the_message()
    {
        await InScopeAsync(async scope =>
        {
            var db = scope.GetRequiredService<TestDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = "order-1" });
            await db.SaveChangesAsync(Ct);
            Assert.True(await scope.GetRequiredService<IOutbox>().AddAsync(new OutboxMessage(Kind, "order-1"), Ct));

            await transaction.RollbackAsync(Ct);
        });

        Assert.Empty(await RowsAsync());
        Assert.Equal(0, await InScopeAsync(scope => scope.GetRequiredService<TestDbContext>().Orders.CountAsync(Ct)));
    }

    // Guarantee 1: writing outside a transaction is refused.
    [Fact]
    public async Task Adding_with_no_transaction_throws_and_writes_nothing()
    {
        await InScopeAsync(async scope =>
        {
            var outbox = scope.GetRequiredService<IOutbox>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => outbox.AddAsync(new OutboxMessage(Kind, "order-1"), Ct));
        });

        Assert.Empty(await RowsAsync());
    }

    // Guarantee 2: a duplicate unique key is not queued, and does not fail the caller's transaction.
    [Fact]
    public async Task A_duplicate_unique_key_is_ignored_and_the_callers_transaction_still_commits()
    {
        await AddAsync(new OutboxMessage(Kind, "order-1") { UniqueKey = "order-1" });

        await InScopeAsync(async scope =>
        {
            var db = scope.GetRequiredService<TestDbContext>();
            var outbox = scope.GetRequiredService<IOutbox>();
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);

            Assert.False(await outbox.AddAsync(new OutboxMessage(Kind, "order-1") { UniqueKey = "order-1" }, Ct));
            Assert.True(await outbox.AddAsync(new OutboxMessage("receipt.void", "order-1") { UniqueKey = "order-1" }, Ct));

            db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = "order-1" });
            await db.SaveChangesAsync(Ct);
            await transaction.CommitAsync(Ct);
        });

        var rows = await RowsAsync();
        Assert.Equal([Kind, "receipt.void"], rows.Select(row => row.Kind).Order());
        Assert.Equal(1, await InScopeAsync(scope => scope.GetRequiredService<TestDbContext>().Orders.CountAsync(Ct)));
    }
}
