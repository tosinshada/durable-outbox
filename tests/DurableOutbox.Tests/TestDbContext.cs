using Microsoft.EntityFrameworkCore;

namespace DurableOutbox.Tests;

/// <summary>A stand-in for a service's own context: one business table and the outbox.</summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().ToTable("orders", "svc");
        modelBuilder.AddDurableOutbox(schema: "svc", table: "outbox");
    }
}
