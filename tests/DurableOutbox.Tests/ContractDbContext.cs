using Microsoft.EntityFrameworkCore;

namespace DurableOutbox.Tests;

/// <summary>A second service's context: only the outbox, in another schema and under another table name.</summary>
public sealed class ContractDbContext(DbContextOptions<ContractDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.AddDurableOutbox(schema: "contract", table: "messages");
}
