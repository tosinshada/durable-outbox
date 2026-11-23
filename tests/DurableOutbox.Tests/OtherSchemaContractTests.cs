using DurableOutbox.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DurableOutbox.Tests;

/// <summary>
/// The kit against <c>contract.messages</c>: another schema and table name, beside a message of the service's
/// own that the kit must leave alone.
/// </summary>
public sealed class OtherSchemaContractTests(PostgresFixture postgres) : ContractTests<ContractDbContext>(postgres)
{
    protected override async Task RunAsync(Func<OutboxContract<ContractDbContext>, Task> guarantee)
    {
        await using var context = new ContractDbContext(
            new DbContextOptionsBuilder<ContractDbContext>().UseNpgsql(ConnectionString).Options);
        await context.Database.MigrateAsync(Ct);

        var id = Guid.CreateVersion7(Time.GetUtcNow());
        await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO contract.messages (id, kind, record_ref, priority, status, attempts, next_attempt_at, created_at, errors)
            VALUES ({0}, 'receipt.send', 'order-1', 0, 'Pending', 0, {1}, {1}, '[]'::jsonb)
            """,
            [id, Time.GetUtcNow()],
            Ct);
        var before = await ServiceMessageAsync(context, id);

        await guarantee(new OutboxContract<ContractDbContext>(services =>
            services.AddDbContext<ContractDbContext>(options => options.UseNpgsql(ConnectionString))));

        Assert.Equivalent(before, await ServiceMessageAsync(context, id));
    }

    private static Task<OutboxMessageRow> ServiceMessageAsync(ContractDbContext context, Guid id) =>
        context.Set<OutboxMessageRow>().AsNoTracking().SingleAsync(row => row.Id == id, Ct);
}
