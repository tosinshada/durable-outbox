using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DurableOutbox.Tests;

/// <summary>Lets <c>dotnet ef migrations add</c> build the second model. It never connects.</summary>
public sealed class ContractDbContextFactory : IDesignTimeDbContextFactory<ContractDbContext>
{
    public ContractDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ContractDbContext>().UseNpgsql("Host=design-time").Options);
}
