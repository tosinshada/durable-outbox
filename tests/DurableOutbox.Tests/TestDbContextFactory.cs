using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DurableOutbox.Tests;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model. It never connects.</summary>
public sealed class TestDbContextFactory : IDesignTimeDbContextFactory<TestDbContext>
{
    public TestDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TestDbContext>().UseNpgsql("Host=design-time").Options);
}
