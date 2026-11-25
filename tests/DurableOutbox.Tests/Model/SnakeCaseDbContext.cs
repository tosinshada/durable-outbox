using Microsoft.EntityFrameworkCore;

namespace DurableOutbox.Tests;

/// <summary>
/// A service's context that is registered with <c>UseSnakeCaseNamingConvention()</c>, which renames every column
/// the model does not name itself. It maps <c>svc.outbox</c>, the table <see cref="TestDbContext"/> migrates.
/// </summary>
public sealed class SnakeCaseDbContext(DbContextOptions<SnakeCaseDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.AddDurableOutbox(schema: "svc", table: "outbox");
}
